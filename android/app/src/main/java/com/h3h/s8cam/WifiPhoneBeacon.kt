package com.h3h.s8cam

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Build
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.SocketTimeoutException

/**
 * Фоновый Wi-Fi радиомаяк и TCP/UDP сервер на смартфоне (активен, пока открыт главный экран H3H Cam).
 * Позволяет Windows-клиенту при нажатии «НАЙТИ» в режиме Wi-Fi мгновенно обнаруживать второй телефон
 * без кабеля и без ADB, считывать список его камер и запускать поток прямо с ПК!
 */
class WifiPhoneBeacon(
    private val context: Context,
    private val onRemoteStart: (StreamSettings) -> Unit
) {
    companion object {
        const val PHONE_PORT = 5006
    }

    @Volatile private var running = false
    private var udpThread: Thread? = null
    private var tcpThread: Thread? = null
    private var udpSocket: DatagramSocket? = null
    private var tcpServer: ServerSocket? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    fun start() {
        if (running) return
        running = true
        try {
            val wm = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            multicastLock = wm?.createMulticastLock("H3HCam::beacon")?.apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (_: Exception) {}

        udpThread = Thread({ runUdpLoop() }, "H3HPhoneBeaconUdp").apply { isDaemon = true; start() }
        tcpThread = Thread({ runTcpLoop() }, "H3HPhoneBeaconTcp").apply { isDaemon = true; start() }
    }

    fun stop() {
        running = false
        try { udpSocket?.close() } catch (_: Exception) {}
        udpSocket = null
        try { tcpServer?.close() } catch (_: Exception) {}
        tcpServer = null
        try { multicastLock?.let { if (it.isHeld) it.release() } } catch (_: Exception) {}
        multicastLock = null
    }

    private fun buildPingPayload(): ByteArray {
        val obj = JSONObject()
            .put("model", Build.MODEL ?: "Android")
            .put("manufacturer", Build.MANUFACTURER ?: "Android")
            .put("phone_port", PHONE_PORT)
        WifiNetBinder.localWifiIpv4(context)?.let { obj.put("phone_ip", it) }
        return ("H3HCAM_DISCOVERY_PING " + obj.toString()).toByteArray(Charsets.UTF_8)
    }

    private fun runUdpLoop() {
        try {
            val sock = DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                soTimeout = 1500
                bind(InetSocketAddress(PHONE_PORT))
            }
            WifiNetBinder.bind(context, sock)
            udpSocket = sock

            var lastAnnounce = 0L
            val buf = ByteArray(8192)

            while (running) {
                val now = System.currentTimeMillis()
                if (!StreamStats.running && now - lastAnnounce >= 2500L) {
                    lastAnnounce = now
                    val ping = buildPingPayload()
                    val targets = WifiNetBinder.wifiBroadcastAddresses(context)
                    for (addr in targets) {
                        for (pcPort in intArrayOf(5005, 5015, 5025)) {
                            try {
                                sock.send(DatagramPacket(ping, ping.size, addr, pcPort))
                            } catch (_: Exception) {}
                        }
                    }
                }

                try {
                    val pkt = DatagramPacket(buf, buf.size)
                    sock.receive(pkt)
                    val msg = String(pkt.data, 0, pkt.length, Charsets.UTF_8)
                    if (msg.startsWith("H3HCAM_PC_SCAN")) {
                        val ping = buildPingPayload()
                        try {
                            sock.send(DatagramPacket(ping, ping.size, pkt.address, pkt.port))
                        } catch (_: Exception) {}
                    }
                } catch (_: SocketTimeoutException) {
                } catch (_: Exception) {
                    if (!running) break
                }
            }
        } catch (_: Exception) {
        }
    }

    private fun runTcpLoop() {
        try {
            val server = ServerSocket().apply {
                reuseAddress = true
                soTimeout = 2000
                bind(InetSocketAddress(PHONE_PORT))
            }
            tcpServer = server

            while (running) {
                val client = try { server.accept() } catch (_: SocketTimeoutException) { continue } catch (_: Exception) { break }
                Thread {
                    client.use { sock ->
                        try {
                            sock.soTimeout = 3000
                            val line = BufferedReader(InputStreamReader(sock.getInputStream(), Charsets.UTF_8)).readLine() ?: return@use
                            val out = sock.getOutputStream()
                            when {
                                line.startsWith("H3HCAM_GET_CAPS") -> {
                                    val caps = CapabilitiesProvider.buildJson(context)
                                    out.write(("H3HCAM_CAPS " + caps + "\n").toByteArray(Charsets.UTF_8))
                                    out.flush()
                                }
                                line.startsWith("H3HCAM_WIFI_START ") -> {
                                    if (StreamStats.running) {
                                        out.write(("H3HCAM_START_ERR Busy\n").toByteArray(Charsets.UTF_8))
                                        out.flush()
                                        return@use
                                    }
                                    val req = JSONObject(line.substringAfter(' ').trim())
                                    val pcIp = req.optString("pc_ip").takeIf { it.isNotEmpty() }
                                        ?: sock.inetAddress.hostAddress ?: error("Missing PC IP")
                                    val rtpPort = req.optInt("port", 5000)
                                    val token = req.optString("token", "")
                                    val discPort = req.optInt("dp", 5005)
                                    val settingsObj = req.getJSONObject("settings")
                                    val cfg = WifiPairClient.buildSettingsFromPcJson(context, pcIp, rtpPort, token, discPort, settingsObj)
                                    out.write("H3HCAM_WIFI_STARTED OK\n".toByteArray(Charsets.UTF_8))
                                    out.flush()
                                    onRemoteStart(cfg)
                                }
                            }
                        } catch (_: Exception) {}
                    }
                }.start()
            }
        } catch (_: Exception) {
        }
    }
}
