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
    private val onCommand: ((JSONObject) -> Unit)? = null
) : QueuedSender(requestKeyframe, codec = codec) {
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
    @Volatile private var socket: DatagramSocket? = null
    private val history = object : LinkedHashMap<Int, ByteArray>(768, 0.75f, false) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<Int, ByteArray>?) = size > 768
    }
    private val control = DatagramSocket(null).apply {
        reuseAddress = true
        bind(InetSocketAddress(port + 1))
        receiveBufferSize = 64 * 1024
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
        val s = socket ?: DatagramSocket().apply {
            sendBufferSize = 2 * 1024 * 1024
            trafficClass = 0x10
            socket = this
        }
        s.send(DatagramPacket(bytes, bytes.size, address, port))
        StreamStats.sentBytes.addAndGet(bytes.size.toLong())
    }

    private fun controlLoop() {
        val buffer = ByteArray(16 * 1024)
        while (running) try {
            val packet = DatagramPacket(buffer, buffer.size)
            control.receive(packet)
            if (packet.address != address) continue
            val json = JSONObject(String(packet.data, packet.offset, packet.length, Charsets.UTF_8))
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
                "TAP_FOCUS", "SET_CONTROLS" -> {
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
        socket?.close(); socket = null; nextPacketNs = 0L; packetizer.resetAfterLoss()
        synchronized(history) { history.clear() }
    }

    override fun close() {
        super.close()
        control.close()
        controlThread.interrupt()
        controlThread.join(1000)
    }
}
