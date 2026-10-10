package com.h3h.s8cam

import android.content.Context
import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.media.MediaCodec
import android.os.Build
import android.util.Size
import kotlin.math.abs

data class VideoMode(
    val width: Int,
    val height: Int,
    val fps: List<Int>,
    val highSpeedFps: List<Int> = emptyList(),
    val captureWidth: Int = width,
    val captureHeight: Int = height
) {
    val key get() = "${width}x${height}"
    val label get() = "${width} × ${height}"
    fun isHighSpeed(value: Int) = value in highSpeedFps
    val scaled get() = captureWidth != width || captureHeight != height
}

data class CameraRoute(
    val key: String,
    val cameraId: String,
    val physicalId: String?,
    val label: String,
    val facing: String,
    val focalLengths: List<Float>,
    val minimumFocusDistance: Float,
    val flash: Boolean,
    val modes: List<VideoMode>
)

object CameraCatalog {
    private val commonFps = listOf(15, 24, 25, 30, 50, 60)

    fun list(context: Context): List<CameraRoute> {
        val manager = context.getSystemService(CameraManager::class.java)
        val result = mutableListOf<CameraRoute>()
        val publicIds = manager.cameraIdList.toSet()
        publicIds.forEach { logicalId ->
            try {
                val logical = manager.getCameraCharacteristics(logicalId)
                val physicalIds = if (Build.VERSION.SDK_INT >= 28) logical.physicalCameraIds.sorted() else emptyList()
                result += route(logicalId, null, logical, logical)
                // A public physical ID is already listed as an independently openable camera.
                // Add nested routes only for modules that must be addressed through a logical ID.
                physicalIds.filterNot { it in publicIds }.forEach { physicalId ->
                    try {
                        val physical = manager.getCameraCharacteristics(physicalId)
                        result += route(logicalId, physicalId, physical, logical)
                    } catch (_: Exception) {
                    }
                }
            } catch (_: Exception) {
            }
        }
        return result.filter { it.modes.isNotEmpty() }.distinctBy { it.key }
    }

