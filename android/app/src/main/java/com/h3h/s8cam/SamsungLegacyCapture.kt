package com.h3h.s8cam

import android.graphics.SurfaceTexture
import android.hardware.Camera
import android.opengl.EGL14
import android.opengl.EGLExt
import android.opengl.GLES11Ext
import android.opengl.GLES20
import android.os.Build
import android.os.Handler
import android.os.SystemClock
import android.view.Surface
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Универсальный захват через оригинальный Camera1 API (HAL1) для любых смартфонов + Samsung fast-fps. */
@Suppress("DEPRECATION")
class SamsungLegacyCapture(private val handler: Handler, private val onError: (String) -> Unit) {
    companion object {
        private val SupportedModels = setOf(
            "SM-N960N", "SM-N960F", "SM-N960U", "SM-N9600",
            "SM-G960N", "SM-G960F", "SM-G960U",
            "SM-G965N", "SM-G965F", "SM-G965U",
            "SM-G950F", "SM-G950N", "SM-G955F", "SM-G955N"
        )

        fun parseLegacyCameraIndex(cameraId: String, cameraCount: Int): Int {
            if (cameraCount <= 0) return -1
            val trimmed = cameraId.trim()
            if (trimmed.isEmpty() || trimmed == "auto") return 0
            val raw = if (trimmed.startsWith("legacy:")) trimmed.removePrefix("legacy:") else trimmed
            val index = raw.toIntOrNull() ?: return -1
            return if (index in 0 until cameraCount) index else -1
        }

        fun shouldUseLegacy(
            cameraApi: String,
            forceLegacy: Boolean,
            manufacturer: String,
            model: String,
            cameraId: String,
            physicalId: String?,
            width: Int,
            height: Int,
            camera1Count: Int
        ): Boolean {
            if (cameraApi == "camera2") return false
            val validLegacyCamera = physicalId == null && parseLegacyCameraIndex(cameraId, camera1Count) >= 0
            if (cameraApi == "camera1" || forceLegacy || cameraId.startsWith("legacy:")) {
                return validLegacyCamera
            }
            return manufacturer.equals("samsung", true) &&
                SupportedModels.contains(model) &&
                cameraId == "0" && physicalId == null &&
                width == 1920 && height == 1080
        }

        fun supports(
            cameraId: String,
            physicalId: String?,
            width: Int,
            height: Int,
            forceLegacy: Boolean = false,
            cameraApi: String = "auto"
        ): Boolean {
            val count = try { Camera.getNumberOfCameras() } catch (_: Exception) { 0 }
            return shouldUseLegacy(
                cameraApi = cameraApi,
                forceLegacy = forceLegacy,
                manufacturer = Build.MANUFACTURER ?: "",
                model = Build.MODEL ?: "",
                cameraId = cameraId,
                physicalId = physicalId,
                width = width,
                height = height,
                camera1Count = count
            )
        }

        fun selectBestPreviewSize(supported: List<Pair<Int, Int>>, targetW: Int, targetH: Int): Pair<Int, Int> {
            if (supported.isEmpty()) return targetW to targetH
            supported.firstOrNull { it.first == targetW && it.second == targetH }?.let { return it }
            val targetAspect = targetW.toDouble() / targetH.coerceAtLeast(1)
            val targetArea = targetW.toLong() * targetH.toLong()
            return supported.minByOrNull { (w, h) ->
                val aspectPenalty = (kotlin.math.abs(w.toDouble() / h.coerceAtLeast(1) - targetAspect) * 20_000_000.0).toLong()
                val areaDiff = kotlin.math.abs(w.toLong() * h.toLong() - targetArea)
                aspectPenalty + areaDiff
            } ?: (targetW to targetH)
        }

        fun selectBestFpsRange(supported: List<Pair<Int, Int>>, targetFps: Int): Pair<Int, Int>? {
            if (supported.isEmpty()) return null
            val target = targetFps * 1000
            supported.firstOrNull { it.first == target && it.second == target }?.let { return it }
            supported.filter { it.second == target }
                .maxByOrNull { it.first }
                ?.let { return it }
            supported.filter { target in it.first..it.second }
                .minWithOrNull(compareBy<Pair<Int, Int>> { it.second - it.first }.thenByDescending { it.first })
                ?.let { return it }
            return supported.filter { it.second >= target }
                .minByOrNull { it.second - target }
                ?: supported.maxByOrNull { it.second }
        }
    }

    private val isSamsung = Build.MANUFACTURER.equals("samsung", true)
    private var outputWidth = 1920
    private var outputHeight = 1080
    private var previewWidth = 1920
    private var previewHeight = 1080
    private var cameraIndex = 0

