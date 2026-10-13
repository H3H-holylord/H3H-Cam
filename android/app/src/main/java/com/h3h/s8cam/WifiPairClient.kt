package com.h3h.s8cam

import android.content.Context
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

object WifiPairClient {
    fun connect(context: Context, target: WifiPairTarget): StreamSettings {
        val pc = InetAddress.getByName(target.ip)
        val request = ("H3HCAM_PAIR " + JSONObject().put("token", target.token)
            .put("capabilities", JSONObject(CapabilitiesProvider.buildJson(context)))).toByteArray(Charsets.UTF_8)
        require(request.size <= 60000) { "Camera list is too large" }
        DatagramSocket().use { socket ->
            socket.soTimeout = 2000
            val buffer = ByteArray(16384)
            repeat(10) {
                socket.send(DatagramPacket(request, request.size, pc, 5005))
                try {
                    val packet = DatagramPacket(buffer, buffer.size)
                    socket.receive(packet)
                    if (packet.address != pc || packet.port != 5005) return@repeat
                    val text = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)
                    if (!text.startsWith("H3HCAM_PAIR_READY ")) return@repeat
                    val reply = JSONObject(text.substringAfter(' '))
                    if (reply.optString("token") != target.token) return@repeat
                    require(reply.optBoolean("ready")) { reply.optString("error", "PC is not ready") }
                    val s = reply.getJSONObject("settings")
                    return StreamSettings.load(context).copy(transport = "wifi", ip = target.ip,
                        wifiPort = reply.getInt("port"), sessionId = target.token,
                        cameraKey = s.getString("cameraKey"), width = s.getInt("width"), height = s.getInt("height"),
                        fps = s.getInt("fps"), bitrate = s.getInt("bitrate"), codec = s.getString("codec"),
                        focus = s.getString("focus"), focusDistance = s.getDouble("focusDistance").toFloat(),
                        exposure = s.getInt("exposure"), wb = s.getString("wb"), powerMode = s.getString("powerMode"),
                        torch = s.getBoolean("torch"), zoom = s.getDouble("zoom").toFloat(),
                        lockAeAwb = s.getBoolean("lockAeAwb"), faceTracking = s.getBoolean("faceTracking"),
                        autoFraming = s.getBoolean("autoFraming"), autoFramingZoom = s.getDouble("autoFramingZoom").toFloat(),
                        autoFramingSpeed = s.getDouble("autoFramingSpeed").toFloat(), autoFramingDeadzone = s.getDouble("autoFramingDeadzone").toFloat(),
                        manualIso = s.getInt("manualIso"), shutterSpeedNs = s.getLong("shutterSpeedNs"),
                        manualWbKelvin = s.getInt("manualWbKelvin"), stabilization = s.getBoolean("stabilization"),
                        stabilizationMode = s.getString("stabilizationMode")).also { it.validate() }
                } catch (_: SocketTimeoutException) { }
            }
        }
        error(context.getString(R.string.wifi_no_response))
    }
}
