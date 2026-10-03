package com.h3h.s8cam

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.util.Log
import android.view.Surface
import kotlin.concurrent.thread

class H264Encoder(
    private val width: Int,
    private val height: Int,
    private val fps: Int,
    private val bitrate: Int,
    private val codecName: String = "h264",
    private val onError: (String) -> Unit,
    private val onEncodedData: (ByteArray, Boolean, Boolean, Long) -> Unit
) {
    constructor(
        width: Int,
        height: Int,
        fps: Int,
        bitrate: Int,
        onError: (String) -> Unit,
        onEncodedData: (ByteArray, Boolean, Boolean, Long) -> Unit
    ) : this(width, height, fps, bitrate, "h264", onError, onEncodedData)

    private enum class Tuning { LOW_LATENCY, STANDARD, MINIMAL }

    private val isHevc get() = codecName.equals("hevc", ignoreCase = true)
    private val mimeType get() = if (isHevc) MediaFormat.MIMETYPE_VIDEO_HEVC else MediaFormat.MIMETYPE_VIDEO_AVC

    private var codec: MediaCodec? = null
    private var surface: Surface? = null
    val inputSurface: Surface
        get() = surface ?: error("Поверхность кодека ещё не создана")

    @Volatile private var running = false
    @Volatile private var activeBitrate = bitrate
    @Volatile private var lastIdrRequest = 0L
    private var reader: Thread? = null

    @Synchronized
    fun start() {
        check(codec == null) { "Кодек уже запущен" }
        val candidates = encoderCandidates(mimeType, width, height, fps).ifEmpty {
            defaultEncoderCandidate(mimeType, width, height, fps)?.let(::listOf).orEmpty()
        }
        if (candidates.isEmpty()) error("На телефоне нет Surface ${if (isHevc) "HEVC" else "H.264"} encoder")

        val failures = mutableListOf<String>()
        for (candidate in candidates) {
            for (tuning in Tuning.entries) {
                var attemptedCodec: MediaCodec? = null
                var attemptedSurface: Surface? = null
                var started = false
                try {
                    attemptedCodec = MediaCodec.createByCodecName(candidate.info.name)
                    val effectiveBitrate = candidate.clampBitrate(bitrate)
                    val format = buildFormat(candidate, effectiveBitrate, tuning)
                    attemptedCodec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                    attemptedSurface = attemptedCodec.createInputSurface()
                    attemptedCodec.start()
                    started = true

                    codec = attemptedCodec
                    surface = attemptedSurface
                    running = true
                    activeBitrate = effectiveBitrate
                    StreamStats.targetBitrate = effectiveBitrate
                    StreamStats.codec = "${candidate.info.name} · ${tuning.name.lowercase()}"
                    if (effectiveBitrate != bitrate) {
                        StreamStats.controls += "Кодек ограничил битрейт до ${effectiveBitrate / 1_000_000.0} Mbps. "
                    }
                    startDrainThread(attemptedCodec)
                    return
                } catch (e: Exception) {
                    failures += "${candidate.info.name}/${tuning.name}: ${e.message ?: e.javaClass.simpleName}"
                    try { attemptedSurface?.release() } catch (_: Exception) {}
                    if (started) try { attemptedCodec?.stop() } catch (_: Exception) {}
                    try { attemptedCodec?.release() } catch (_: Exception) {}
                }
            }
        }
        val detail = failures.take(4).joinToString("; ")
        error("Не удалось настроить ${if (isHevc) "HEVC" else "H.264"} ${width}x$height@$fps${if (detail.isEmpty()) "" else ": $detail"}")
    }

    private fun buildFormat(candidate: EncoderCandidate, effectiveBitrate: Int, tuning: Tuning): MediaFormat =
        MediaFormat.createVideoFormat(mimeType, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, effectiveBitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            // Surface input can run faster than the output (e.g. Camera2 120 -> 60).
            // Drop before encoding; preserve capture PTS instead of duplicating frames.
            if (Build.VERSION.SDK_INT >= 29) setFloat("max-fps-to-encoder", fps.toFloat())
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)

            if (tuning != Tuning.MINIMAL) {
                setInteger(MediaFormat.KEY_PRIORITY, 0)
                val encoderCaps = candidate.caps.encoderCapabilities
                val bitrateMode = when {
                    encoderCaps?.isBitrateModeSupported(MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR) == true ->
                        MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR
                    encoderCaps?.isBitrateModeSupported(MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_VBR) == true ->
                        MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_VBR
                    else -> null
                }
                bitrateMode?.let { setInteger(MediaFormat.KEY_BITRATE_MODE, it) }
                if (!isHevc) {
                    val profiles = candidate.caps.profileLevels.map { it.profile }
                    val preferredProfile = when {
                        profiles.contains(MediaCodecInfo.CodecProfileLevel.AVCProfileHigh) -> MediaCodecInfo.CodecProfileLevel.AVCProfileHigh
                        profiles.contains(MediaCodecInfo.CodecProfileLevel.AVCProfileMain) -> MediaCodecInfo.CodecProfileLevel.AVCProfileMain
                        profiles.contains(MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline) -> MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline
                        else -> null
                    }
                    preferredProfile?.let { setInteger(MediaFormat.KEY_PROFILE, it) }
                } else {
                    if (candidate.caps.profileLevels.any {
                            it.profile == MediaCodecInfo.CodecProfileLevel.HEVCProfileMain
                        }) {
                        setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.HEVCProfileMain)
                    }
                }
            }

            if (tuning == Tuning.LOW_LATENCY) {
                // Some vendor encoders reject unknown or partially implemented latency keys, so
                // all optional tuning is confined to the first configure attempt.
                setInteger("max-bframes", 0)
                setInteger("latency", 0)
                if (Build.VERSION.SDK_INT >= 30 && candidate.caps.isFeatureSupported(
                        MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency)) {
                    setFeatureEnabled(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency, true)
                }
            }
        }

    private fun startDrainThread(activeCodec: MediaCodec) {
        reader = thread(name = "S8CamEncoder") {
            val info = MediaCodec.BufferInfo()
            try {
                while (running) {
                    when (val index = activeCodec.dequeueOutputBuffer(info, 10_000)) {
                        MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> emitCodecSpecificData(activeCodec.outputFormat)
                        MediaCodec.INFO_TRY_AGAIN_LATER -> Unit
                        else -> if (index >= 0) drainBuffer(activeCodec, index, info)
                    }
                }
            } catch (e: Exception) {
                if (running) onError("Кодек: ${e.message ?: e.javaClass.simpleName}")
            }
        }
    }

    private fun drainBuffer(activeCodec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
        try {
            val buffer = activeCodec.getOutputBuffer(index) ?: return
            if (info.size <= 0) return
            buffer.position(info.offset)
            buffer.limit(info.offset + info.size)
            val raw = ByteArray(info.size)
            buffer.get(raw)
            emitNormalized(
                raw = raw,
                config = info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0,
                keyFrame = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0,
                ptsUs = info.presentationTimeUs
            )
        } catch (e: Exception) {
            // One malformed vendor output buffer must not terminate the drain loop.
            StreamStats.dropped.incrementAndGet()
            Log.w("S8Cam", "Dropped malformed ${if (isHevc) "HEVC" else "H.264"} buffer: ${e.message}")
            requestIdr()
        } finally {
            try { activeCodec.releaseOutputBuffer(index, false) } catch (_: Exception) {}
        }
    }

    private fun emitCodecSpecificData(format: MediaFormat) {
        for (key in listOf("csd-0", "csd-1", "csd-2")) {
            try {
                format.getByteBuffer(key)?.duplicate()?.let { buffer ->
                    val raw = ByteArray(buffer.remaining())
                    buffer.get(raw)
                    emitNormalized(raw, config = true, keyFrame = false, ptsUs = 0)
                }
            } catch (e: Exception) {
                Log.w("S8Cam", "Ignored malformed $key: ${e.message}")
            }
        }
    }

    private fun emitNormalized(raw: ByteArray, config: Boolean, keyFrame: Boolean, ptsUs: Long) {
        val nals = if (isHevc) HevcNal.split(raw) else H264Nal.split(raw)
        if (nals.isEmpty()) {
            Log.w("S8Cam", "Ignored unrecognized ${if (isHevc) "HEVC" else "H.264"} buffer (${raw.size} bytes)")
            if (!config) {
                StreamStats.dropped.incrementAndGet()
                requestIdr()
            }
            return
        }
        val bytes = if (isHevc) HevcNal.annexB(nals) else H264Nal.annexB(nals)
        if (!config) {
            StreamStats.frames.incrementAndGet()
            StreamStats.encodedBytes.addAndGet(bytes.size.toLong())
        }
        onEncodedData(bytes, config, keyFrame, ptsUs)
    }

    @Synchronized
    fun requestIdr() {
        val now = SystemClock.elapsedRealtime()
        if (now - lastIdrRequest < 200 || !running) return
        lastIdrRequest = now
        try {
            codec?.setParameters(Bundle().apply {
                putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0)
            })
        } catch (_: Exception) {
        }
    }

    @Synchronized
    fun setBitrate(requested: Int): Boolean {
        if (!running) return false
        val next = requested.coerceIn(1_000_000, 80_000_000)
        if (next == activeBitrate) return true
        return try {
            codec?.setParameters(Bundle().apply {
                putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, next)
            }) ?: return false
            activeBitrate = next
            StreamStats.targetBitrate = next
            true
        } catch (e: Exception) {
            Log.w("S8Cam", "Dynamic bitrate rejected: ${e.message}")
            false
        }
    }

    fun stop() {
        running = false
        // The drain loop has a bounded dequeue timeout; release only after it has exited.
        reader?.join(2500)
        reader = null
        val activeCodec = codec
        codec = null
        try { activeCodec?.stop() } catch (_: Exception) {}
        try { activeCodec?.release() } catch (_: Exception) {}
        try { surface?.release() } catch (_: Exception) {}
        surface = null
    }

    companion object {
        private data class EncoderBase(
            val info: MediaCodecInfo,
            val caps: MediaCodecInfo.CodecCapabilities,
            val hardware: Boolean,
            val alias: Boolean
        )

        private data class EncoderCandidate(
            val info: MediaCodecInfo,
            val caps: MediaCodecInfo.CodecCapabilities,
            val hardware: Boolean,
            val sizeSupported: Boolean,
            val rateSupported: Boolean,
            val bitrateSupported: Boolean,
            val alias: Boolean
        ) {
            fun clampBitrate(requested: Int): Int {
                val range = caps.videoCapabilities?.bitrateRange ?: return requested
                return requested.coerceIn(range.lower, range.upper)
            }
        }

        private fun encodersForMime(mime: String): List<EncoderBase> {
            return try {
                MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
                    .asSequence()
                    .filter { it.isEncoder }
                    .mapNotNull { info ->
                        val matchingMime = info.supportedTypes.firstOrNull {
                            it.equals(mime, ignoreCase = true)
                        } ?: return@mapNotNull null
                        try {
                            val caps = info.getCapabilitiesForType(matchingMime)
                            if (!caps.colorFormats.contains(MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface))
                                return@mapNotNull null
                            EncoderBase(
                                info = info,
                                caps = caps,
                                hardware = isHardware(info),
                                alias = Build.VERSION.SDK_INT >= 29 && info.isAlias
                            )
                        } catch (_: Exception) {
                            null
                        }
                    }
                    .toList()
            } catch (_: Exception) {
                emptyList()
            }
        }

        private val avcEncoders: List<EncoderBase> by lazy(LazyThreadSafetyMode.SYNCHRONIZED) {
            encodersForMime(MediaFormat.MIMETYPE_VIDEO_AVC)
        }

        private val hevcEncoders: List<EncoderBase> by lazy(LazyThreadSafetyMode.SYNCHRONIZED) {
            encodersForMime(MediaFormat.MIMETYPE_VIDEO_HEVC)
        }

        fun supports(width: Int, height: Int, fps: Int, codec: String = "h264"): Boolean {
            val mime = if (codec.equals("hevc", ignoreCase = true)) MediaFormat.MIMETYPE_VIDEO_HEVC else MediaFormat.MIMETYPE_VIDEO_AVC
            val bases = if (codec.equals("hevc", ignoreCase = true)) hevcEncoders else avcEncoders
            return bases.isEmpty() || encoderCandidates(mime, width, height, fps).any {
                it.sizeSupported && (it.rateSupported || (fps <= 60 && it.hardware))
            }
        }

        private fun encoderCandidates(mime: String, width: Int, height: Int, fps: Int): List<EncoderCandidate> {
            val bases = if (mime.equals(MediaFormat.MIMETYPE_VIDEO_HEVC, ignoreCase = true)) hevcEncoders else avcEncoders
            return bases.mapNotNull { base ->
                try {
                    val video = base.caps.videoCapabilities ?: return@mapNotNull null
                    val sizeSupported = video.isSizeSupported(width, height)
                    val rateSupported = sizeSupported && try {
                        video.areSizeAndRateSupported(width, height, fps.toDouble()) ||
                            (video.supportedFrameRates?.let { fps <= it.upper } ?: false) ||
                            (Build.VERSION.SDK_INT >= 23 && video.getAchievableFrameRatesFor(width, height)?.let { fps <= it.upper } ?: false) ||
                            (fps <= 60 && base.hardware)
                    } catch (_: Exception) {
                        fps <= 60 && base.hardware
                    }
                    EncoderCandidate(
                        info = base.info,
                        caps = base.caps,
                        hardware = base.hardware,
                        sizeSupported = sizeSupported,
                        rateSupported = rateSupported,
                        bitrateSupported = bitrateRangeContains(video.bitrateRange, 1_000_000),
                        alias = base.alias
                    )
                } catch (_: Exception) {
                    null
                }
            }.sortedWith(
                compareByDescending<EncoderCandidate> { it.sizeSupported }
                    .thenByDescending { it.hardware }
                    .thenByDescending { it.rateSupported }
                    .thenByDescending { it.bitrateSupported }
                    .thenBy { it.alias }
                    .thenBy { it.info.name }
            )
        }

        private fun defaultEncoderCandidate(mime: String, width: Int, height: Int, fps: Int): EncoderCandidate? {
            var probe: MediaCodec? = null
            return try {
                probe = MediaCodec.createEncoderByType(mime)
                val info = probe.codecInfo
                val actualMime = info.supportedTypes.firstOrNull {
                    it.equals(mime, ignoreCase = true)
                } ?: mime
                val caps = info.getCapabilitiesForType(actualMime)
                val video = caps.videoCapabilities
                val sizeSupported = try { video?.isSizeSupported(width, height) == true } catch (_: Exception) { false }
                val rateSupported = sizeSupported && try {
                    video?.areSizeAndRateSupported(width, height, fps.toDouble()) == true ||
                        (video?.supportedFrameRates?.let { fps <= it.upper } ?: false) ||
                        (Build.VERSION.SDK_INT >= 23 && video?.getAchievableFrameRatesFor(width, height)?.let { fps <= it.upper } ?: false) ||
                        (fps <= 60 && isHardware(info))
                } catch (_: Exception) {
                    fps <= 60 && isHardware(info)
                }
                EncoderCandidate(
                    info = info,
                    caps = caps,
                    hardware = isHardware(info),
                    sizeSupported = sizeSupported,
                    rateSupported = rateSupported,
                    bitrateSupported = bitrateRangeContains(video?.bitrateRange, 1_000_000),
                    alias = Build.VERSION.SDK_INT >= 29 && info.isAlias
                )
            } catch (_: Exception) {
                null
            } finally {
                try { probe?.release() } catch (_: Exception) {}
            }
        }

        private fun bitrateRangeContains(range: android.util.Range<Int>?, value: Int) =
            range == null || value in range.lower..range.upper

        private fun isHardware(info: MediaCodecInfo): Boolean {
            if (Build.VERSION.SDK_INT >= 29) return info.isHardwareAccelerated
            val name = info.name.lowercase()
            return !(name.startsWith("omx.google.") || name.startsWith("c2.android.") ||
                name.startsWith("c2.google.") || name.contains("ffmpeg") ||
                name.contains("software") || name.contains(".sw."))
        }
    }
}