    private var camera: Camera? = null
    private var texture: SurfaceTexture? = null
    private var display = EGL14.EGL_NO_DISPLAY
    private var context = EGL14.EGL_NO_CONTEXT
    private var window = EGL14.EGL_NO_SURFACE
    private var program = 0
    private var textureId = 0
    private var closed = false
    private var faceDetectionRunning = false
    private var lastFaceProcessMs = 0L
    private var cropRectLoc = -1
    private var cameraLoc = -1
    private var transformLoc = -1
    private var posLoc = -1
    private var uvLoc = -1
    @Volatile private var activeSettings: StreamSettings? = null
    @Volatile private var currentCx = 0.5f
    @Volatile private var currentCy = 0.5f
    @Volatile private var currentZoom = 1.0f
    @Volatile private var targetCx = 0.5f
    @Volatile private var targetCy = 0.5f
    @Volatile private var targetZoom = 1.0f
    @Volatile private var lastFaceSeenMs = 0L
    @Volatile private var lastDrawTimeMs = 0L
    @Volatile private var faceX = 0.5f
    @Volatile private var faceY = 0.5f
    @Volatile private var faceW = 0.0f
    @Volatile private var faceH = 0.0f
    @Volatile private var velCx = 0.0f
    @Volatile private var velCy = 0.0f
    @Volatile private var velZoom = 0.0f
    @Volatile private var currentAppliedFastFps = 1
    @Volatile private var faceCount = 0
    @Volatile private var skipAlternateFrame = false
    @Volatile private var lastEglPtsNs = 0L

    fun getFaceInfo(): FloatArray {
        val scale = 1.0f / currentZoom.coerceIn(1.0f, 4.0f)
        val halfSpan = 0.5f * scale
        val safeCx = currentCx.coerceIn(halfSpan, 1.0f - halfSpan)
        val safeCy = currentCy.coerceIn(halfSpan, 1.0f - halfSpan)
        return floatArrayOf(faceX, faceY, faceW, faceH, safeCx, safeCy, currentZoom, faceCount.toFloat())
    }
    @Volatile private var lastEncodedFrameTimeMs = 0L
    private val transform = FloatArray(16)
    private val vertices = ByteBuffer.allocateDirect(16 * 4).order(ByteOrder.nativeOrder()).asFloatBuffer().apply {
        put(floatArrayOf(-1f, -1f, 0f, 0f, 1f, -1f, 1f, 0f, -1f, 1f, 0f, 1f, 1f, 1f, 1f, 1f)); position(0)
    }

    private fun updateFaceTracking(c: Camera, faceTracking: Boolean, autoFraming: Boolean, lockAeAwb: Boolean) {
        try {
            val maxFaces = c.parameters.maxNumDetectedFaces
            if (maxFaces <= 0) return
            val shouldRun = faceTracking || autoFraming
            if (shouldRun && !faceDetectionRunning) {
                c.setFaceDetectionListener { faces, cam ->
                    if (faces.isNotEmpty()) {
                        val now = SystemClock.uptimeMillis()
                        // Responsive face detection handling (~20 Hz) at both 30 and 60 FPS
                        if (now - lastFaceProcessMs < 50) return@setFaceDetectionListener
                        lastFaceProcessMs = now

                        val validFaces = faces.filter { it.score >= 25 }
                        if (validFaces.isNotEmpty()) {
                            lastFaceSeenMs = now
                            faceCount = validFaces.size

                            val targetFaceX: Float
                            val targetFaceY: Float
                            val targetFaceW: Float
                            val targetFaceH: Float
                            val desiredZoom: Float
                            val maxZoom = (activeSettings?.autoFramingZoom ?: 1.35f).coerceIn(1.05f, 2.5f)
                            val deadzone = (activeSettings?.autoFramingDeadzone ?: 0.05f).coerceIn(0.01f, 0.20f)

                            if (validFaces.size == 1) {
                                val best = validFaces[0]
                                val rawFaceX = (best.rect.centerX() + 1000f) / 2000f
                                val rawFaceY = 1.0f - (best.rect.centerY() + 1000f) / 2000f
                                val faceHeightNorm = (Math.abs(best.rect.height()).toFloat() / 2000f).coerceIn(0.02f, 1.0f)
                                val faceWidthNorm = (Math.abs(best.rect.width()).toFloat() / 2000f).coerceIn(0.02f, 1.0f)

                                targetFaceX = rawFaceX
                                targetFaceY = rawFaceY
                                targetFaceW = faceWidthNorm
                                targetFaceH = faceHeightNorm

                                desiredZoom = when {
                                    faceHeightNorm > 0.45f -> 1.05f
                                    faceHeightNorm < 0.10f -> maxZoom
                                    else -> (maxZoom * (0.25f / faceHeightNorm)).coerceIn(1.05f, maxZoom)
                                }
                            } else {
                                // Group Framing: encompass all detected faces
                                val minLeft = validFaces.minOf { it.rect.left }
                                val maxRight = validFaces.maxOf { it.rect.right }
                                val minTop = validFaces.minOf { it.rect.top }
                                val maxBottom = validFaces.maxOf { it.rect.bottom }

                                val groupCenterX = (minLeft + maxRight) * 0.5f
                                val groupCenterY = (minTop + maxBottom) * 0.5f

                                val rawFaceX = (groupCenterX + 1000f) / 2000f
                                val rawFaceY = 1.0f - (groupCenterY + 1000f) / 2000f
                                val groupWidthNorm = (Math.abs(maxRight - minLeft).toFloat() / 2000f).coerceIn(0.05f, 1.0f)
                                val groupHeightNorm = (Math.abs(maxBottom - minTop).toFloat() / 2000f).coerceIn(0.05f, 1.0f)

                                targetFaceX = rawFaceX
                                targetFaceY = rawFaceY
                                targetFaceW = groupWidthNorm
                                targetFaceH = groupHeightNorm

                                val spanZoomW = 1.0f / (groupWidthNorm * 1.45f)
                                val spanZoomH = 1.0f / (groupHeightNorm * 1.45f)
                                val groupFitZoom = Math.min(spanZoomW, spanZoomH)
                                desiredZoom = groupFitZoom.coerceIn(1.02f, maxZoom)
                            }

                            faceX = targetFaceX
                            faceY = targetFaceY
                            faceW = targetFaceW
                            faceH = targetFaceH

                            if (activeSettings?.autoFraming == true) {
                                val dx = targetFaceX - currentCx
                                val dy = targetFaceY - currentCy
                                val dist = Math.sqrt((dx * dx + dy * dy).toDouble()).toFloat()
                                if (dist > deadzone) {
                                    targetCx = targetFaceX
                                    val scale = 1.0f / desiredZoom
                                    targetCy = (targetFaceY - 0.04f * scale).coerceIn(0.1f, 0.9f)
                                }
                                if (Math.abs(desiredZoom - currentZoom) > (deadzone * 0.8f)) {
                                    targetZoom = desiredZoom
                                }
                            }

                            // Continuous video focus tracks focus smoothly without freezing ISP registers.
                            // Tap-to-focus remains available for explicit manual touch focus.
                        } else {
                            faceCount = 0
                        }
                    } else {
                        faceCount = 0
                    }
                }
                c.startFaceDetection()
                faceDetectionRunning = true
                android.util.Log.i("S8Cam", "Samsung HAL1 Face Detection started (max $maxFaces faces)")
            } else if (!shouldRun && faceDetectionRunning) {
                c.stopFaceDetection()
                c.setFaceDetectionListener(null)
                faceDetectionRunning = false
                targetCx = 0.5f
                targetCy = 0.5f
                targetZoom = 1.0f
                android.util.Log.i("S8Cam", "Samsung HAL1 Face Detection stopped")
            }
        } catch (e: Exception) {
            android.util.Log.w("S8Cam", "Face Detection toggle failed: ${e.message}")
        }
    }

