package com.h3h.s8cam

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress

class RtpSymmetricSocketTest {

    private fun allocateFreePortPair(): Int {
        for (port in 26000..28000 step 2) {
            try {
                DatagramSocket(null).use { a ->
                    a.reuseAddress = true
                    a.bind(InetSocketAddress(InetAddress.getByName("127.0.0.1"), port))
                    DatagramSocket(null).use { b ->
                        b.reuseAddress = true
                        b.bind(InetSocketAddress(InetAddress.getByName("127.0.0.1"), port + 1))
                        return port
                    }
                }
            } catch (_: Exception) {
            }
        }
        error("Не удалось найти свободную пару UDP-портов для теста")
    }

    @Test
    fun `RTP video packets originate from control port plus one for stateful firewall traversal`() {
        val videoPort = allocateFreePortPair()
        val expectedControlPort = videoPort + 1
        val loopback = InetAddress.getByName("127.0.0.1")

        DatagramSocket(null).use { pcReceiver ->
            pcReceiver.reuseAddress = true
            pcReceiver.soTimeout = 3000
            pcReceiver.bind(InetSocketAddress(loopback, videoPort))

            val sender = RtpH264Sender(
                ip = "127.0.0.1",
                port = videoPort,
                bitrate = 8_000_000,
                codec = "h264",
                requestKeyframe = {},
                changeBitrate = { true },
                onCommand = null,
                session = "",
                appContext = null,
                discoveryPort = 5005,
                monotonicMs = { 1000L }
            )
            try {
                sender.start()

                // Имитируем UDP-punch от ПК с видеопорта 5000 на порт управления телефона 5001
                val punch = "H3HCAM_PUNCH".toByteArray(Charsets.UTF_8)
                pcReceiver.send(DatagramPacket(punch, punch.size, loopback, expectedControlPort))

                // Подаём валидный Annex-B кадр: SPS (type 7) + PPS (type 8) + IDR (type 5)
                val annexB = byteArrayOf(
                    0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1E,
                    0, 0, 0, 1, 0x68, 0xCE.toByte(), 0x3C, 0x80.toByte(),
                    0, 0, 0, 1, 0x65, 0x88.toByte(), 0x84.toByte(), 0x21, 0x10
                )
                sender.offer(annexB, 33_333L)

                val buf = ByteArray(2048)
                val received = DatagramPacket(buf, buf.size)
                pcReceiver.receive(received)

                assertTrue("Ожидался RTP-пакет >= 12 байт", received.length >= 12)
                assertEquals(
                    "Видео RTP должно уходить с того же порта port+1 (симметричный UDP)",
                    expectedControlPort,
                    received.port
                )
            } finally {
                sender.close()
            }
        }
    }
}
