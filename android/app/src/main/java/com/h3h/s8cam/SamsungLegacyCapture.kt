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

/** Verified Samsung HAL1 fast-fps path; other phones continue using Camera2. */
@Suppress("DEPRECATION")
class SamsungLegacyCapture(private val handler: Handler, private val onError: (String) -> Unit) {
    companion object {
        private val SupportedModels = setOf(
            "SM-N960N", "SM-N960F", "SM-N960U", "SM-N9600",
            "SM-G960N", "SM-G960F", "SM-G960U",
            "SM-G965N", "SM-G965F", "SM-G965U",
            "SM-G950F", "SM-G950N", "SM-G955F", "SM-G955N"
        )

        fun supports(cameraId: String, physicalId: String?, width: Int, height: Int, forceLegacy: Boolean = false) =
            Build.MANUFACTURER.equals("samsung", true) &&
                (forceLegacy || SupportedModels.contains(Build.MODEL)) &&
                cameraId == "0" && physicalId == null &&
                width == 1920 && height == 1080
    }

    private var camera: Camera? = null
    private var texture: SurfaceTexture? = null
    private var display = EGL14.EGL_NO_DISPLAY
    private var context = EGL14.EGL_NO_CONTEXT
    private var window = EGL14.EGL_NO_SURFACE
    private var program = 0
    private var textureId = 0
    private var closed = false
    private var faceDetectionRunning = false
    private var lastFaceUpdateMs = 0L
    private var lastFaceProcessMs = 0L
    private var lastFocusCenterX = 0
    private var lastFocusCenterY = 0
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

                            if (activeSettings?.faceTracking == true && now - lastFaceUpdateMs >= 2000) {
                                val primary = validFaces.maxByOrNull { it.score } ?: validFaces[0]
                                val fX = primary.rect.centerX()
                                val fY = primary.rect.centerY()
                                val moved = Math.abs(fX - lastFocusCenterX) > 150 || Math.abs(fY - lastFocusCenterY) > 150
                                if (moved) {
                                    lastFaceUpdateMs = now
                                    lastFocusCenterX = fX
                                    lastFocusCenterY = fY
                                    try {
                                        val cp = cam.parameters
                                        val area = listOf(Camera.Area(primary.rect, 1000))
                                        if (cp.maxNumFocusAreas > 0) cp.focusAreas = area
                                        if (cp.maxNumMeteringAreas > 0 && !lockAeAwb) cp.meteringAreas = area
                                        cam.parameters = cp
                                    } catch (_: Exception) {}
                                }
                            }
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