    private fun applyFps(p: Camera.Parameters, effectiveFps: Int) {
        val supported = p.supportedPreviewFpsRange?.mapNotNull {
            if (it != null && it.size >= 2) it[0] to it[1] else null
        }.orEmpty()
        val chosen = selectBestFpsRange(supported, effectiveFps)
        if (isSamsung && previewWidth == 1920 && previewHeight == 1080) {
            if (effectiveFps >= 60) {
                p.set("fast-fps-mode", 1)
                p.set("preview-fps-range", "60000,60000")
                p.set("preview-frame-rate", 60)
                try { p.setPreviewFpsRange(60000, 60000) } catch (_: Exception) {}
                try { p.setPreviewFrameRate(60) } catch (_: Exception) {}
                return
            } else {
                p.set("fast-fps-mode", 0)
            }
        }
        val hsrValues = p.get("video-hsr-values") ?: ""
        val hfrValues = p.get("video-hfr-values") ?: ""
        if (effectiveFps >= 60 && hsrValues.split(',').any { it.trim() == "60" }) {
            try { p.set("video-hsr", "60") } catch (_: Exception) {}
        } else if (hsrValues.isNotEmpty()) {
            try { p.set("video-hsr", "off") } catch (_: Exception) {}
        }
        if (effectiveFps >= 60 && hsrValues.isEmpty() && hfrValues.split(',').any { it.trim() == "60" }) {
            try { p.set("video-hfr", "60") } catch (_: Exception) {}
        } else if (hfrValues.isNotEmpty()) {
            try { p.set("video-hfr", "off") } catch (_: Exception) {}
        }
        if (chosen != null) {
            try { p.setPreviewFpsRange(chosen.first, chosen.second) } catch (_: Exception) {}
            try { p.set("preview-fps-range", "${chosen.first},${chosen.second}") } catch (_: Exception) {}
            try { p.setPreviewFrameRate((chosen.second / 1000).coerceAtLeast(15)) } catch (_: Exception) {}
        } else {
            try { p.setPreviewFpsRange(effectiveFps * 1000, effectiveFps * 1000) } catch (_: Exception) {}
            try { p.setPreviewFrameRate(effectiveFps) } catch (_: Exception) {}
        }
    }

