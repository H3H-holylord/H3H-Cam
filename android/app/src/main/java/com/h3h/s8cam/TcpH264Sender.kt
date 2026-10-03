package com.h3h.s8cam

import android.os.SystemClock
import java.io.BufferedOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * RTP over a reliable ADB tunnel. Every RTP packet has a two-byte big-endian
 * length prefix (RFC 4571 framing). RTP timestamps are the original MediaCodec PTS.
 */
class TcpH264Sender(private val port: Int, codec: String = "h264", requestIdr: () -> Unit) : QueuedSender(requestIdr, codec = codec, queueCapacity = 6) {
    constructor(port: Int, requestIdr: () -> Unit) : this(port, "h264", requestIdr)
    @Volatile private var socket: Socket? = null
    @Volatile private var output: BufferedOutputStream? = null
    @Volatile private var writeSince = 0L
    private val watchdog = Executors.newSingleThreadScheduledExecutor()
    private val packetizer = RtpPacketizer({ packet -> writePacket(packet) }, codec = codec)

    init {
        watchdog.scheduleWithFixedDelay({
            val since = writeSince
            if (since != 0L && SystemClock.elapsedRealtime() - since > 3000) disconnect()
        }, 200, 200, TimeUnit.MILLISECONDS)
    }

    override fun transmit(unit: AccessUnit) {
        if (socket == null) {
            check(unit.idr) { "Ожидание IDR для USB reconnect" }
            val connected = Socket()
            connected.tcpNoDelay = true
            connected.sendBufferSize = 1024 * 1024
            connected.connect(InetSocketAddress("127.0.0.1", port), 1000)
            socket = connected
            output = BufferedOutputStream(connected.getOutputStream(), 256 * 1024)
            packetizer.resetAfterLoss()
        }
        writeSince = SystemClock.elapsedRealtime()
        try {
            packetizer.send(unit.bytes, unit.pts)
            output?.flush()
        } finally {
            writeSince = 0L
        }
    }

    private val lenBuf = ByteArray(2)

    private fun writePacket(packet: ByteArray) {
        require(packet.size in 1..65535)
        val stream = output ?: error("USB tunnel closed")
        lenBuf[0] = (packet.size ushr 8).toByte()
        lenBuf[1] = (packet.size and 0xff).toByte()
        stream.write(lenBuf, 0, 2)
        stream.write(packet, 0, packet.size)
        StreamStats.sentBytes.addAndGet((packet.size + 2).toLong())
        StreamStats.packets.incrementAndGet()
    }

    override fun disconnect() {
        val oldOutput = output
        val oldSocket = socket
        output = null
        socket = null
        packetizer.resetAfterLoss()
        try { oldSocket?.close() } catch (_: Exception) {}
        try { oldOutput?.close() } catch (_: Exception) {}
    }

    override fun close() {
        watchdog.shutdownNow()
        super.close()
    }
}
