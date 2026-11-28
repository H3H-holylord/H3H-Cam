package com.h3h.s8cam

import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.LinkedHashMap
import java.util.concurrent.locks.LockSupport
import kotlin.concurrent.thread

/** RTP/UDP plus a reverse control channel on videoPort + 1. */
class RtpH264Sender(
    ip: String,
    private val port: Int,
    bitrate: Int,
    codec: String = "h264",
    private val requestKeyframe: () -> Unit,
    private val changeBitrate: (Int) -> Boolean,
    private val onCommand: ((JSONObject) -> Unit)? = null,
    private val session: String = "",
    private val appContext: android.content.Context? = null,
    private val discoveryPort: Int = 5005,
    private val monotonicMs: () -> Long = { android.os.SystemClock.elapsedRealtime() }
) : QueuedSender(requestKeyframe, codec = codec, monotonicMs = monotonicMs) {
    constructor(
        ip: String,
        port: Int,
        bitrate: Int,
        requestKeyframe: () -> Unit,
        changeBitrate: (Int) -> Boolean
    ) : this(ip, port, bitrate, "h264", requestKeyframe, changeBitrate, null)
    private val address = InetAddress.getByName(ip)
    @Volatile private var currentBitrate = bitrate
    fun updateBitrate(value: Int) {
        require(value in 1_000_000..80_000_000) { "Invalid transport bitrate" }
        currentBitrate = value
    }
    private val pacingEnabled get() = currentBitrate <= 20_000_000
    private val pacedBitrate get() = (currentBitrate.toLong() * 5 / 4).coerceAtLeast(2_000_000L)
    private var nextPacketNs = 0L
    private var paceCurrentFrame = true
    private val sendLock = Any()
    private val history = object : LinkedHashMap<Int, ByteArray>(768, 0.75f, false) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<Int, ByteArray>?) = size > 768
    }
    // Симметричный UDP-сокет: привязан к port + 1 (5001). ПК отправляет START_STREAM/HEARTBEAT/PUNCH
    // со своего видеопорта 5000 на наш порт 5001, и мы отправляем видео RTP обратно с 5001 на 5000.
    // Это создаёт обратный 4-кортеж, который Stateful Firewall Windows пропускает автоматически даже в Public Wi-Fi.
    // Важно: вычисляем controlPort вне apply {}, т.к. внутри DatagramSocket.apply имя `port` затеняется
    // методом DatagramSocket.getPort() (-1), из-за чего `port + 1` превращалось в 0 (случайный порт)!
    private val controlPort = port + 1
    private val control = DatagramSocket(null).apply {
        reuseAddress = true
        bind(InetSocketAddress(controlPort))
        receiveBufferSize = 64 * 1024
        sendBufferSize = 2 * 1024 * 1024
        trafficClass = 0x10
        soTimeout = 1000
        WifiNetBinder.bind(appContext, this)
    }
    private val controlThread = thread(name = "S8CamRtpControl") { controlLoop() }
    private val packetizer = RtpPacketizer({ bytes ->
        val sequence = ((bytes[2].toInt() and 255) shl 8) or (bytes[3].toInt() and 255)
        synchronized(history) { history[sequence] = bytes.copyOf() }
        if (paceCurrentFrame) pace(bytes.size)
        sendRaw(bytes)
        StreamStats.packets.incrementAndGet()
    }, codec = codec)

    override fun transmit(unit: AccessUnit) {
        paceCurrentFrame = pacingEnabled && !unit.idr
        if (unit.idr) nextPacketNs = 0L
        packetizer.send(unit.bytes, unit.pts)
        if (unit.idr) nextPacketNs = System.nanoTime()
    }

    private fun sendRaw(bytes: ByteArray) {
        synchronized(sendLock) {
            control.send(DatagramPacket(bytes, bytes.size, address, port))
        }
        StreamStats.sentBytes.addAndGet(bytes.size.toLong())
    }

    private fun controlLoop() {
        val buffer = ByteArray(16 * 1024)
        var helloAt = 0L
        var revision = ""
        while (running) try {
            if (session.isNotEmpty() && monotonicMs() - helloAt >= 1000) {
                helloAt = monotonicMs()
                val hello = ("H3HCAM_CONTROL_POLL " + JSONObject().put("token", session).put("revision", revision)
                    .put("telemetry", JSONObject(StreamStats.snapshot))).toByteArray(Charsets.UTF_8)
                synchronized(sendLock) {
                    control.send(DatagramPacket(hello, hello.size, address, discoveryPort))
                }
            }
            val packet = DatagramPacket(buffer, buffer.size)
            control.receive(packet)
            if (packet.address != address) continue
            if (packet.length < 2 || buffer[packet.offset] != '{'.code.toByte()) continue
            val json = JSONObject(String(packet.data, packet.offset, packet.length, Charsets.UTF_8))
            if (session.isNotEmpty() && json.optString("session") != session) continue
            if (json.has("revision")) revision = json.getString("revision")
            when (json.optString("command")) {
                "NACK" -> {
                    StreamStats.nackRequests.incrementAndGet()
                    val sequences = json.optJSONArray("sequences") ?: continue
                    for (i in 0 until minOf(sequences.length(), 64)) {
                        val bytes = synchronized(history) { history[sequences.optInt(i) and 65535] }
                        if (bytes != null) {
                            sendRaw(bytes)
                            StreamStats.retransmitted.incrementAndGet()
                        }
                    }
                }
                "REQUEST_IDR" -> requestKeyframe()
                "SET_BITRATE" -> {
                    val value = json.optInt("bitrate", 0)
                    if (value in 1_000_000..80_000_000 && changeBitrate(value)) {
                        currentBitrate = value
                    }
                }
                "TAP_FOCUS", "SET_CONTROLS", "START_STREAM", "STOP_STREAM", "HEARTBEAT" -> {
                    onCommand?.invoke(json)
                }
            }
        } catch (_: Exception) {
            if (!running) break
        }
    }

    private fun pace(bytes: Int) {
        val now = System.nanoTime()
        if (nextPacketNs == 0L || nextPacketNs < now - 5_000_000L) nextPacketNs = now
        val wait = nextPacketNs - now
        if (wait > 0) LockSupport.parkNanos(wait)
        nextPacketNs += bytes.toLong() * 8_000_000_000L / pacedBitrate
    }

    override fun disconnect() {
        nextPacketNs = 0L; packetizer.resetAfterLoss()
        synchronized(history) { history.clear() }
    }

    override fun close() {
        super.close()
        control.close()
        controlThread.interrupt()
        controlThread.join(1000)
    }
}
