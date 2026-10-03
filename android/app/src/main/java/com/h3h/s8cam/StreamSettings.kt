package com.h3h.s8cam

import android.content.Context
import android.content.Intent

data class StreamSettings(
    val ip: String = "192.168.1.2",
    val wifiPort: Int = 5000,
    val usbPort: Int = 5002,
    val fps: Int = 30,
    val bitrate: Int = 20_000_000,
    val transport: String = "wifi",
    val autoStart: Boolean = false,
    val focus: String = "continuous",
    val focusDistance: Float = 0f,
    val exposure: Int = 0,
    val wb: String = "auto",
    val cameraKey: String = "auto",
    val width: Int = 1920,
    val height: Int = 1080,
    val sessionId: String = "",
    val powerMode: String = "balanced",
    val torch: Boolean = false,
    val zoom: Float = 1.0f,
    val lockAeAwb: Boolean = false,
    val faceTracking: Boolean = true,
    val autoFraming: Boolean = false,
    val autoFramingZoom: Float = 1.35f,
    val autoFramingSpeed: Float = 1.0f,
    val autoFramingDeadzone: Float = 0.05f,
    val forceSamsungLegacy: Boolean = false,
    val codec: String = "h264",
    val manualIso: Int = 0,
    val shutterSpeedNs: Long = 0L,
    val manualWbKelvin: Int = 0,
    val stabilization: Boolean = true,
    val stabilizationMode: String = "strong"
) {
    val port get() = if (transport == "usb") usbPort else wifiPort

    fun validate() {
        require(transport in listOf("wifi", "usb", "direct")) { "Неизвестный режим" }
        require(stabilizationMode in listOf("standard", "strong")) { "Неверный режим стабилизации" }
        require(wifiPort in 1024..65534 && usbPort in 1024..65535) { "Порты: 1024–65534" }
        require(fps in 10..60) { "FPS: 10–60" }
        require(width in 320..4096 && height in 240..2160) { "Неверное разрешение" }
        require(bitrate in 1_000_000..80_000_000) { "Битрейт: 1–80 Mbps" }
        require(transport != "wifi" || validIpv4(ip)) { "Введите IPv4 компьютера" }
        require(focus in listOf("continuous", "auto", "infinity", "manual") &&
            focusDistance.isFinite() && focusDistance >= 0) { "Неверный режим фокуса" }
        require(wb in listOf("auto", "daylight", "cloudy", "incandescent", "fluorescent")) { "Неверный WB" }
        require(powerMode in listOf("maximum", "balanced", "saving")) { "Неверный Power mode" }
        require(zoom in 0.5f..10.0f) { "Zoom: 0.5–10.0" }
        require(codec in listOf("h264", "hevc")) { "Кодек: h264 или hevc" }
    }

    fun save(context: Context) {
        validate()
        context.getSharedPreferences("s8cam_settings", 0).edit()
            .putString("last_ip", ip).putInt("wifi_port", wifiPort).putInt("usb_port", usbPort)
            .putInt("fps", fps).putInt("bitrate", bitrate).putString("transport", transport)
            .putBoolean("auto_start", autoStart).putString("focus", focus)
            .putFloat("focus_distance", focusDistance).putInt("exposure", exposure).putString("wb", wb)
            .putString("camera_key", cameraKey).putInt("width", width).putInt("height", height)
            .putString("power_mode", powerMode)
            .putBoolean("torch", torch).putFloat("zoom", zoom)
            .putBoolean("lock_ae_awb", lockAeAwb)
            .putBoolean("face_tracking", faceTracking)
            .putBoolean("auto_framing", autoFraming)
            .putFloat("auto_framing_zoom", autoFramingZoom)
            .putFloat("auto_framing_speed", autoFramingSpeed)
            .putFloat("auto_framing_deadzone", autoFramingDeadzone)
            .putBoolean("force_samsung_legacy", forceSamsungLegacy)
            .putString("codec", codec)
            .putInt("manual_iso", manualIso)
            .putLong("shutter_speed_ns", shutterSpeedNs)
            .putInt("manual_wb_kelvin", manualWbKelvin)
            .putBoolean("stabilization", stabilization)
            .putString("stabilization_mode", stabilizationMode).apply()
    }

    fun withIntent(i: Intent): StreamSettings {
        val intentTransport = i.getStringExtra("transport") ?: transport
        return copy(
            ip = i.getStringExtra("ip") ?: ip,
            transport = intentTransport,
            wifiPort = if (intentTransport != "usb") i.getIntExtra("port", i.getIntExtra("wifi_port", wifiPort))
                else i.getIntExtra("wifi_port", wifiPort),
            usbPort = if (intentTransport == "usb") i.getIntExtra("port", i.getIntExtra("usb_port", usbPort))
                else i.getIntExtra("usb_port", usbPort),
            fps = i.getIntExtra("fps", fps),
            bitrate = i.getIntExtra("bitrate", bitrate),
            focus = i.getStringExtra("focus") ?: focus,
            focusDistance = i.getFloatExtra("focus_distance", focusDistance),
            exposure = i.getIntExtra("exposure", exposure),
            wb = i.getStringExtra("wb") ?: wb,
            cameraKey = i.getStringExtra("camera_key") ?: cameraKey,
            width = i.getIntExtra("width", width),
            height = i.getIntExtra("height", height),
            sessionId = i.getStringExtra("session_id") ?: sessionId,
            powerMode = i.getStringExtra("power_mode") ?: powerMode,
            torch = i.getBooleanExtra("torch", torch),
            zoom = i.getFloatExtra("zoom", zoom),
            lockAeAwb = i.getBooleanExtra("lock_ae_awb", lockAeAwb),
            faceTracking = i.getBooleanExtra("face_tracking", faceTracking),
            autoFraming = i.getBooleanExtra("auto_framing", autoFraming),
            autoFramingZoom = i.getFloatExtra("auto_framing_zoom", autoFramingZoom),
            autoFramingSpeed = i.getFloatExtra("auto_framing_speed", autoFramingSpeed),
            autoFramingDeadzone = i.getFloatExtra("auto_framing_deadzone", autoFramingDeadzone),
            forceSamsungLegacy = i.getBooleanExtra("force_legacy", i.getBooleanExtra("force_samsung_legacy", forceSamsungLegacy)),
            codec = i.getStringExtra("codec") ?: codec,
            manualIso = i.getIntExtra("manual_iso", manualIso),
            shutterSpeedNs = i.getLongExtra("shutter_speed_ns", shutterSpeedNs),
            manualWbKelvin = i.getIntExtra("manual_wb_kelvin", manualWbKelvin),
            stabilization = i.getBooleanExtra("stabilization", stabilization),
            stabilizationMode = i.getStringExtra("stabilization_mode") ?: stabilizationMode
        )
    }

    fun applyTo(i: Intent): Intent = i.apply {
        putExtra("ip", ip)
        putExtra("transport", transport)
        putExtra("wifi_port", wifiPort)
        putExtra("usb_port", usbPort)
        putExtra("port", port)
        putExtra("fps", fps)
        putExtra("bitrate", bitrate)
        putExtra("focus", focus)
        putExtra("focus_distance", focusDistance)
        putExtra("exposure", exposure)
        putExtra("wb", wb)
        putExtra("camera_key", cameraKey)
        putExtra("width", width)
        putExtra("height", height)
        putExtra("session_id", sessionId)
        putExtra("power_mode", powerMode)
        putExtra("torch", torch)
        putExtra("zoom", zoom)
        putExtra("lock_ae_awb", lockAeAwb)
        putExtra("face_tracking", faceTracking)
        putExtra("auto_framing", autoFraming)
        putExtra("auto_framing_zoom", autoFramingZoom)
        putExtra("auto_framing_speed", autoFramingSpeed)
        putExtra("auto_framing_deadzone", autoFramingDeadzone)
        putExtra("force_legacy", forceSamsungLegacy)
        putExtra("codec", codec)
        putExtra("manual_iso", manualIso)
        putExtra("shutter_speed_ns", shutterSpeedNs)
        putExtra("manual_wb_kelvin", manualWbKelvin)
        putExtra("stabilization", stabilization)
        putExtra("stabilization_mode", stabilizationMode)
    }

    companion object {
        fun validIpv4(s: String) = s.split('.').let {
            it.size == 4 && it.all { p -> p.isNotEmpty() && p.all(Char::isDigit) && (p.toIntOrNull() ?: -1) in 0..255 }
        }

        fun load(c: Context): StreamSettings {
            val p = c.getSharedPreferences("s8cam_settings", 0)
            return StreamSettings(
                ip = p.getString("last_ip", "192.168.1.2")!!,
                wifiPort = p.getInt("wifi_port", 5000),
                usbPort = p.getInt("usb_port", 5002),
                fps = p.getInt("fps", 30),
                bitrate = p.getInt("bitrate", 20_000_000),
                transport = p.getString("transport", "wifi")!!,
                autoStart = p.getBoolean("auto_start", false),
                focus = p.getString("focus", "continuous")!!,
                focusDistance = p.getFloat("focus_distance", 0f),
                exposure = p.getInt("exposure", 0),
                wb = p.getString("wb", "auto")!!,
                cameraKey = p.getString("camera_key", "auto")!!,
                width = p.getInt("width", 1920),
                height = p.getInt("height", 1080),
                powerMode = p.getString("power_mode", "balanced")!!,
                torch = p.getBoolean("torch", false),
                zoom = p.getFloat("zoom", 1.0f),
                lockAeAwb = p.getBoolean("lock_ae_awb", false),
                faceTracking = p.getBoolean("face_tracking", true),
                autoFraming = p.getBoolean("auto_framing", false),
                autoFramingZoom = p.getFloat("auto_framing_zoom", 1.35f),
                autoFramingSpeed = p.getFloat("auto_framing_speed", 1.0f),
                autoFramingDeadzone = p.getFloat("auto_framing_deadzone", 0.05f),
                forceSamsungLegacy = p.getBoolean("force_samsung_legacy", false),
                codec = p.getString("codec", "h264")!!,
                manualIso = p.getInt("manual_iso", 0),
                shutterSpeedNs = p.getLong("shutter_speed_ns", 0L),
                manualWbKelvin = p.getInt("manual_wb_kelvin", 0),
                stabilization = p.getBoolean("stabilization", true),
                stabilizationMode = p.getString("stabilization_mode", "strong") ?: "strong"
            )
        }
    }
}
