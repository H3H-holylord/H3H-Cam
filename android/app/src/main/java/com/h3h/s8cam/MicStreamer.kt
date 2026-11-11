package com.h3h.s8cam

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.NoiseSuppressor
import android.os.Process
import android.os.SystemClock
import java.io.BufferedOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.atomic.AtomicInteger
import kotlin.concurrent.thread
import kotlin.math.log10
import kotlin.math.sqrt

/**
 * Low-latency 48 kHz 16-bit PCM mono microphone capture for H3H Cam.
 * Frame size = 10 ms (480 samples = 960 bytes). With 32-byte H3H header = 992 bytes (< 1500 MTU).
 */
class MicStreamer(
    private val context: Context,
    private val sourceMode: String,
    private val enableNoiseSuppression: Boolean,
    private val onAudioFrame: (pcmBytes: ByteArray, ptsUs: Long, flags: Int) -> Unit
) {
    @Volatile private var running = false
    private var captureThread: Thread? = null

    fun start(): Boolean {
        if (running) return true
        if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            StreamStats.audioActive = false
            StreamStats.audioLevelDb = -90.0
            return false
        }
        running = true
        captureThread = thread(name = "H3HMicCapture", start = true) { captureLoop() }
        return true
    }

    @SuppressLint("MissingPermission")
    private fun captureLoop() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        val audioSource = when (sourceMode) {
            "camcorder" -> MediaRecorder.AudioSource.CAMCORDER
            "unprocessed" -> MediaRecorder.AudioSource.UNPROCESSED
            else -> MediaRecorder.AudioSource.VOICE_COMMUNICATION
        }
        val preferredRates = intArrayOf(48000, 44100)
        var recorder: AudioRecord? = null
        var sampleRate = 48000

        for (rate in preferredRates) {
            val minBuf = AudioRecord.getMinBufferSize(
                rate,
                AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT
            )
            if (minBuf <= 0) continue
            val frameBytes = (rate / 100) * 2 // 10 ms frame
            val recBufSize = maxOf(minBuf * 2, frameBytes * 4)
            try {
                val candidate = AudioRecord(
                    audioSource,
                    rate,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    recBufSize
                )
                if (candidate.state == AudioRecord.STATE_INITIALIZED) {
                    recorder = candidate
                    sampleRate = rate
                    break
                } else {
                    candidate.release()
                }
            } catch (_: Exception) { }
        }

        if (recorder == null) {
            StreamStats.audioActive = false
            return
        }

        var ns: NoiseSuppressor? = null
        var aec: AcousticEchoCanceler? = null
        try {
            val sessionId = recorder.audioSessionId
            if (enableNoiseSuppression && NoiseSuppressor.isAvailable()) {
                ns = NoiseSuppressor.create(sessionId)?.apply { enabled = true }
            }
            if (enableNoiseSuppression && AcousticEchoCanceler.isAvailable()) {
                aec = AcousticEchoCanceler.create(sessionId)?.apply { enabled = true }
            }

            recorder.startRecording()
            StreamStats.audioActive = true

            // Flags: bits 0..7 = channels (1), bits 8..15 = sampleRate / 100 (e.g. 480 for 48kHz, 441 for 44.1kHz)
            val flags = 1 or ((sampleRate / 100) shl 8)
            val frameSamples = sampleRate / 100 // 480 samples (10 ms)
            val frameBytes = frameSamples * 2
            val buffer = ByteArray(frameBytes)

            while (running) {
                var offset = 0
                while (offset < frameBytes && running) {
                    val read = recorder.read(buffer, offset, frameBytes - offset)
                    if (read <= 0) break
                    offset += read
                }
                if (offset < frameBytes || !running) break

                // Compute RMS dBFS for telemetry
                var sumSq = 0.0
                var i = 0
                while (i < frameBytes) {
                    val sample = ((buffer[i + 1].toInt() shl 8) or (buffer[i].toInt() and 0xFF)).toShort().toDouble()
                    sumSq += sample * sample
                    i += 2
                }
                val rms = sqrt(sumSq / frameSamples) / 32768.0
                StreamStats.audioLevelDb = if (rms > 1e-5) 20.0 * log10(rms) else -90.0

                val ptsUs = SystemClock.elapsedRealtimeNanos() / 1000L
                onAudioFrame(buffer.copyOf(), ptsUs, flags)
            }
        } catch (_: Exception) {
        } finally {
            StreamStats.audioActive = false
            StreamStats.audioLevelDb = -90.0
            try { ns?.release() } catch (_: Exception) {}
            try { aec?.release() } catch (_: Exception) {}
            try { recorder.stop() } catch (_: Exception) {}
            try { recorder.release() } catch (_: Exception) {}
        }
    }

    fun stop() {
        running = false
        captureThread?.interrupt()
        try { captureThread?.join(600) } catch (_: Exception) {}
        captureThread = null
        StreamStats.audioActive = false
        StreamStats.audioLevelDb = -90.0
    }
}

/**
 * Transports H3HProtocol.AUDIO frames over UDP (Wi-Fi) or TCP via adb reverse (USB).
 */
class AudioNetSender(
    private val transport: String,
    private val host: String,
    private val port: Int,
    private val appContext: Context? = null
) : AutoCloseable {
    private val seq = AtomicInteger()
    @Volatile private var closed = false
    private var udpSocket: DatagramSocket? = null
    private val targetAddress by lazy { InetAddress.getByName(host) }
    private var tcpSocket: Socket? = null
    private var tcpOut: BufferedOutputStream? = null
    private var lastConnectAttemptMs = 0L
    private val lock = Any()

    fun send(pcmBytes: ByteArray, ptsUs: Long, flags: Int) {
        if (closed) return
        val message = H3HProtocol.Message(
            H3HProtocol.AUDIO,
            seq.incrementAndGet(),
            ptsUs,
            flags,
            pcmBytes
        )
        try {
            if (transport == "usb") {
                synchronized(lock) {
                    if (tcpOut == null) {
                        val now = SystemClock.elapsedRealtime()
                        if (now - lastConnectAttemptMs < 1000L) return
                        lastConnectAttemptMs = now
                        val s = Socket()
                        s.tcpNoDelay = true
                        s.sendBufferSize = 128 * 1024
                        s.connect(InetSocketAddress("127.0.0.1", port), 400)
                        tcpSocket = s
                        tcpOut = BufferedOutputStream(s.getOutputStream(), 16 * 1024)
                    }
                    H3HProtocol.write(tcpOut!!, message)
                }
            } else {
                val datagramBytes = H3HProtocol.encode(message)
                val sock = udpSocket ?: DatagramSocket().apply {
                    sendBufferSize = 256 * 1024
                    trafficClass = 0x10
                    WifiNetBinder.bind(appContext, this)
                    udpSocket = this
                }
                sock.send(DatagramPacket(datagramBytes, datagramBytes.size, targetAddress, port))
            }
            StreamStats.audioPackets.incrementAndGet()
        } catch (_: Exception) {
            if (transport == "usb") {
                synchronized(lock) {
                    try { tcpOut?.close() } catch (_: Exception) {}
                    try { tcpSocket?.close() } catch (_: Exception) {}
                    tcpOut = null
                    tcpSocket = null
                }
            }
        }
    }

    override fun close() {
        closed = true
        synchronized(lock) {
            try { udpSocket?.close() } catch (_: Exception) {}
            udpSocket = null
            try { tcpOut?.close() } catch (_: Exception) {}
            tcpOut = null
            try { tcpSocket?.close() } catch (_: Exception) {}
            tcpSocket = null
        }
    }
}
