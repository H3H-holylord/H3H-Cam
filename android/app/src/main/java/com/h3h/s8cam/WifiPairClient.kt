package com.h3h.s8cam

import android.content.Context
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException

object WifiPairClient {
    fun connect(context: Context, target: WifiPairTarget): StreamSettings {
        val payloadStr = "H3HCAM_PAIR " + JSONObject().put("token", target.token)
            .put("capabilities", JSONObject(CapabilitiesProvider.buildJson(context))).toString()
        val request = payloadStr.toByteArray(Charsets.UTF_8)
        require(request.size <= 60000) { "Camera list is too large" }

        val candidates = WifiNetBinder.prioritizeCandidateIps(context, listOf(target.ip) + target.altIps)
        val discPort = target.discoveryPort
        var lastError: Throwable? = null

        // 1. Сначала пробуем быстрый TCP-канал на discoveryPort (проходит сквозь фильтры UDP и не теряет крупные пакеты capabilities > MTU!)
        for (hostIp in candidates) {
            for (bindToWifi in listOf(true, false)) {
                try {
                    Socket().use { tcp ->
                        if (bindToWifi) WifiNetBinder.bind(context, tcp)
                        tcp.soTimeout = 1800
                        tcp.tcpNoDelay = true
                        tcp.connect(InetSocketAddress(hostIp, discPort), 1400)
                        val out = tcp.getOutputStream()
                        out.write((payloadStr + "\n").toByteArray(Charsets.UTF_8))
                        out.flush()
                        val line = BufferedReader(InputStreamReader(tcp.getInputStream(), Charsets.UTF_8)).readLine()
                        if (line != null && line.startsWith("H3HCAM_PAIR_READY ")) {
                            return parsePairReply(context, hostIp, target.token, discPort, line)
                        }
                    }
                } catch (e: Exception) {
                    lastError = e
                    if (!bindToWifi || !WifiNetBinder.isEperm(e)) break
                }
            }
        }

        // 2. Классический UDP-обмен (сначала привязанный к Wi-Fi в обход VPN, затем стандартный сокет)
        for (bindToWifi in listOf(true, false)) {
            try {
                DatagramSocket().use { socket ->
                    if (bindToWifi) WifiNetBinder.bind(context, socket)
                    socket.soTimeout = 1400
                    val buffer = ByteArray(16384)
                    repeat(4) {
                        var sentAny = false
                        for (hostIp in candidates) {
                            try {
                                val pc = InetAddress.getByName(hostIp)
                                socket.send(DatagramPacket(request, request.size, pc, discPort))
                                sentAny = true
                            } catch (sendEx: Exception) {
                                lastError = sendEx
                            }
                        }
                        if (!sentAny && lastError != null) throw lastError!!

                        val deadline = System.currentTimeMillis() + 1200
                        while (System.currentTimeMillis() < deadline) {
                            try {
                                val packet = DatagramPacket(buffer, buffer.size)
                                socket.receive(packet)
                                val senderIp = packet.address.hostAddress ?: target.ip
                                if (packet.port != discPort) continue
                                val text = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)
                                if (!text.startsWith("H3HCAM_PAIR_READY ")) continue
                                return parsePairReply(context, senderIp, target.token, discPort, text)
                            } catch (_: SocketTimeoutException) {
                                break
                            }
                        }
                    }
                }
                break
            } catch (e: Exception) {
                lastError = e
                if (!bindToWifi) break
            }
        }

        if (lastError != null && WifiNetBinder.isEperm(lastError)) {
            throw IllegalStateException(WifiNetBinder.formatError(context, lastError))
        }
        error(context.getString(R.string.wifi_no_response))
    }

    fun parsePairReply(context: Context, hostIp: String, expectedToken: String, discPort: Int, text: String): StreamSettings {
        val reply = JSONObject(text.substringAfter(' ').trim())
        require(reply.optString("token") == expectedToken) { "Token mismatch" }
        require(reply.optBoolean("ready")) { reply.optString("error", "PC is not ready") }
        val s = reply.getJSONObject("settings")
        return buildSettingsFromPcJson(context, hostIp, reply.getInt("port"), expectedToken, discPort, s)
    }

    fun buildSettingsFromPcJson(
        context: Context,
        hostIp: String,
        rtpPort: Int,
        sessionToken: String,
        discPort: Int,
        s: JSONObject
    ): StreamSettings {
        return StreamSettings.load(context).copy(
            transport = "wifi",
            ip = hostIp,
            wifiPort = rtpPort,
            sessionId = sessionToken,
            cameraKey = s.optString("cameraKey", "auto"),
            width = s.optInt("width", 1920),
            height = s.optInt("height", 1080),
            fps = s.optInt("fps", 30),
            bitrate = s.optInt("bitrate", 12_000_000),
            codec = s.optString("codec", "h264"),
            focus = s.optString("focus", "continuous"),
            focusDistance = s.optDouble("focusDistance", 0.0).toFloat(),
            exposure = s.optInt("exposure", 0),
            wb = s.optString("wb", "auto"),
            powerMode = s.optString("powerMode", "balanced"),
            torch = s.optBoolean("torch", false),
            zoom = s.optDouble("zoom", 1.0).toFloat(),
            lockAeAwb = s.optBoolean("lockAeAwb", false),
            faceTracking = s.optBoolean("faceTracking", false),
            autoFraming = s.optBoolean("autoFraming", false),
            autoFramingZoom = s.optDouble("autoFramingZoom", 1.35).toFloat(),
            autoFramingSpeed = s.optDouble("autoFramingSpeed", 1.0).toFloat(),
            autoFramingDeadzone = s.optDouble("autoFramingDeadzone", 0.05).toFloat(),
            manualIso = s.optInt("manualIso", 0),
            shutterSpeedNs = s.optLong("shutterSpeedNs", 0L),
            manualWbKelvin = s.optInt("manualWbKelvin", 0),
            stabilization = s.optBoolean("stabilization", false),
            stabilizationMode = s.optString("stabilizationMode", "standard"),
            audioEnabled = s.optBoolean("audioEnabled", false),
            audioPort = s.optInt("audioPort", 5004),
            audioSource = s.optString("audioSource", "voice_comm"),
            audioNoiseSuppression = s.optBoolean("audioNoiseSuppression", true),
            discoveryPort = discPort
        ).also { it.validate() }
    }
}