    private fun route(
        logicalId: String,
        physicalId: String?,
        characteristics: CameraCharacteristics,
        logicalCharacteristics: CameraCharacteristics
    ): CameraRoute {
        val facing = facingName(characteristics.get(CameraCharacteristics.LENS_FACING))
        val focal = characteristics.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)
            ?.map { it.toFloat() } ?: emptyList()
        val focalText = if (focal.isEmpty()) "" else " · " + focal.joinToString("/") { formatFocal(it) } + " мм"
        val moduleText = if (physicalId == null) "ID $logicalId" else "модуль $physicalId · логический $logicalId"
        val label = "$facing$focalText · $moduleText"
        val ownSizes = outputSizes(characteristics)
        val logicalSizes = outputSizes(logicalCharacteristics).toSet()
        val sizes = ownSizes.filter { it in logicalSizes }.ifEmpty { outputSizes(logicalCharacteristics) }
        val nativeModes = sizes
            .filter { it.width >= 640 && it.height >= 360 && it.width <= 4096 && it.height <= 2160 }
            .mapNotNull { size ->
                val (regular, highSpeed) = supportedFps(characteristics, size, physicalId == null)
                val samsung = if (SamsungLegacyCapture.supports(logicalId, physicalId, size.width, size.height) &&
                    H264Encoder.supports(size.width, size.height, 60)) listOf(60) else emptyList()
                val fps = (regular + highSpeed + samsung).distinct().sorted()
                if (fps.isEmpty()) null else VideoMode(size.width, size.height, fps, highSpeed)
            }
        // The encoder's Surface cannot request an unadvertised Camera2 size. When QHD
        // is absent, capture a real larger SurfaceTexture mode and downscale on the GPU.
        val logicalTextureSizes = textureSizes(logicalCharacteristics).toSet()
        val textureModes = textureSizes(characteristics).filter { it in logicalTextureSizes }
            .filter { it.width >= 2560 && it.height >= 1440 && it.width <= 4096 && it.height <= 2304 }
            .map { size ->
                val (fps, _) = supportedFps(characteristics, size, false, Size(2560, 1440), true)
                VideoMode(size.width, size.height, fps)
            }
        val modes = QhdModePlanner.add(nativeModes, textureModes) { fps -> H264Encoder.supports(2560, 1440, fps) }
            .sortedWith(compareByDescending<VideoMode> { it.width.toLong() * it.height }
                .thenByDescending { abs(it.width.toDouble() / it.height - 16.0 / 9.0) < 0.02 })
            .distinctBy { it.key }
        // Do not trim the catalog. The desktop is the single source of settings and must see
        // every encoder-compatible resolution, including each mode that can really sustain 60 FPS.
        return CameraRoute(
            key = if (physicalId == null) logicalId else "$logicalId/$physicalId",
            cameraId = logicalId,
            physicalId = physicalId,
            label = label,
            facing = facing,
            focalLengths = focal,
            minimumFocusDistance = characteristics.get(CameraCharacteristics.LENS_INFO_MINIMUM_FOCUS_DISTANCE) ?: 0f,
            flash = characteristics.get(CameraCharacteristics.FLASH_INFO_AVAILABLE) == true,
            modes = modes
        )
    }

    private fun outputSizes(c: CameraCharacteristics): List<Size> {
        val map = c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: return emptyList()
        val codec = try { map.getOutputSizes(MediaCodec::class.java)?.toList() } catch (_: Exception) { null }
        val recorder = try { map.getOutputSizes(android.media.MediaRecorder::class.java)?.toList() } catch (_: Exception) { null }
        val texture = try { map.getOutputSizes(SurfaceTexture::class.java)?.toList() } catch (_: Exception) { null }
        val priv = try { map.getOutputSizes(android.graphics.ImageFormat.PRIVATE)?.toList() } catch (_: Exception) { null }
        val result = mutableListOf<Size>()
        codec?.let { result.addAll(it) }
        recorder?.let { result.addAll(it) }
        texture?.let { result.addAll(it) }
        priv?.let { result.addAll(it) }
        return result.distinct()
    }

    private fun textureSizes(c: CameraCharacteristics): List<Size> = try {
        c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
            ?.getOutputSizes(SurfaceTexture::class.java)?.toList().orEmpty()
    } catch (_: Exception) { emptyList() }

    private fun supportedFps(
        c: CameraCharacteristics,
        size: Size,
        allowHighSpeed: Boolean,
        encodedSize: Size = size,
        textureOnly: Boolean = false
    ): Pair<List<Int>, List<Int>> {
        val map = c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
            ?: return emptyList<Int>() to emptyList()
        val durations = sequenceOf(
            try { map.getOutputMinFrameDuration(SurfaceTexture::class.java, size) } catch (_: Exception) { 0L },
            try { map.getOutputMinFrameDuration(MediaCodec::class.java, size) } catch (_: Exception) { 0L },
            try { map.getOutputMinFrameDuration(android.media.MediaRecorder::class.java, size) } catch (_: Exception) { 0L },
            try { map.getOutputMinFrameDuration(android.graphics.ImageFormat.PRIVATE, size) } catch (_: Exception) { 0L }
        )
        val duration = (if (textureOnly) durations.take(1) else durations).filter { it > 0L }.minOrNull() ?: 0L

        val ranges = c.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.toList().orEmpty()
        val maxSensorFps = ranges.maxOfOrNull { it.upper } ?: 30
        // No measured per-size duration: advertise a conservative 30 FPS for scaled
        // modes, rather than copying a sensor-wide 60 FPS range onto a 4K stream.
        val durationLimit = if (duration > 0) (1_000_000_000L / duration).toInt()
            else if (textureOnly) minOf(maxSensorFps, 30) else maxSensorFps

        val regular = commonFps.filter { fps ->
            fps <= durationLimit + 1 && ranges.any { (fps in it.lower..it.upper) || (it.upper >= fps) } &&
                H264Encoder.supports(encodedSize.width, encodedSize.height, fps)
        }.toMutableList()
        if (regular.isEmpty()) {
            ranges.map { it.upper }.filter { it in 10..60 && it <= durationLimit + 1 }
                .distinct().sorted()
                .filter { H264Encoder.supports(encodedSize.width, encodedSize.height, it) }
                .forEach { regular += it }
        }
        if (!textureOnly && regular.isEmpty() && H264Encoder.supports(size.width, size.height, 30)) {
            regular += 30
        }
        if (!textureOnly && !(size.width == 2560 && size.height == 1440) &&
            maxSensorFps >= 60 && 60 !in regular && H264Encoder.supports(size.width, size.height, 60)) {
            regular += 60
        }
        val highSpeed = if (allowHighSpeed && Build.VERSION.SDK_INT >= 23) {
            try {
                if (map.highSpeedVideoSizes?.contains(size) == true) {
                    map.getHighSpeedVideoFpsRangesFor(size)
                        .filter { it.lower == it.upper }
                        .flatMap { range -> commonFps.filter { fps ->
                            fps == range.upper || (Build.VERSION.SDK_INT >= 29 && fps == 60 &&
                                range.upper > fps && range.upper % fps == 0)
                        } }
                        .filter { it !in regular }
                        .filter { H264Encoder.supports(size.width, size.height, it) }
                        .distinct().sorted()
                } else emptyList()
            } catch (_: Exception) {
                emptyList()
            }
        } else emptyList()
        return regular.distinct().sorted() to highSpeed
    }

    fun resolve(routes: List<CameraRoute>, key: String): CameraRoute {
        routes.firstOrNull { it.key == key }?.let { return it }
        return routes.firstOrNull { it.physicalId == null && it.facing == "Задняя" }
            ?: routes.firstOrNull { it.facing == "Задняя" }
            ?: routes.firstOrNull()
            ?: error("Camera2 не нашёл совместимую H.264 камеру")
    }

    fun resolveMode(route: CameraRoute, width: Int, height: Int, requestedFps: Int): Pair<VideoMode, Int> {
        val mode = route.modes.firstOrNull { it.width == width && it.height == height }
            ?: route.modes.minByOrNull {
                val aspect = abs(it.width.toDouble() / it.height - width.toDouble() / height)
                abs(it.width.toLong() * it.height - width.toLong() * height) + (aspect * 10_000_000).toLong()
            } ?: error("У выбранной камеры нет совместимого видеорежима")
        val fps = mode.fps.minByOrNull { abs(it - requestedFps) } ?: 30
        return mode to fps
    }

    private fun facingName(value: Int?) = when (value) {
        CameraCharacteristics.LENS_FACING_BACK -> "Задняя"
        CameraCharacteristics.LENS_FACING_FRONT -> "Фронтальная"
        CameraCharacteristics.LENS_FACING_EXTERNAL -> "Внешняя"
        else -> "Камера"
    }

    private fun formatFocal(value: Float): String =
        if (abs(value - value.toInt()) < 0.05f) value.toInt().toString() else String.format(java.util.Locale.US, "%.1f", value)
}
