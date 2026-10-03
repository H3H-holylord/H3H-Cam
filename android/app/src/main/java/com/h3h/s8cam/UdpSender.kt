package com.h3h.s8cam

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

class UdpSender(
    ip: String,
    private val port: Int
) {

    private val address = InetAddress.getByName(ip)

    private val socket = DatagramSocket().apply {
        sendBufferSize = 4 * 1024 * 1024
    }

    /*
     * Оставляем пакет меньше MTU,
     * чтобы Wi-Fi/роутер не фрагментировал IP-пакеты.
     */
    private val maxPacketSize = 1200

    @Synchronized
    fun send(data: ByteArray) {

        var offset = 0

        while (offset < data.size) {

            val length = minOf(
                maxPacketSize,
                data.size - offset
            )

            val packet = DatagramPacket(
                data,
                offset,
                length,
                address,
                port
            )

            socket.send(packet)

            offset += length
        }
    }

    fun close() {
        try {
            socket.close()
        } catch (_: Exception) {
        }
    }
}