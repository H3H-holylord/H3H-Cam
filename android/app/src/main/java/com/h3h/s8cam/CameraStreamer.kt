package com.h3h.s8cam

import android.annotation.SuppressLint
import android.content.Context
import android.hardware.camera2.*
import android.hardware.camera2.params.MeteringRectangle
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.media.MediaCodec
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.util.Range
import android.util.Size
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executor
import java.util.concurrent.TimeUnit

class CameraStreamer(
    private val context: Context,
    private var settings: StreamSettings,
    private val onError: (String) -> Unit,
    private val onEncodedData: (ByteArray, Boolean, Boolean, Long) -> Unit
) {
    private val manager = context.getSystemService(CameraManager::class.java)
    private val thread = HandlerThread("S8CamCamera").apply { start() }
    private val handler = Handler(thread.looper)
    private val executor = Executor { command -> handler.post(command) }
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var legacy: SamsungLegacyCapture? = null
    private var activeCharacteristics: CameraCharacteristics? = null
    private var activeRange: Range<Int>? = null
    private var isHighSpeedSession: Boolean = false
    @Volatile private var encoder: H264Encoder? = null
    @Volatile private var stopped = false

    @Volatile private var c2FaceX = 0.5f
    @Volatile private var c2FaceY = 0.5f
    @Volatile private var c2FaceW = 0.0f
    @Volatile private var c2FaceH = 0.0f
    @Volatile private var c2CurrentCx = 0.5f
    @Volatile private var c2CurrentCy = 0.5f
    @Volatile private var c2CurrentZoom = 1.0f
    @Volatile private var c2TargetCx = 0.5f
    @Volatile private var c2TargetCy = 0.5f
    @Volatile private var c2TargetZoom = 1.0f
    @Volatile private var c2VelCx = 0.0f
    @Volatile private var c2VelCy = 0.0f
    @Volatile private var c2VelZoom = 0.0f
    @Volatile private var c2FaceCount = 0
    @Volatile private var c2LastFaceSeenMs = 0L
    @Volatile private var c2LastProcessMs = 0L
    @Volatile private var c2LastAppliedCx = 0.5f
    @Volatile private var c2LastAppliedCy = 0.5f
    @Volatile private var c2LastAppliedZoom = 1.0f
    @Volatile private var lastAeExposureTimeNs = 16_666_666L
    @Volatile private var lastAeSensitivity = 100

    private fun applyCamera2Crop() {
        handler.post {
            if (stopped) return@post
            val d = camera ?: return@post
            val s = session ?: return@post
            val c = activeCharacteristics ?: return@post
            val range = activeRange ?: return@post
            try {
                val req = buildRepeatingRequest(d, c, range, isHighSpeedSession, settings)
                if (isHighSpeedSession) {
                    val hs = s as CameraConstrainedHighSpeedCaptureSession
                    hs.setRepeatingBurst(hs.createHighSpeedRequestList(req.build()), captureCallback, handler)
                } else {
                    s.setRepeatingRequest(req.build(), captureCallback, handler)
                }
            } catch (_: Exception) {}
        }
    }

    private val captureCallback = object : CameraCaptureSession.CaptureCallback() {
        override fun onCaptureCompleted(
            session: CameraCaptureSession,
            request: CaptureRequest,
            result: TotalCaptureResult
        ) {
            if (stopped) return
            val exp = result.get(CaptureResult.SENSOR_EXPOSURE_TIME)
            if (exp != null && exp > 0) lastAeExposureTimeNs = exp
            val sens = result.get(CaptureResult.SENSOR_SENSITIVITY)
            if (sens != null && sens > 0) lastAeSensitivity = sens
            val faces = result.get(CaptureResult.STATISTICS_FACES)
            val now = android.os.SystemClock.uptimeMillis()
            if (!faces.isNullOrEmpty()) {
                val f = faces[0]
                val sensorRect = activeCharacteristics?.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE)
                if (sensorRect != null && sensorRect.width() > 0 && sensorRect.height() > 0) {
                    val normX = (f.bounds.centerX().toFloat() - sensorRect.left) / sensorRect.width().toFloat()
                    val normY = (f.bounds.centerY().toFloat() - sensorRect.top) / sensorRect.height().toFloat()
                    val normW = f.bounds.width().toFloat() / sensorRect.width().toFloat()
                    val normH = f.bounds.height().toFloat() / sensorRect.height().toFloat()

                    c2FaceX = normX.coerceIn(0f, 1f)
                    c2FaceY = normY.coerceIn(0f, 1f)
                    c2FaceW = normW.coerceIn(0f, 1f)
                    c2FaceH = normH.coerceIn(0f, 1f)
                    c2FaceCount = faces.size
                    c2LastFaceSeenMs = now

                    if (settings.autoFraming) {
                        val dx = normX - c2CurrentCx
                        val dy = (normY - 0.08f) - c2CurrentCy
                        val deadzone = settings.autoFramingDeadzone.coerceIn(0.01f, 0.20f)
                        if (kotlin.math.abs(dx) > deadzone || kotlin.math.abs(dy) > deadzone) {
                            c2TargetCx = normX.coerceIn(0.2f, 0.8f)
                            c2TargetCy = (normY - 0.08f).coerceIn(0.2f, 0.8f)
                        }
                        val maxZoom = settings.autoFramingZoom.coerceIn(1.05f, 2.5f)
                        val desiredZoom = when {
                            normH > 0.45f -> 1.05f
                            normH < 0.10f -> maxZoom
                            else -> (maxZoom * (0.25f / normH)).coerceIn(1.05f, maxZoom)
                        }
                        c2TargetZoom = (desiredZoom * settings.zoom).coerceIn(1.0f, 3.5f)
                    }
                }
            } else {
                if (now - c2LastFaceSeenMs > 2500) {
                    c2FaceCount = 0
                    if (settings.autoFraming) {
                        c2TargetCx = 0.5f
                        c2TargetCy = 0.5f
                        c2TargetZoom = settings.zoom.coerceIn(1.0f, 3.5f)
                    }
                }
            }

            if (settings.autoFraming && (now - c2LastProcessMs >= 33)) {
                val dt = if (c2LastProcessMs > 0) ((now - c2LastProcessMs).coerceIn(5, 100) / 1000f) else 0.033f
                c2LastProcessMs = now

                val speedMult = settings.autoFramingSpeed.coerceIn(0.2f, 3.0f)
                val omega = 4.2f * speedMult
                val expTerm = Math.exp((-omega * dt).toDouble()).toFloat()

                val dCx = c2CurrentCx - c2TargetCx
                val tempCx = (c2VelCx + omega * dCx) * dt
                c2CurrentCx = c2TargetCx + (dCx + tempCx) * expTerm
                c2VelCx = (c2VelCx - omega * tempCx) * expTerm

                val dCy = c2CurrentCy - c2TargetCy
                val tempCy = (c2VelCy + omega * dCy) * dt
                c2CurrentCy = c2TargetCy + (dCy + tempCy) * expTerm
                c2VelCy = (c2VelCy - omega * tempCy) * expTerm

                val dZoom = c2CurrentZoom - c2TargetZoom
                val tempZoom = (c2VelZoom + omega * dZoom) * dt
                c2CurrentZoom = c2TargetZoom + (dZoom + tempZoom) * expTerm
                c2VelZoom = (c2VelZoom - omega * tempZoom) * expTerm

                if (kotlin.math.abs(c2CurrentCx - c2LastAppliedCx) > 0.008f ||
                    kotlin.math.abs(c2CurrentCy - c2LastAppliedCy) > 0.008f ||
                    kotlin.math.abs(c2CurrentZoom - c2LastAppliedZoom) > 0.015f) {
                    c2LastAppliedCx = c2CurrentCx
                    c2LastAppliedCy = c2CurrentCy
                    c2LastAppliedZoom = c2CurrentZoom
                    applyCamera2Crop()
                }
            }
        }
    }

    fun requestIdr() { encoder?.requestIdr() }
    fun setBitrate(value: Int) = encoder?.setBitrate(value) == true

    @SuppressLint("MissingPermission")
    fun start() {
        handler.post {
            try {
                if (stopped) return@post
                val routes = CameraCatalog.list(context)
                val route = CameraCatalog.resolve(routes, settings.cameraKey)
                val (mode, actualFps) = CameraCatalog.resolveMode(route, settings.width, settings.height, settings.fps)
                val selectedCharacteristics = manager.getCameraCharacteristics(route.physicalId ?: route.cameraId)
                val highSpeed = mode.isHighSpeed(actualFps)
                val useLegacy = (actualFps == 60 || actualFps == 30) && SamsungLegacyCapture.supports(
                    route.cameraId, route.physicalId, mode.width, mode.height, settings.forceSamsungLegacy)
                val fpsRange = if (useLegacy) Range(actualFps, actualFps) else selectFpsRange(
                    selectedCharacteristics,
                    Size(mode.width, mode.height),
                    actualFps,
                    highSpeed
                )

                StreamStats.actualFps = actualFps
                StreamStats.highSpeed = highSpeed
                StreamStats.resolution = "${mode.width}x${mode.height}"
                StreamStats.camera = route.label
                StreamStats.controls = buildString {
                    if (route.key != settings.cameraKey && settings.cameraKey != "auto")
                        append("Сохранённый модуль недоступен; выбран ${route.label}. ")
                    if (mode.width != settings.width || mode.height != settings.height)
                        append("Разрешение заменено на ${mode.label}. ")
                    if (actualFps != settings.fps)
                        append("${settings.fps} fps недоступны; выбрано $actualFps fps. ")
                }

                val e = H264Encoder(
                    width = mode.width,
                    height = mode.height,
                    fps = actualFps,
                    bitrate = settings.bitrate,
                    codecName = settings.codec,
                    onError = onError,
                    onEncodedData = onEncodedData
                )
                encoder = e
                e.start()

                if (useLegacy) {
                    legacy = SamsungLegacyCapture(handler, onError).also { it.start(e.inputSurface, settings) }
                    return@post
                }

                manager.openCamera(route.cameraId, object : CameraDevice.StateCallback() {
                    override fun onOpened(device: CameraDevice) {
                        if (stopped) { device.close(); return }
                        camera = device
                        try {
                            createSession(device, route, selectedCharacteristics, fpsRange, highSpeed)
                        } catch (ex: Exception) {
                            onError("Настройка камеры: ${ex.message}")
                        }
                    }

                    override fun onDisconnected(device: CameraDevice) {
                        device.close()
                        if (!stopped) onError("Камера отключилась")
                    }

                    override fun onError(device: CameraDevice, error: Int) {
                        device.close()
                        if (!stopped) onError("Camera2 error $error")
                    }
                }, handler)
            } catch (e: Exception) {
                if (!stopped) onError(e.message ?: "Ошибка камеры")
            }
        }
    }

    private fun selectFpsRange(
        c: CameraCharacteristics,
        size: Size,
        fps: Int,
        highSpeed: Boolean
    ): Range<Int> {
        val ranges = if (highSpeed) {
            val map = c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
                ?: error("Camera2 не сообщает high-speed режимы")
            map.getHighSpeedVideoFpsRangesFor(size).toList()
        } else c.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.toList().orEmpty()
        if (highSpeed) {
            return ranges.filter { it.lower == it.upper && it.upper >= fps && it.upper % fps == 0 }
                .minByOrNull { it.upper }
                ?: error("Camera2 не сообщает фиксированный high-speed режим для $fps fps")
        }
        val matching = ranges.filter { fps in it.lower..it.upper }
        if (matching.isNotEmpty()) {
            return matching.minWithOrNull(compareBy<Range<Int>> { it.upper - it.lower }
                .thenByDescending { it.lower })!!
        }
        return ranges.filter { it.upper >= fps }.minByOrNull { it.upper - fps }
            ?: ranges.maxByOrNull { it.upper }
            ?: Range(fps, fps)
    }

    private fun createSession(
        device: CameraDevice,
        route: CameraRoute,
        c: CameraCharacteristics,
        fpsRange: Range<Int>,
        highSpeed: Boolean
    ) {
        val surface = encoder?.inputSurface ?: error("Кодек не запущен")
        val callback = object : CameraCaptureSession.StateCallback() {
            override fun onConfigured(configured: CameraCaptureSession) {
                if (stopped) { configured.close(); return }
                session = configured
                try {
                    startRepeating(device, configured, c, fpsRange, highSpeed)
                } catch (ex: Exception) {
                    if (!stopped) onError("Capture request: ${ex.message}")
                }
            }

            override fun onConfigureFailed(configured: CameraCaptureSession) {
                if (!stopped) {
                    if (route.physicalId != null) {
                        try {
                            @Suppress("DEPRECATION")
                            device.createCaptureSession(listOf(surface), object : CameraCaptureSession.StateCallback() {
                                override fun onConfigured(fallbackSession: CameraCaptureSession) {
                                    if (stopped) { fallbackSession.close(); return }
                                    session = fallbackSession
                                    try {
                                        startRepeating(device, fallbackSession, c, fpsRange, highSpeed)
                                    } catch (ex: Exception) {
                                        if (!stopped) onError("Capture request: ${ex.message}")
                                    }
                                }
                                override fun onConfigureFailed(s: CameraCaptureSession) {
                                    if (!stopped) onError("Camera2 не смог открыть выбранный модуль/режим")
                                }
                            }, handler)
                            return
                        } catch (_: Exception) {}
                    }
                    onError("Camera2 не смог открыть выбранный модуль/режим")
                }
            }
        }

        if (highSpeed) {
            device.createConstrainedHighSpeedCaptureSession(listOf(surface), callback, handler)
        } else if (Build.VERSION.SDK_INT >= 28 && route.physicalId != null) {
            val output = OutputConfiguration(surface).apply { setPhysicalCameraId(route.physicalId) }
            val config = SessionConfiguration(
                SessionConfiguration.SESSION_REGULAR,
                listOf(output),
                executor,
                callback
            )
            device.createCaptureSession(config)
        } else {
            @Suppress("DEPRECATION")
            device.createCaptureSession(listOf(surface), callback, handler)
        }
    }

    private fun startRepeating(
        device: CameraDevice,
        configured: CameraCaptureSession,
        c: CameraCharacteristics,
        fpsRange: Range<Int>,
        highSpeed: Boolean
    ) {
        activeCharacteristics = c
        activeRange = fpsRange
        isHighSpeedSession = highSpeed
        val request = buildRepeatingRequest(device, c, fpsRange, highSpeed, settings)
        val afModes = c.get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES) ?: intArrayOf()
        val autoOnce = settings.focus == "auto" && afModes.contains(CaptureRequest.CONTROL_AF_MODE_AUTO)
        if (autoOnce) {
            request.set(CaptureRequest.CONTROL_AF_TRIGGER, CaptureRequest.CONTROL_AF_TRIGGER_START)
            configured.capture(request.build(), null, handler)
            request.set(CaptureRequest.CONTROL_AF_TRIGGER, CaptureRequest.CONTROL_AF_TRIGGER_IDLE)
        }
        if (highSpeed) {
            val highSpeedSession = configured as CameraConstrainedHighSpeedCaptureSession
            highSpeedSession.setRepeatingBurst(
                highSpeedSession.createHighSpeedRequestList(request.build()),
                captureCallback,
                handler
            )
        } else configured.setRepeatingRequest(request.build(), captureCallback, handler)

        val ev = c.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_RANGE)
        val focusMax = c.get(CameraCharacteristics.LENS_INFO_MINIMUM_FOCUS_DISTANCE) ?: 0f
        val step = c.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_STEP)
        StreamStats.controls += if (highSpeed) {
            "High-speed Camera2; ручные controls ограничены. "
        } else {
            "Фокус 0–$focusMax D; EV index ${ev?.lower}…${ev?.upper}, шаг $step. "
        }
        StreamStats.state = "Камера запущена"
    }

    private fun buildRepeatingRequest(
        device: CameraDevice,
        c: CameraCharacteristics,
        fpsRange: Range<Int>,
        highSpeed: Boolean,
        s: StreamSettings
    ): CaptureRequest.Builder {
        val surface = encoder?.inputSurface ?: error("Кодек не запущен")
        val request = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
            addTarget(surface)
            set(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_AUTO)
            if (s.manualIso > 0 || s.shutterSpeedNs > 0) {
                set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_OFF)
                val isoRange = c.get(CameraCharacteristics.SENSOR_INFO_SENSITIVITY_RANGE)
                val baseIso = if (s.manualIso > 0 && isoRange != null) s.manualIso.coerceIn(isoRange.lower, isoRange.upper)
                              else isoRange?.let { lastAeSensitivity.coerceIn(it.lower, it.upper) } ?: 100
                set(CaptureRequest.SENSOR_SENSITIVITY, baseIso)

                val expRange = c.get(CameraCharacteristics.SENSOR_INFO_EXPOSURE_TIME_RANGE)
                val nominalExpNs = if (s.fps >= 60) 16_666_666L else 33_333_333L
                val baseExpNs = if (s.shutterSpeedNs > 0) s.shutterSpeedNs else lastAeExposureTimeNs.coerceAtLeast(nominalExpNs)
                if (expRange != null) {
                    set(CaptureRequest.SENSOR_EXPOSURE_TIME, baseExpNs.coerceIn(expRange.lower, expRange.upper))
                }
            } else {
                set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
            }
            set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, fpsRange)
            val flashAvailable = c.get(CameraCharacteristics.FLASH_INFO_AVAILABLE) == true ||
                activeCharacteristics?.get(CameraCharacteristics.FLASH_INFO_AVAILABLE) == true ||
                try { manager.getCameraCharacteristics(s.cameraKey.split("/")[0]).get(CameraCharacteristics.FLASH_INFO_AVAILABLE) == true } catch (_: Exception) { false }
            if (flashAvailable) {
                set(CaptureRequest.FLASH_MODE, if (s.torch) CaptureRequest.FLASH_MODE_TORCH else CaptureRequest.FLASH_MODE_OFF)
            }
            val baseZoom = if (s.stabilization && s.stabilizationMode == "strong" && s.zoom < 1.08f && !s.autoFraming) 1.08f else s.zoom
            val effectiveZoom = if (s.autoFraming) c2CurrentZoom.coerceAtLeast(baseZoom) else baseZoom
            val zoomRatioRange = if (Build.VERSION.SDK_INT >= 30) c.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE) else null
            if (Build.VERSION.SDK_INT >= 30 && zoomRatioRange != null) {
                val clampedRatio = effectiveZoom.coerceIn(zoomRatioRange.lower, zoomRatioRange.upper)
                set(CaptureRequest.CONTROL_ZOOM_RATIO, clampedRatio)
                val sensorRect = c.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE)
                if (sensorRect != null && s.autoFraming && (c2CurrentCx != 0.5f || c2CurrentCy != 0.5f)) {
                    val cropW = (sensorRect.width() / clampedRatio).toInt().coerceAtMost(sensorRect.width())
                    val cropH = (sensorRect.height() / clampedRatio).toInt().coerceAtMost(sensorRect.height())
                    val halfW = cropW / 2
                    val halfH = cropH / 2
                    val centerX = sensorRect.left + (c2CurrentCx * sensorRect.width()).toInt()
                    val centerY = sensorRect.top + (c2CurrentCy * sensorRect.height()).toInt()
                    val cropLeft = (centerX - halfW).coerceIn(sensorRect.left, sensorRect.right - cropW)
                    val cropTop = (centerY - halfH).coerceIn(sensorRect.top, sensorRect.bottom - cropH)
                    set(CaptureRequest.SCALER_CROP_REGION, android.graphics.Rect(cropLeft, cropTop, cropLeft + cropW, cropTop + cropH))
                }
            } else {
                val sensorRect = c.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE)
                if (sensorRect != null && effectiveZoom > 1.0f) {
                    val clampedZoom = effectiveZoom.coerceIn(1.0f, 5.0f)
                    val scale = 1.0f / clampedZoom
                    val cropW = (sensorRect.width() * scale).toInt()
                    val cropH = (sensorRect.height() * scale).toInt()
                    val cx = if (s.autoFraming) c2CurrentCx else 0.5f
                    val cy = if (s.autoFraming) c2CurrentCy else 0.5f
                    val halfW = cropW / 2
                    val halfH = cropH / 2
                    val centerX = sensorRect.left + (cx * sensorRect.width()).toInt()
                    val centerY = sensorRect.top + (cy * sensorRect.height()).toInt()
                    val cropLeft = (centerX - halfW).coerceIn(sensorRect.left, sensorRect.right - cropW)
                    val cropTop = (centerY - halfH).coerceIn(sensorRect.top, sensorRect.bottom - cropH)
                    set(CaptureRequest.SCALER_CROP_REGION, android.graphics.Rect(cropLeft, cropTop, cropLeft + cropW, cropTop + cropH))
                }
            }
        }

        val afModes = c.get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES) ?: intArrayOf()
        val focusMax = c.get(CameraCharacteristics.LENS_INFO_MINIMUM_FOCUS_DISTANCE) ?: 0f
        if (highSpeed) {
            setBestAutoFocus(request, afModes)
        } else when (s.focus) {
            "manual", "infinity" -> {
                if (focusMax > 0f && afModes.contains(CaptureRequest.CONTROL_AF_MODE_OFF)) {
                    request.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_OFF)
                    val distance = if (s.focus == "infinity") 0f else s.focusDistance.coerceIn(0f, focusMax)
                    request.set(CaptureRequest.LENS_FOCUS_DISTANCE, distance)
                } else {
                    setBestAutoFocus(request, afModes)
                }
            }
            "auto" -> {
                if (afModes.contains(CaptureRequest.CONTROL_AF_MODE_AUTO)) {
                    request.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_AUTO)
                } else {
                    setBestAutoFocus(request, afModes)
                }
            }
            else -> {
                setBestAutoFocus(request, afModes)
            }
        }

        val ev = c.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_RANGE)
        if (!highSpeed && ev != null)
            request.set(CaptureRequest.CONTROL_AE_EXPOSURE_COMPENSATION, s.exposure.coerceIn(ev.lower, ev.upper))

        val awbModes = c.get(CameraCharacteristics.CONTROL_AWB_AVAILABLE_MODES) ?: intArrayOf()
        if (!highSpeed && s.manualWbKelvin > 0 && awbModes.contains(CaptureRequest.CONTROL_AWB_MODE_OFF)) {
            request.set(CaptureRequest.CONTROL_AWB_MODE, CaptureRequest.CONTROL_AWB_MODE_OFF)
            val kelvin = s.manualWbKelvin.toFloat().coerceIn(2500f, 8000f)
            val r = (kelvin / 5000f).coerceIn(0.6f, 2.5f)
            val b = (5000f / kelvin).coerceIn(0.6f, 2.5f)
            request.set(CaptureRequest.COLOR_CORRECTION_MODE, CaptureRequest.COLOR_CORRECTION_MODE_TRANSFORM_MATRIX)
            request.set(CaptureRequest.COLOR_CORRECTION_GAINS, android.hardware.camera2.params.RggbChannelVector(r, 1.0f, 1.0f, b))
        } else {
            request.set(CaptureRequest.COLOR_CORRECTION_MODE, CaptureRequest.COLOR_CORRECTION_MODE_FAST)
            val wantedWb = when (s.wb) {
                "daylight" -> CaptureRequest.CONTROL_AWB_MODE_DAYLIGHT
                "cloudy" -> CaptureRequest.CONTROL_AWB_MODE_CLOUDY_DAYLIGHT
                "incandescent" -> CaptureRequest.CONTROL_AWB_MODE_INCANDESCENT
                "fluorescent" -> CaptureRequest.CONTROL_AWB_MODE_FLUORESCENT
                else -> CaptureRequest.CONTROL_AWB_MODE_AUTO
            }
            if (!highSpeed && awbModes.contains(wantedWb)) request.set(CaptureRequest.CONTROL_AWB_MODE, wantedWb)
        }

        if (!highSpeed) {
            val nrModes = c.get(CameraCharacteristics.NOISE_REDUCTION_AVAILABLE_NOISE_REDUCTION_MODES) ?: intArrayOf()
            if (nrModes.contains(CaptureRequest.NOISE_REDUCTION_MODE_FAST)) {
                request.set(CaptureRequest.NOISE_REDUCTION_MODE, CaptureRequest.NOISE_REDUCTION_MODE_FAST)
            } else if (nrModes.contains(CaptureRequest.NOISE_REDUCTION_MODE_HIGH_QUALITY)) {
                request.set(CaptureRequest.NOISE_REDUCTION_MODE, CaptureRequest.NOISE_REDUCTION_MODE_HIGH_QUALITY)
            }

            val edgeModes = c.get(CameraCharacteristics.EDGE_AVAILABLE_EDGE_MODES) ?: intArrayOf()
            if (edgeModes.contains(CaptureRequest.EDGE_MODE_FAST)) {
                request.set(CaptureRequest.EDGE_MODE, CaptureRequest.EDGE_MODE_FAST)
            } else if (edgeModes.contains(CaptureRequest.EDGE_MODE_HIGH_QUALITY)) {
                request.set(CaptureRequest.EDGE_MODE, CaptureRequest.EDGE_MODE_HIGH_QUALITY)
            }
        }
        // Hardware OIS (optical) and EIS (video gyro) stabilization to eliminate desk/tripod shaking
        if (!highSpeed) {
            val oisModes = c.get(CameraCharacteristics.LENS_INFO_AVAILABLE_OPTICAL_STABILIZATION) ?: intArrayOf()
            val videoStabModes = c.get(CameraCharacteristics.CONTROL_AVAILABLE_VIDEO_STABILIZATION_MODES) ?: intArrayOf()
            if (s.stabilization) {
                if (oisModes.contains(CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE_ON)) {
                    request.set(CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE, CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE_ON)
                }
                val wantsSuperSteady = s.stabilizationMode == "strong"
                val previewStab = 2 // CONTROL_VIDEO_STABILIZATION_MODE_PREVIEW_STABILIZATION
                if (wantsSuperSteady && videoStabModes.contains(previewStab)) {
                    request.set(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE, previewStab)
                } else if (videoStabModes.contains(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_ON)) {
                    request.set(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE, CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_ON)
                }
            } else {
                if (oisModes.contains(CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE_OFF)) {
                    request.set(CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE, CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE_OFF)
                }
                if (videoStabModes.contains(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_OFF)) {
                    request.set(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE, CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_OFF)
                }
            }
            if (c.get(CameraCharacteristics.CONTROL_AE_LOCK_AVAILABLE) == true)
                request.set(CaptureRequest.CONTROL_AE_LOCK, s.lockAeAwb)
            if (c.get(CameraCharacteristics.CONTROL_AWB_LOCK_AVAILABLE) == true)
                request.set(CaptureRequest.CONTROL_AWB_LOCK, s.lockAeAwb)
            val maxFaces = c.get(CameraCharacteristics.STATISTICS_INFO_MAX_FACE_COUNT) ?: 0
            if ((s.faceTracking || s.autoFraming) && maxFaces > 0) {
                val faceModes = c.get(CameraCharacteristics.STATISTICS_INFO_AVAILABLE_FACE_DETECT_MODES) ?: intArrayOf()
                if (faceModes.contains(CaptureRequest.STATISTICS_FACE_DETECT_MODE_SIMPLE)) {
                    request.set(CaptureRequest.STATISTICS_FACE_DETECT_MODE, CaptureRequest.STATISTICS_FACE_DETECT_MODE_SIMPLE)
                } else if (faceModes.contains(CaptureRequest.STATISTICS_FACE_DETECT_MODE_FULL)) {
                    request.set(CaptureRequest.STATISTICS_FACE_DETECT_MODE, CaptureRequest.STATISTICS_FACE_DETECT_MODE_FULL)
                }
            } else {
                request.set(CaptureRequest.STATISTICS_FACE_DETECT_MODE, CaptureRequest.STATISTICS_FACE_DETECT_MODE_OFF)
            }
        }

        return request
    }

    fun updateControls(newSettings: StreamSettings) {
        handler.post {
            if (stopped) return@post
            settings = newSettings
            legacy?.updateControls(newSettings)
            val d = camera ?: return@post
            val s = session ?: return@post
            val c = activeCharacteristics ?: return@post
            val range = activeRange ?: return@post
            try {
                val req = buildRepeatingRequest(d, c, range, isHighSpeedSession, newSettings)
                if (isHighSpeedSession) {
                    val hs = s as CameraConstrainedHighSpeedCaptureSession
                    hs.setRepeatingBurst(hs.createHighSpeedRequestList(req.build()), captureCallback, handler)
                } else {
                    s.setRepeatingRequest(req.build(), captureCallback, handler)
                }
            } catch (e: Exception) {
                android.util.Log.w("S8Cam", "Dynamic controls failed: ${e.message}")
            }
        }
    }

    fun isLegacy(): Boolean = legacy != null
    fun getFaceInfo(): FloatArray? {
        legacy?.let { return it.getFaceInfo() }
        val now = android.os.SystemClock.uptimeMillis()
        if (now - c2LastFaceSeenMs > 2000 || c2FaceCount <= 0) return null
        val scale = 1.0f / c2CurrentZoom.coerceIn(1.0f, 4.0f)
        val halfSpan = 0.5f * scale
        val safeCx = c2CurrentCx.coerceIn(halfSpan, 1.0f - halfSpan)
        val safeCy = c2CurrentCy.coerceIn(halfSpan, 1.0f - halfSpan)
        return floatArrayOf(c2FaceX, c2FaceY, c2FaceW, c2FaceH, safeCx, safeCy, c2CurrentZoom, c2FaceCount.toFloat())
    }

    fun triggerTapToFocus(normX: Float, normY: Float) {
        handler.post {
            if (stopped) return@post
            legacy?.triggerTapToFocus(normX, normY)
            val dev = camera ?: return@post
            val s = session ?: return@post
            val c = activeCharacteristics ?: return@post
            val range = activeRange ?: return@post
            val sensorRect = c.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE) ?: return@post
            val afMaxRegions = c.get(CameraCharacteristics.CONTROL_MAX_REGIONS_AF) ?: 0
            val aeMaxRegions = c.get(CameraCharacteristics.CONTROL_MAX_REGIONS_AE) ?: 0
            if (afMaxRegions < 1 && aeMaxRegions < 1) return@post

            val clampedX = normX.coerceIn(0.05f, 0.95f)
            val clampedY = normY.coerceIn(0.05f, 0.95f)
            val focusX = sensorRect.left + (clampedX * sensorRect.width()).toInt()
            val focusY = sensorRect.top + (clampedY * sensorRect.height()).toInt()
            val halfBox = (sensorRect.width().coerceAtMost(sensorRect.height()) * 0.08f).toInt()
            val boxLeft = (focusX - halfBox).coerceIn(sensorRect.left, sensorRect.right - halfBox * 2)
            val boxTop = (focusY - halfBox).coerceIn(sensorRect.top, sensorRect.bottom - halfBox * 2)
            val meteringRect = MeteringRectangle(boxLeft, boxTop, halfBox * 2, halfBox * 2, MeteringRectangle.METERING_WEIGHT_MAX)

            try {
                val triggerReq = buildRepeatingRequest(dev, c, range, isHighSpeedSession, settings)
                if (afMaxRegions > 0) {
                    triggerReq.set(CaptureRequest.CONTROL_AF_REGIONS, arrayOf(meteringRect))
                    triggerReq.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_AUTO)
                    triggerReq.set(CaptureRequest.CONTROL_AF_TRIGGER, CaptureRequest.CONTROL_AF_TRIGGER_START)
                }
                if (aeMaxRegions > 0) {
                    triggerReq.set(CaptureRequest.CONTROL_AE_REGIONS, arrayOf(meteringRect))
                }
                s.capture(triggerReq.build(), null, handler)

                val repeatReq = buildRepeatingRequest(dev, c, range, isHighSpeedSession, settings)
                if (afMaxRegions > 0) {
                    repeatReq.set(CaptureRequest.CONTROL_AF_REGIONS, arrayOf(meteringRect))
                    repeatReq.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_AUTO)
                }
                if (aeMaxRegions > 0) {
                    repeatReq.set(CaptureRequest.CONTROL_AE_REGIONS, arrayOf(meteringRect))
                }
                s.setRepeatingRequest(repeatReq.build(), captureCallback, handler)
                android.util.Log.i("S8Cam", "Tap to focus & AE at $clampedX, $clampedY")
            } catch (ex: Exception) {
                android.util.Log.w("S8Cam", "Tap to focus failed: ${ex.message}")
            }
        }
    }

    private fun setBestAutoFocus(request: CaptureRequest.Builder, modes: IntArray) {
        when {
            modes.contains(CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO) ->
                request.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
            modes.contains(CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE) ->
                request.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE)
            modes.contains(CaptureRequest.CONTROL_AF_MODE_AUTO) ->
                request.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_AUTO)
            modes.contains(CaptureRequest.CONTROL_AF_MODE_OFF) ->
                request.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_OFF)
        }
    }

    fun stop() {
        stopped = true
        val done = CountDownLatch(1)
        handler.post {
            try {
                try { session?.stopRepeating() } catch (_: Exception) {}
                try { session?.abortCaptures() } catch (_: Exception) {}
                session?.close()
                camera?.close()
                legacy?.close()
                legacy = null
                encoder?.stop()
                encoder = null
            } finally {
                done.countDown()
            }
        }
        done.await(4, TimeUnit.SECONDS)
        thread.quitSafely()
        thread.join(1000)
    }
}