    private fun applyParametersSafe(c: Camera, p: Camera.Parameters, effectiveFps: Int, settings: StreamSettings) {
        try {
            c.parameters = p
        } catch (ex: Exception) {
            android.util.Log.w("S8Cam", "Camera1 full setParameters failed (${ex.message}), retrying standard params")
            val clean = c.parameters
            clean.setRecordingHint(true)
            clean.setPreviewSize(previewWidth, previewHeight)
            val supported = clean.supportedPreviewFpsRange?.mapNotNull {
                if (it != null && it.size >= 2) it[0] to it[1] else null
            }.orEmpty()
            selectBestFpsRange(supported, effectiveFps)?.let { (minFps, maxFps) ->
                try { clean.setPreviewFpsRange(minFps, maxFps) } catch (_: Exception) {}
            }
            val focus = when (settings.focus) {
                "infinity" -> Camera.Parameters.FOCUS_MODE_INFINITY
                "auto" -> Camera.Parameters.FOCUS_MODE_AUTO
                else -> Camera.Parameters.FOCUS_MODE_CONTINUOUS_VIDEO
            }
            if (clean.supportedFocusModes?.contains(focus) == true) clean.focusMode = focus
            clean.exposureCompensation = settings.exposure.coerceIn(clean.minExposureCompensation, clean.maxExposureCompensation)
            c.parameters = clean
        }
    }