    fun start(surface: Surface, settings: StreamSettings) {
        try {
            activeSettings = settings
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
            texture = SurfaceTexture(textureId).apply {
                setDefaultBufferSize(1920, 1080)
                setOnFrameAvailableListener({ draw() }, handler)
            }
            camera = Camera.open(0)
            val c = requireNotNull(camera)
            val p = c.parameters
            p.setRecordingHint(true)
            p.setPreviewSize(1920, 1080)
            p.set("video-size", "1920x1080")
            val effectiveFps = if (settings.powerMode == "saving" || settings.fps <= 30) 30 else 60
            currentAppliedFastFps = if (effectiveFps == 60) 1 else 0
            if (effectiveFps == 60) {
                p.set("fast-fps-mode", 1)
                p.set("preview-fps-range", "60000,60000")
                p.set("preview-frame-rate", 60)
                try { p.setPreviewFpsRange(60000, 60000) } catch (_: Exception) {}
                try { p.setPreviewFrameRate(60) } catch (_: Exception) {}
            } else {
                p.set("fast-fps-mode", 0)
                p.set("preview-fps-range", "30000,30000")
                p.set("preview-frame-rate", 30)
                try { p.setPreviewFpsRange(30000, 30000) } catch (_: Exception) {}
                try { p.setPreviewFrameRate(30) } catch (_: Exception) {}
            }
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
                StreamStats.controls += "Подсветка (Samsung HAL1). "
            }
            val isSuperSteady = settings.stabilization && settings.stabilizationMode == "strong"
            if (!settings.autoFraming && settings.zoom > 1.0f && p.isZoomSupported) {
                val maxZoom = p.maxZoom
                val target = ((settings.zoom - 1.0f) * (maxZoom / 4.0f)).toInt().coerceIn(0, maxZoom)
                p.zoom = target
                StreamStats.controls += "Зум ${settings.zoom}x (Samsung HAL1). "
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
                p.videoStabilization = settings.stabilization
            }
            if (settings.stabilization) {
                try { p.set("vdis", if (isSuperSteady) "on" else "off") } catch (_: Exception) {}
                try { p.set("video-stabilization-mode", if (isSuperSteady) "1" else "0") } catch (_: Exception) {}
            }
            c.parameters = p
            val accepted = c.parameters.previewSize
            check(accepted.width == 1920 && accepted.height == 1080) { "Samsung replaced 1080p preview size" }
            c.setErrorCallback { error, _ -> if (!closed) onError("Samsung camera error $error") }
            c.setPreviewTexture(texture)
            c.startPreview()
            if (settings.focus == "auto") c.autoFocus { _, _ -> }
            updateFaceTracking(c, settings.faceTracking, settings.autoFraming, settings.lockAeAwb)
            StreamStats.controls += "Samsung fast-fps · 1080p60; фокус ${p.focusMode}. "
            if (settings.focus == "manual") StreamStats.controls += "Ручная дистанция недоступна в этом режиме; автофокус. "
            StreamStats.state = "Камера запущена"
        } catch (e: Exception) { close(); throw e }
    }

    fun updateControls(settings: StreamSettings) {
        val c = camera ?: return
        try {
            activeSettings = settings
            val p = c.parameters
            p.setRecordingHint(true)
            p.setPreviewSize(1920, 1080)
            p.set("video-size", "1920x1080")
            val effectiveFps = if (settings.powerMode == "saving" || settings.fps <= 30) 30 else 60
            val desiredFastFps = if (effectiveFps == 60) 1 else 0
            val fpsToggled = currentAppliedFastFps != desiredFastFps
            if (effectiveFps == 60) {
                p.set("fast-fps-mode", 1)
                p.set("preview-fps-range", "60000,60000")
                p.set("preview-frame-rate", 60)
                try { p.setPreviewFpsRange(60000, 60000) } catch (_: Exception) {}
                try { p.setPreviewFrameRate(60) } catch (_: Exception) {}
            } else {
                p.set("fast-fps-mode", 0)
                p.set("preview-fps-range", "30000,30000")
                p.set("preview-frame-rate", 30)
                try { p.setPreviewFpsRange(30000, 30000) } catch (_: Exception) {}
                try { p.setPreviewFrameRate(30) } catch (_: Exception) {}
            }
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
                p.videoStabilization = settings.stabilization
            }
            if (settings.stabilization) {
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
                    c.parameters = p
                    c.setPreviewTexture(texture)
                    c.startPreview()
                    android.util.Log.i("S8Cam", "Samsung HAL1 dynamic FPS switched to $effectiveFps fps (fast-fps-mode=$desiredFastFps)")
                } catch (e: Exception) {
                    android.util.Log.w("S8Cam", "Failed to restart preview on FPS switch: ${e.message}")
                    c.parameters = p
                }
            } else {
                c.parameters = p
            }
            if (settings.focus == "auto") c.autoFocus { _, _ -> }
            updateFaceTracking(c, settings.faceTracking, settings.autoFraming, settings.lockAeAwb)
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
            p.setPreviewSize(1920, 1080)
            p.set("video-size", "1920x1080")
            val effectiveFps = if (activeSettings?.powerMode == "saving" || (activeSettings?.fps ?: 60) <= 30) 30 else 60
            if (effectiveFps == 60) {
                p.set("fast-fps-mode", 1)
                p.set("preview-fps-range", "60000,60000")
                p.set("preview-frame-rate", 60)
                try { p.setPreviewFpsRange(60000, 60000) } catch (_: Exception) {}
                try { p.setPreviewFrameRate(60) } catch (_: Exception) {}
            } else {
                p.set("fast-fps-mode", 0)
                p.set("preview-fps-range", "30000,30000")
                p.set("preview-frame-rate", 30)
                try { p.setPreviewFpsRange(30000, 30000) } catch (_: Exception) {}
                try { p.setPreviewFrameRate(30) } catch (_: Exception) {}
            }
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

            GLES20.glViewport(0, 0, 1920, 1080)
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
            val pts = if (rawPts > lastEglPtsNs) rawPts else (lastEglPtsNs + 1_000_000L)
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