    fun start(
        surface: Surface,
        settings: StreamSettings,
        cameraId: String = "0",
        width: Int = settings.width,
        height: Int = settings.height,
        targetFps: Int = settings.fps
    ) {
        try {
            activeSettings = settings
            outputWidth = width.coerceAtLeast(320)
            outputHeight = height.coerceAtLeast(240)
            val totalCameras = Camera.getNumberOfCameras()
            cameraIndex = parseLegacyCameraIndex(cameraId, totalCameras).coerceAtLeast(0)
            display = EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY)
            check(EGL14.eglInitialize(display, null, 0, null, 0)) { "EGL initialize" }
            val configs = arrayOfNulls<android.opengl.EGLConfig>(1)
            val count = IntArray(1)
            check(EGL14.eglChooseConfig(display, intArrayOf(
                EGL14.EGL_RED_SIZE, 8, EGL14.EGL_GREEN_SIZE, 8, EGL14.EGL_BLUE_SIZE, 8,
                EGL14.EGL_RENDERABLE_TYPE, EGL14.EGL_OPENGL_ES2_BIT, EGL14.EGL_SURFACE_TYPE, EGL14.EGL_WINDOW_BIT,
                0x3142, 1, EGL14.EGL_NONE), 0, configs, 0, 1, count, 0) && count[0] > 0)
            context = EGL14.eglCreateContext(display, configs[0], EGL14.EGL_NO_CONTEXT,
                intArrayOf(EGL14.EGL_CONTEXT_CLIENT_VERSION, 2, EGL14.EGL_NONE), 0)
            window = EGL14.eglCreateWindowSurface(display, configs[0], surface, intArrayOf(EGL14.EGL_NONE), 0)
            check(EGL14.eglMakeCurrent(display, window, window, context)) { "EGL encoder surface" }
            val vertex = shader(GLES20.GL_VERTEX_SHADER,
                "attribute vec2 pos; attribute vec2 uv; uniform mat4 transform; uniform vec4 cropRect; varying vec2 tex; void main(){gl_Position=vec4(pos,0.,1.); vec2 framedUv=(uv-vec2(0.5,0.5))*cropRect.zw+cropRect.xy; tex=(transform*vec4(framedUv,0.,1.)).xy;}")
            val fragment = shader(GLES20.GL_FRAGMENT_SHADER,
                "#extension GL_OES_EGL_image_external : require\nprecision mediump float; varying vec2 tex; uniform samplerExternalOES camera; void main(){gl_FragColor=texture2D(camera,tex);}")
            program = GLES20.glCreateProgram()
            GLES20.glAttachShader(program, vertex); GLES20.glAttachShader(program, fragment)
            GLES20.glLinkProgram(program)
            GLES20.glDeleteShader(vertex); GLES20.glDeleteShader(fragment)
            val linked = IntArray(1)
            GLES20.glGetProgramiv(program, GLES20.GL_LINK_STATUS, linked, 0)
            check(linked[0] != 0) { GLES20.glGetProgramInfoLog(program) }
            cropRectLoc = GLES20.glGetUniformLocation(program, "cropRect")
            cameraLoc = GLES20.glGetUniformLocation(program, "camera")
            transformLoc = GLES20.glGetUniformLocation(program, "transform")
            posLoc = GLES20.glGetAttribLocation(program, "pos")
            uvLoc = GLES20.glGetAttribLocation(program, "uv")
            val ids = IntArray(1)
            GLES20.glGenTextures(1, ids, 0); textureId = ids[0]
            GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, textureId)
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_LINEAR)
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR)
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_S, GLES20.GL_CLAMP_TO_EDGE)
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_T, GLES20.GL_CLAMP_TO_EDGE)
            camera = Camera.open(cameraIndex)
            val c = requireNotNull(camera)
            val p = c.parameters
            val supportedSizes = p.supportedPreviewSizes?.map { it.width to it.height }.orEmpty()
            val chosenSize = selectBestPreviewSize(supportedSizes, outputWidth, outputHeight)
            previewWidth = chosenSize.first
            previewHeight = chosenSize.second
            texture = SurfaceTexture(textureId).apply {
                setDefaultBufferSize(previewWidth, previewHeight)
                setOnFrameAvailableListener({ draw() }, handler)
            }
            p.setRecordingHint(true)
            p.setPreviewSize(previewWidth, previewHeight)
            try { p.set("video-size", "${previewWidth}x${previewHeight}") } catch (_: Exception) {}
            val effectiveFps = if (settings.powerMode == "saving") minOf(targetFps, 30) else targetFps.coerceIn(15, 60)
            currentAppliedFastFps = if (effectiveFps >= 60) 1 else 0
            applyFps(p, effectiveFps)
            val focus = when (settings.focus) {
                "infinity" -> Camera.Parameters.FOCUS_MODE_INFINITY
                "auto" -> Camera.Parameters.FOCUS_MODE_AUTO
                else -> Camera.Parameters.FOCUS_MODE_CONTINUOUS_VIDEO
            }
            if (p.supportedFocusModes?.contains(focus) == true) p.focusMode = focus
            val wb = if (settings.manualWbKelvin > 0) {
                when (settings.manualWbKelvin) {
                    in 2000..3500 -> Camera.Parameters.WHITE_BALANCE_INCANDESCENT
                    in 3501..4500 -> Camera.Parameters.WHITE_BALANCE_FLUORESCENT
                    in 4501..5800 -> Camera.Parameters.WHITE_BALANCE_DAYLIGHT
                    else -> "cloudy-daylight"
                }
            } else when (settings.wb) { "cloudy" -> "cloudy-daylight"; else -> settings.wb }
            if (p.supportedWhiteBalance?.contains(wb) == true) p.whiteBalance = wb
            if (settings.shutterSpeedNs in 19_000_000L..21_000_000L) {
                if (p.supportedAntibanding?.contains(Camera.Parameters.ANTIBANDING_50HZ) == true)
                    p.antibanding = Camera.Parameters.ANTIBANDING_50HZ
            } else if (settings.shutterSpeedNs in 15_000_000L..18_000_000L) {
                if (p.supportedAntibanding?.contains(Camera.Parameters.ANTIBANDING_60HZ) == true)
                    p.antibanding = Camera.Parameters.ANTIBANDING_60HZ
            }
            if (settings.manualIso > 0) {
                try {
                    p.set("iso", "ISO" + settings.manualIso)
                    p.set("iso-speed", settings.manualIso.toString())
                } catch (_: Exception) {}
            }
            p.exposureCompensation = settings.exposure.coerceIn(p.minExposureCompensation, p.maxExposureCompensation)
            if (settings.torch && p.supportedFlashModes?.contains(Camera.Parameters.FLASH_MODE_TORCH) == true) {
                p.flashMode = Camera.Parameters.FLASH_MODE_TORCH
                StreamStats.controls += "Подсветка (Camera1). "
            }
            val isSuperSteady = settings.stabilization && settings.stabilizationMode == "strong"
            if (!settings.autoFraming && settings.zoom > 1.0f && p.isZoomSupported) {
                val maxZoom = p.maxZoom
                val target = ((settings.zoom - 1.0f) * (maxZoom / 4.0f)).toInt().coerceIn(0, maxZoom)
                p.zoom = target
                StreamStats.controls += "Зум ${settings.zoom}x (Camera1). "
            } else if (!settings.autoFraming && isSuperSteady && p.isZoomSupported) {
                p.zoom = (p.maxZoom * 0.08f).toInt().coerceAtLeast(1)
            } else if (p.isZoomSupported) {
                p.zoom = 0
            }
            if (p.isAutoExposureLockSupported) {
                p.autoExposureLock = settings.lockAeAwb
            }
            if (p.isAutoWhiteBalanceLockSupported) {
                p.autoWhiteBalanceLock = settings.lockAeAwb
            }
            if (p.isVideoStabilizationSupported) {
                p.videoStabilization = settings.stabilization && effectiveFps < 60
            }
            if (isSamsung && settings.stabilization && effectiveFps < 60) {
                try { p.set("vdis", if (isSuperSteady) "on" else "off") } catch (_: Exception) {}
                try { p.set("video-stabilization-mode", if (isSuperSteady) "1" else "0") } catch (_: Exception) {}
            }
            applyParametersSafe(c, p, effectiveFps, settings)
            val accepted = c.parameters.previewSize
            if (accepted != null && (accepted.width != previewWidth || accepted.height != previewHeight)) {
                previewWidth = accepted.width
                previewHeight = accepted.height
                texture?.setDefaultBufferSize(previewWidth, previewHeight)
            }
            c.setErrorCallback { error, _ -> if (!closed) onError("Camera1 error $error") }
            c.setPreviewTexture(texture)
            c.startPreview()
            if (settings.focus == "auto") c.autoFocus { _, _ -> }
            val allowFace = if (effectiveFps >= 60 && !isSamsung) settings.autoFraming else settings.faceTracking
            updateFaceTracking(c, allowFace, settings.autoFraming, settings.lockAeAwb)
            val apiTag = if (isSamsung && previewWidth == 1920 && previewHeight == 1080) "Samsung HAL1" else "Camera1 API"
            StreamStats.controls += "$apiTag · ${previewWidth}x${previewHeight}@${effectiveFps}fps; фокус ${c.parameters.focusMode}. "
            if (settings.focus == "manual") StreamStats.controls += "Ручная дистанция недоступна в Camera1; автофокус. "
            StreamStats.state = "Камера запущена"
        } catch (e: Exception) { close(); throw e }
    }

    fun updateControls(settings: StreamSettings) {
        val c = camera ?: return
        try {
            activeSettings = settings
            val p = c.parameters
            p.setRecordingHint(true)
            p.setPreviewSize(previewWidth, previewHeight)
            try { p.set("video-size", "${previewWidth}x${previewHeight}") } catch (_: Exception) {}
            val effectiveFps = if (settings.powerMode == "saving") minOf(settings.fps, 30) else settings.fps.coerceIn(15, 60)
            val desiredFastFps = if (effectiveFps >= 60) 1 else 0
            val fpsToggled = currentAppliedFastFps != desiredFastFps
            applyFps(p, effectiveFps)
            val focus = when (settings.focus) {
                "infinity" -> Camera.Parameters.FOCUS_MODE_INFINITY
                "auto" -> Camera.Parameters.FOCUS_MODE_AUTO
                else -> Camera.Parameters.FOCUS_MODE_CONTINUOUS_VIDEO
            }
            if (p.supportedFocusModes?.contains(focus) == true) p.focusMode = focus
            val wb = if (settings.manualWbKelvin > 0) {
                when (settings.manualWbKelvin) {
                    in 2000..3500 -> Camera.Parameters.WHITE_BALANCE_INCANDESCENT
                    in 3501..4500 -> Camera.Parameters.WHITE_BALANCE_FLUORESCENT
                    in 4501..5800 -> Camera.Parameters.WHITE_BALANCE_DAYLIGHT
                    else -> "cloudy-daylight"
                }
            } else when (settings.wb) { "cloudy" -> "cloudy-daylight"; else -> settings.wb }
            if (p.supportedWhiteBalance?.contains(wb) == true) p.whiteBalance = wb

            if (settings.shutterSpeedNs in 19_000_000L..21_000_000L) {
                if (p.supportedAntibanding?.contains(Camera.Parameters.ANTIBANDING_50HZ) == true)
                    p.antibanding = Camera.Parameters.ANTIBANDING_50HZ
            } else if (settings.shutterSpeedNs in 15_000_000L..18_000_000L) {
                if (p.supportedAntibanding?.contains(Camera.Parameters.ANTIBANDING_60HZ) == true)
                    p.antibanding = Camera.Parameters.ANTIBANDING_60HZ
            } else {
                if (p.supportedAntibanding?.contains(Camera.Parameters.ANTIBANDING_AUTO) == true)
                    p.antibanding = Camera.Parameters.ANTIBANDING_AUTO
            }

            p.exposureCompensation = settings.exposure.coerceIn(p.minExposureCompensation, p.maxExposureCompensation)
            if (p.supportedFlashModes != null) {
                p.flashMode = if (settings.torch && p.supportedFlashModes.contains(Camera.Parameters.FLASH_MODE_TORCH))
                    Camera.Parameters.FLASH_MODE_TORCH else Camera.Parameters.FLASH_MODE_OFF
            }
            val isSuperSteady = settings.stabilization && settings.stabilizationMode == "strong"
            if (!settings.autoFraming && settings.zoom > 1.0f && p.isZoomSupported) {
                val maxZoom = p.maxZoom
                val target = ((settings.zoom - 1.0f) * (maxZoom / 4.0f)).toInt().coerceIn(0, maxZoom)
                p.zoom = target
            } else if (!settings.autoFraming && isSuperSteady && p.isZoomSupported) {
                p.zoom = (p.maxZoom * 0.08f).toInt().coerceAtLeast(1)
            } else if (p.isZoomSupported) {
                p.zoom = 0
            }
            if (p.isAutoExposureLockSupported) {
                p.autoExposureLock = settings.lockAeAwb
            }
            if (p.isAutoWhiteBalanceLockSupported) {
                p.autoWhiteBalanceLock = settings.lockAeAwb
            }
            if (p.isVideoStabilizationSupported) {
                p.videoStabilization = settings.stabilization && effectiveFps < 60
            }
            if (isSamsung && settings.stabilization && effectiveFps < 60) {
                try { p.set("vdis", if (isSuperSteady) "on" else "off") } catch (_: Exception) {}
                try { p.set("video-stabilization-mode", if (isSuperSteady) "1" else "0") } catch (_: Exception) {}
            }
            if (settings.manualIso > 0) {
                try {
                    p.set("iso", "ISO" + settings.manualIso)
                    p.set("iso-speed", settings.manualIso.toString())
                } catch (_: Exception) {}
            } else {
                try {
                    p.set("iso", "auto")
                    p.set("iso-speed", "auto")
                } catch (_: Exception) {}
            }
            if (fpsToggled) {
                currentAppliedFastFps = desiredFastFps
                try {
                    if (faceDetectionRunning) {
                        try { c.stopFaceDetection(); c.setFaceDetectionListener(null) } catch (_: Exception) {}
                        faceDetectionRunning = false
                    }
                    c.stopPreview()
                    applyParametersSafe(c, p, effectiveFps, settings)
                    c.setPreviewTexture(texture)
                    c.startPreview()
                    android.util.Log.i("S8Cam", "Camera1 dynamic FPS switched to $effectiveFps fps")
                } catch (e: Exception) {
                    android.util.Log.w("S8Cam", "Failed to restart preview on FPS switch: ${e.message}")
                    applyParametersSafe(c, p, effectiveFps, settings)
                }
            } else {
                applyParametersSafe(c, p, effectiveFps, settings)
            }
            if (settings.focus == "auto") c.autoFocus { _, _ -> }
            val allowFace = if (effectiveFps >= 60 && !isSamsung) settings.autoFraming else settings.faceTracking
            updateFaceTracking(c, allowFace, settings.autoFraming, settings.lockAeAwb)
            android.util.Log.i("S8Cam", "Samsung HAL1 updateControls: torch=${p.flashMode}, zoom=${p.zoom}, autoFraming=${settings.autoFraming}, aeLock=${p.autoExposureLock}, fps-range=${p.get("preview-fps-range")}")
        } catch (e: Exception) {
            android.util.Log.e("S8Cam", "Samsung HAL1 updateControls failed: ${e.message}", e)
        }
    }

    fun triggerTapToFocus(normX: Float, normY: Float) {
        val c = camera ?: return
        try {
            val p = c.parameters
            p.setRecordingHint(true)
            p.setPreviewSize(previewWidth, previewHeight)
            try { p.set("video-size", "${previewWidth}x${previewHeight}") } catch (_: Exception) {}
            val effectiveFps = if (activeSettings?.powerMode == "saving") minOf(activeSettings?.fps ?: 30, 30) else (activeSettings?.fps ?: 30).coerceIn(15, 60)
            applyFps(p, effectiveFps)
            if (p.maxNumFocusAreas > 0) {
                val x = (normX.coerceIn(0.05f, 0.95f) * 2000 - 1000).toInt()
                val y = (normY.coerceIn(0.05f, 0.95f) * 2000 - 1000).toInt()
                val box = 150
                val rect = android.graphics.Rect(
                    (x - box).coerceIn(-1000, 800),
                    (y - box).coerceIn(-1000, 800),
                    (x + box).coerceIn(-800, 1000),
                    (y + box).coerceIn(-800, 1000)
                )
                p.focusAreas = listOf(Camera.Area(rect, 1000))
                if (p.maxNumMeteringAreas > 0) {
                    p.meteringAreas = listOf(Camera.Area(rect, 1000))
                }
                if (p.supportedFocusModes?.contains(Camera.Parameters.FOCUS_MODE_AUTO) == true) {
                    p.focusMode = Camera.Parameters.FOCUS_MODE_AUTO
                }
                c.parameters = p
                c.autoFocus { _, _ -> }
                android.util.Log.i("S8Cam", "Samsung HAL1 tapToFocus & spot metering at ($x, $y)")
            }
        } catch (e: Exception) {
            android.util.Log.w("S8Cam", "Samsung HAL1 tapToFocus failed: ${e.message}")
        }
    }

    private fun draw() {
        if (closed) return
        try {
            val t = texture ?: return
            check(EGL14.eglMakeCurrent(display, window, window, context))
            t.updateTexImage(); t.getTransformMatrix(transform)

            val now = SystemClock.uptimeMillis()
            val effectiveFps = if (activeSettings?.powerMode == "saving" || (activeSettings?.fps ?: 60) <= 30) 30 else 60
            // If camera is running in 60 fps fast-mode, but effective output is 30 fps: cleanly drop alternate frames
            if (currentAppliedFastFps == 1 && effectiveFps <= 30) {
                skipAlternateFrame = !skipAlternateFrame
                if (skipAlternateFrame) return
            }

            GLES20.glViewport(0, 0, outputWidth, outputHeight)
            GLES20.glUseProgram(program)
            GLES20.glActiveTexture(GLES20.GL_TEXTURE0)
            GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, textureId)
            GLES20.glUniform1i(cameraLoc, 0)
            GLES20.glUniformMatrix4fv(transformLoc, 1, false, transform, 0)

            val dt = if (lastDrawTimeMs > 0) ((now - lastDrawTimeMs).coerceIn(5, 100) / 1000f) else (1f / effectiveFps.toFloat())
            lastDrawTimeMs = now

            val isAutoFraming = activeSettings?.autoFraming == true
            if (isAutoFraming) {
                if (now - lastFaceSeenMs > 2500) {
                    targetCx = 0.5f
                    targetCy = 0.5f
                    targetZoom = 1.0f
                }
            } else {
                targetCx = 0.5f
                targetCy = 0.5f
                targetZoom = 1.0f
            }

            // Exact closed-form critically damped spring (unconditionally stable and smooth at any FPS/dt)
            val speedMult = (activeSettings?.autoFramingSpeed ?: 1.0f).coerceIn(0.2f, 3.0f)
            val omega = 4.2f * speedMult
            val expTerm = Math.exp((-omega * dt).toDouble()).toFloat()

            val dCx = currentCx - targetCx
            val tempCx = (velCx + omega * dCx) * dt
            currentCx = targetCx + (dCx + tempCx) * expTerm
            velCx = (velCx - omega * tempCx) * expTerm

            val dCy = currentCy - targetCy
            val tempCy = (velCy + omega * dCy) * dt
            currentCy = targetCy + (dCy + tempCy) * expTerm
            velCy = (velCy - omega * tempCy) * expTerm

            val dZoom = currentZoom - targetZoom
            val tempZoom = (velZoom + omega * dZoom) * dt
            currentZoom = targetZoom + (dZoom + tempZoom) * expTerm
            velZoom = (velZoom - omega * tempZoom) * expTerm

            val scale = 1.0f / currentZoom.coerceIn(1.0f, 4.0f)
            val halfSpan = 0.5f * scale
            val safeCx = currentCx.coerceIn(halfSpan, 1.0f - halfSpan)
            val safeCy = currentCy.coerceIn(halfSpan, 1.0f - halfSpan)

            if (cropRectLoc >= 0) {
                GLES20.glUniform4f(cropRectLoc, safeCx, safeCy, scale, scale)
            }

            vertices.position(0); GLES20.glVertexAttribPointer(posLoc, 2, GLES20.GL_FLOAT, false, 16, vertices)
            vertices.position(2); GLES20.glVertexAttribPointer(uvLoc, 2, GLES20.GL_FLOAT, false, 16, vertices)
            GLES20.glEnableVertexAttribArray(posLoc); GLES20.glEnableVertexAttribArray(uvLoc)
            GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4)
            val rawPts = t.timestamp
            val pts = CaptureTimestamp.next(rawPts,lastEglPtsNs)
            lastEglPtsNs = pts
            check(EGLExt.eglPresentationTimeANDROID(display, window, pts))
            check(EGL14.eglSwapBuffers(display, window)) { "EGL swap" }
        } catch (e: Exception) { if (!closed) { closed = true; onError("Samsung frame: ${e.message}") } }
    }

    private fun shader(type: Int, source: String): Int {
        val shader = GLES20.glCreateShader(type)
        GLES20.glShaderSource(shader, source); GLES20.glCompileShader(shader)
        val compiled = IntArray(1)
        GLES20.glGetShaderiv(shader, GLES20.GL_COMPILE_STATUS, compiled, 0)
        check(compiled[0] != 0) { GLES20.glGetShaderInfoLog(shader) }
        return shader
    }

    fun close() {
        closed = true
        lastEglPtsNs = 0L
        if (faceDetectionRunning) {
            try { camera?.stopFaceDetection(); camera?.setFaceDetectionListener(null) } catch (_: Exception) {}
            faceDetectionRunning = false
        }
        try { camera?.setPreviewCallback(null); camera?.stopPreview() } catch (_: Exception) {}
        camera?.release(); camera = null
        texture?.setOnFrameAvailableListener(null); texture?.release(); texture = null
        if (display != EGL14.EGL_NO_DISPLAY) {
            if (context != EGL14.EGL_NO_CONTEXT && window != EGL14.EGL_NO_SURFACE) {
                EGL14.eglMakeCurrent(display, window, window, context)
                GLES20.glDeleteProgram(program); GLES20.glDeleteTextures(1, intArrayOf(textureId), 0)
            }
            EGL14.eglMakeCurrent(display, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_CONTEXT)
            if (window != EGL14.EGL_NO_SURFACE) EGL14.eglDestroySurface(display, window)
            if (context != EGL14.EGL_NO_CONTEXT) EGL14.eglDestroyContext(display, context)
            EGL14.eglTerminate(display); EGL14.eglReleaseThread()
            display = EGL14.EGL_NO_DISPLAY
        }
    }
}
