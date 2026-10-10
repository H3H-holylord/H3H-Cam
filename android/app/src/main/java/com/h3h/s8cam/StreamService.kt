package com.h3h.s8cam

import android.app.*
import android.content.*
import android.net.wifi.WifiManager
import android.hardware.usb.UsbAccessory
import android.hardware.usb.UsbManager
import android.os.*
import android.util.Log
import org.json.JSONObject

class StreamService : Service() {
    private val workerThread = HandlerThread("S8CamService").apply { start() }
    private val worker = Handler(workerThread.looper)
    private var camera: CameraStreamer? = null
    private var sender: QueuedSender? = null
    private var wake: PowerManager.WakeLock? = null
    private var wifi: WifiManager.WifiLock? = null
    @Volatile private var alive = true
    private var generation = 0
    private var retryCount = 0
    private var settings = StreamSettings()
    private var accessory: UsbAccessory? = null
    private var started = 0L
    private var previousTime = 0L
    private var previousFrames = 0L
    private var previousBytes = 0L
    private var previousSent = 0L
    private val powerMonitor by lazy { PowerMonitor(this) }
    private val cpuMonitor by lazy { CpuMonitor() }

    override fun onCreate() {
        super.onCreate()
        refreshForegroundNotification()
    }
    private fun refreshForegroundNotification() {
        val notification = notification()
        if (Build.VERSION.SDK_INT >= 30) {
            startForeground(1, notification, android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA)
        } else {
            startForeground(1, notification)
        }
    }
    private fun notification(): Notification {
        val uiContext = UiLanguage.context(this)
        getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel("stream", uiContext.getString(R.string.notification_channel), NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, StreamService::class.java).setAction("STOP"), PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, "stream").setContentTitle(uiContext.getString(R.string.notification_title))
            .setContentText(uiContext.getString(R.string.notification_text))
            .setSmallIcon(android.R.drawable.presence_video_online).setContentIntent(open)
            .addAction(Notification.Action.Builder(null, uiContext.getString(R.string.stop), stop).build()).setOngoing(true).build()
    }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == "UI_LANGUAGE_CHANGED") {
            intent.getStringExtra("language")?.takeIf { it in listOf("auto", "ru", "en") }?.let { UiLanguage.save(this, it) }
            refreshForegroundNotification()
            return START_NOT_STICKY
        }
        if (intent?.action == "STOP") { stopSelf(); return START_NOT_STICKY }
        if (intent?.action == "TAP_FOCUS" || intent?.action == "com.h3h.s8cam.TAP_FOCUS" || intent?.action?.endsWith("TAP_FOCUS") == true) {
            val fx = intent.getFloatExtra("x", 0.5f)
            val fy = intent.getFloatExtra("y", 0.5f)
            worker.post { camera?.triggerTapToFocus(fx, fy) }
            return START_NOT_STICKY
        }
        if (intent?.action == "REQUEST_IDR" || intent?.action == "com.h3h.s8cam.REQUEST_IDR" || intent?.action?.endsWith("REQUEST_IDR") == true) {
            worker.post { camera?.requestIdr() }
            return START_NOT_STICKY
        }
        if (intent?.action == "UPDATE_CONTROLS" || intent?.action == "com.h3h.s8cam.UPDATE_CONTROLS" || intent?.action?.endsWith("UPDATE_CONTROLS") == true) {
            val next = settings.withIntent(intent)
            worker.post {
                if (!alive || camera == null) return@post
                val current = settings
                val effectiveOldFps = if (current.powerMode == "saving" || current.fps <= 30) 30 else current.fps
                val effectiveNewFps = if (next.powerMode == "saving" || next.fps <= 30) 30 else next.fps
                val fpsChanged = effectiveOldFps != effectiveNewFps
                val lensChanged = current.cameraKey != next.cameraKey
                settings = next
                if ((lensChanged || (fpsChanged && camera?.isLegacy() != true)) && sender != null) {
                    val activeSender = sender!!
                    camera?.stop()
                    camera = CameraStreamer(this, next, { message -> worker.post { recover(generation, message) } }) { data, _, _, pts ->
                        offerVideo(activeSender, data, pts)
                    }.also { it.start() }
                    StreamStats.controls = if (lensChanged) "Объектив переключен на лету: ${next.cameraKey}. " else "FPS переключен на лету: ${effectiveNewFps} fps. "
                } else {
                    camera?.setBitrate(next.bitrate)
                    camera?.updateControls(next)
                    StreamStats.controls = "Параметры обновлены на лету. "
                }
            }
            return START_NOT_STICKY
        }
        val next = try { StreamSettings.load(this).let { if (intent != null) it.withIntent(intent) else it }.also { it.save(this) } }
        catch (e: Exception) { StreamStats.state = e.message ?: "Неверные настройки"; stopSelf(); return START_NOT_STICKY }
        worker.post {
            if (!alive) return@post
            val current = settings
            val canHotUpdate = camera != null &&
                current.cameraKey == next.cameraKey &&
                current.width == next.width &&
                current.height == next.height &&
                current.fps == next.fps &&
                current.codec == next.codec &&
                current.transport == next.transport &&
                current.port == next.port &&
                current.forceSamsungLegacy == next.forceSamsungLegacy

            if (canHotUpdate) {
                settings = next
                camera?.setBitrate(next.bitrate)
                camera?.updateControls(next)
                StreamStats.controls = "Параметры обновлены на лету. "
                return@post
            }

            val canLiveSwitchLens = camera != null && sender != null &&
                current.transport == next.transport &&
                current.port == next.port &&
                current.codec == next.codec &&
                current.width == next.width &&
                current.height == next.height

            if (canLiveSwitchLens) {
                settings = next
                val activeSender = sender!!
                camera?.stop()
                camera = CameraStreamer(this, next, { message -> worker.post { recover(generation, message) } }) { data, _, _, pts ->
                    offerVideo(activeSender, data, pts)
                }.also { it.start() }
                StreamStats.controls = "Объектив переключен на лету: ${next.cameraKey}. "
                return@post
            }

            generation++; stopPipeline()
            settings = next; retryCount = 0
            accessory = if (Build.VERSION.SDK_INT >= 33) intent?.getParcelableExtra(UsbManager.EXTRA_ACCESSORY, UsbAccessory::class.java)
                else @Suppress("DEPRECATION") intent?.getParcelableExtra(UsbManager.EXTRA_ACCESSORY)
            StreamStats.reset()
            started = SystemClock.elapsedRealtime(); previousTime = started
            previousFrames = 0; previousBytes = 0; previousSent = 0
            wake = getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "S8Cam::stream").apply {
                acquire()
            }
            if (settings.transport == "wifi") {
                wifi = (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager)
                    .createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "S8Cam::wifi").apply { acquire() }
            }
            startPipeline(generation)
            worker.removeCallbacks(tick); worker.postDelayed(tick, 1000)
        }
        // On modern Android camera access must originate from a visible activity.
        return START_NOT_STICKY
    }
    private fun offerVideo(target: QueuedSender, data: ByteArray, pts: Long) {
        // Keep pacing aligned with the encoder after a live bitrate or lens change.
        val bitrate = StreamStats.targetBitrate
        if (target is RtpH264Sender && bitrate in 1_000_000..80_000_000) target.updateBitrate(bitrate)
        target.offer(data, pts)
    }

    private fun startPipeline(g: Int) {
        if (!alive || generation != g) return
        try {
            val s = when (settings.transport) {
                "direct" -> AoaH264Sender(this, accessory ?: error("USB Direct accessory не подключён"),
                    settings.codec, { camera?.requestIdr() }) { command -> worker.post { applyDirectCommand(command, g) } }
                "usb" -> TcpH264Sender(settings.port, settings.codec) { camera?.requestIdr() }
                else -> RtpH264Sender(settings.ip, settings.port, settings.bitrate, settings.codec,
                    { camera?.requestIdr() },
                    { value -> camera?.setBitrate(value) == true },
                    { command -> worker.post { applyLiveCommand(command) } })
            }
            sender = s; s.start()
            camera = CameraStreamer(this, settings, { message -> worker.post { recover(g, message) } }) { data, _, _, pts ->
                offerVideo(s, data, pts)
            }.also { it.start() }
        } catch (e: Exception) { recover(g, e.message ?: "Ошибка запуска") }
    }
    private fun applyLiveCommand(command: JSONObject) {
        if (!alive) return
        when (command.optString("command")) {
            "TAP_FOCUS" -> {
                val fx = command.optDouble("x", 0.5).toFloat()
                val fy = command.optDouble("y", 0.5).toFloat()
                camera?.triggerTapToFocus(fx, fy)
            }
            "SET_CONTROLS" -> {
                val current = settings
                val newKey = command.optString("cameraKey", current.cameraKey)
                val newFps = command.optInt("fps", current.fps)
                val newPowerMode = command.optString("powerMode", current.powerMode)
                val next = current.copy(
                    cameraKey = newKey,
                    fps = newFps,
                    powerMode = newPowerMode,
                    bitrate = command.optInt("bitrate", current.bitrate),
                    focus = command.optString("focus", current.focus),
                    focusDistance = command.optDouble("focusDistance", current.focusDistance.toDouble()).toFloat(),
                    exposure = command.optInt("exposure", current.exposure),
                    wb = command.optString("wb", current.wb),
                    zoom = command.optDouble("zoom", current.zoom.toDouble()).toFloat(),
                    torch = command.optBoolean("torch", current.torch),
                    lockAeAwb = command.optBoolean("lockAeAwb", current.lockAeAwb),
                    faceTracking = command.optBoolean("faceTracking", current.faceTracking),
                    autoFraming = command.optBoolean("autoFraming", current.autoFraming),
                    autoFramingZoom = command.optDouble("autoFramingZoom", current.autoFramingZoom.toDouble()).toFloat(),
                    autoFramingSpeed = command.optDouble("autoFramingSpeed", current.autoFramingSpeed.toDouble()).toFloat(),
                    autoFramingDeadzone = command.optDouble("autoFramingDeadzone", current.autoFramingDeadzone.toDouble()).toFloat(),
                    manualIso = command.optInt("manualIso", current.manualIso),
                    shutterSpeedNs = command.optLong("shutterSpeedNs", current.shutterSpeedNs),
                    manualWbKelvin = command.optInt("manualWbKelvin", current.manualWbKelvin),
                    stabilization = command.optBoolean("stabilization", current.stabilization),
                    stabilizationMode = command.optString("stabilizationMode", current.stabilizationMode)
                )
                val effectiveOldFps = if (current.powerMode == "saving" || current.fps <= 30) 30 else current.fps
                val effectiveNewFps = if (next.powerMode == "saving" || next.fps <= 30) 30 else next.fps
                val fpsChanged = effectiveOldFps != effectiveNewFps
                val lensChanged = current.cameraKey != newKey
                settings = next
                if ((lensChanged || (fpsChanged && camera?.isLegacy() != true)) && camera != null && sender != null) {
                    val activeSender = sender!!
                    camera?.stop()
                    camera = CameraStreamer(this, next, { message -> worker.post { recover(generation, message) } }) { data, _, _, pts ->
                        offerVideo(activeSender, data, pts)
                    }.also { it.start() }
                    StreamStats.controls = if (lensChanged) "Объектив переключен на лету: $newKey. " else "FPS переключен на лету: ${effectiveNewFps} fps. "
                } else {
                    camera?.setBitrate(next.bitrate)
                    camera?.updateControls(next)
                    StreamStats.controls = "Параметры обновлены на лету. "
                }
            }
        }
    }
    private fun applyDirectCommand(command: JSONObject, g: Int) {
        if (!alive || generation != g) return
        if (command.optString("command") == "TAP_FOCUS") {
            val fx = command.optDouble("x", 0.5).toFloat()
            val fy = command.optDouble("y", 0.5).toFloat()
            camera?.triggerTapToFocus(fx, fy)
            return
        }
        val cmd = command.optString("command")
        if (cmd != "START_STREAM" && cmd != "SET_CONTROLS") return
        val current = settings
        val next = current.copy(
            cameraKey = command.optString("cameraKey", current.cameraKey),
            width = command.optInt("width", current.width), height = command.optInt("height", current.height),
            fps = command.optInt("fps", current.fps), bitrate = command.optInt("bitrate", current.bitrate),
            focus = command.optString("focus", current.focus),
            focusDistance = command.optDouble("focusDistance", current.focusDistance.toDouble()).toFloat(),
            exposure = command.optInt("exposure", current.exposure), wb = command.optString("wb", current.wb),
            powerMode = command.optString("powerMode", current.powerMode),
            codec = command.optString("codec", current.codec),
            torch = command.optBoolean("torch", current.torch),
            zoom = command.optDouble("zoom", current.zoom.toDouble()).toFloat(),
            lockAeAwb = command.optBoolean("lockAeAwb", current.lockAeAwb),
            faceTracking = command.optBoolean("faceTracking", current.faceTracking),
            autoFraming = command.optBoolean("autoFraming", current.autoFraming),
            autoFramingZoom = command.optDouble("autoFramingZoom", current.autoFramingZoom.toDouble()).toFloat(),
            autoFramingSpeed = command.optDouble("autoFramingSpeed", current.autoFramingSpeed.toDouble()).toFloat(),
            autoFramingDeadzone = command.optDouble("autoFramingDeadzone", current.autoFramingDeadzone.toDouble()).toFloat(),
            manualIso = command.optInt("manualIso", current.manualIso),
            shutterSpeedNs = command.optLong("shutterSpeedNs", current.shutterSpeedNs),
            manualWbKelvin = command.optInt("manualWbKelvin", current.manualWbKelvin),
            stabilization = command.optBoolean("stabilization", current.stabilization),
            stabilizationMode = command.optString("stabilizationMode", current.stabilizationMode)
        )
        try {
            next.validate()
            val effectiveOldFps = if (current.powerMode == "saving" || current.fps <= 30) 30 else current.fps
            val effectiveNewFps = if (next.powerMode == "saving" || next.fps <= 30) 30 else next.fps
            val fpsChanged = effectiveOldFps != effectiveNewFps
            val lensChanged = current.cameraKey != next.cameraKey
            val resChanged = current.width != next.width || current.height != next.height
            val codecChanged = current.codec != next.codec

            settings = next
            val activeSender = sender ?: return

            // Recreate pipeline only when hardware format/lens actually changes
            if (camera == null || resChanged || codecChanged || lensChanged || (fpsChanged && camera?.isLegacy() != true)) {
                camera?.stop()
                camera = CameraStreamer(this, next, { message -> worker.post { recover(g, message) } }) { data, _, _, pts ->
                    offerVideo(activeSender, data, pts)
                }.also { it.start() }
                StreamStats.state = "USB Direct · камера переконфигурирована"
            } else {
                // Seamless dynamic update on the fly without stopping camera or dropping frames!
                camera?.setBitrate(next.bitrate)
                camera?.updateControls(next)
                StreamStats.controls = "Параметры USB Direct обновлены на лету. "
            }
        } catch (e: Exception) { StreamStats.state = "USB Direct command: ${e.message}" }
    }
    private fun recover(g: Int, message: String) {
        if (!alive || g != generation) return
        generation++
        val next = generation
        camera?.stop(); camera = null; sender?.close(); sender = null
        StreamStats.codec = "retry"; StreamStats.state = message
        Log.e("S8Cam", message)
        val delay = minOf(10_000L, 1000L shl retryCount.coerceAtMost(3))
        retryCount++
        worker.postDelayed({ startPipeline(next) }, delay)
    }
    private val tick = object : Runnable {
        override fun run() {
            if (!alive) return
            val now = SystemClock.elapsedRealtime()
            val seconds = (now - previousTime).coerceAtLeast(1) / 1000.0
            val frames = StreamStats.frames.get(); val bytes = StreamStats.encodedBytes.get(); val sent = StreamStats.sentBytes.get()
            val measured = (frames - previousFrames) / seconds
            if (measured > 0) retryCount = 0
            val battery = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            val power = powerMonitor.sample(battery)
            val j = JSONObject().put("state", StreamStats.state).put("codec", StreamStats.codec)
                .put("transport", settings.transport).put("resolution", StreamStats.resolution)
                .put("camera", StreamStats.camera).put("sessionId", settings.sessionId)
                .put("requestedFps", settings.fps).put("selectedFps", StreamStats.actualFps).put("fps", measured)
                .put("bitrateMbps", (bytes - previousBytes) * 8 / seconds / 1e6)
                .put("sentMbps", (sent - previousSent) * 8 / seconds / 1e6)
                .put("packets", StreamStats.packets.get()).put("dropped", StreamStats.dropped.get())
                .put("retransmitted", StreamStats.retransmitted.get()).put("nackRequests", StreamStats.nackRequests.get())
                .put("targetBitrateMbps", StreamStats.targetBitrate / 1e6)
                .put("elapsed", (now - started) / 1000).put("controls", StreamStats.controls).put("powerMode", settings.powerMode)
            power.keys().forEach { key -> j.put(key, power.get(key)) }
            cpuMonitor.sampleUsage()?.let { j.put("cpuUsagePercent", it) }
            cpuMonitor.sampleTemperature()?.let { j.put("cpuTemperatureC", it) }
            if (Build.VERSION.SDK_INT >= 29) j.put("thermalStatus", getSystemService(PowerManager::class.java).currentThermalStatus)
            val face = camera?.getFaceInfo()
            if (face != null && face.size >= 7) {
                j.put("faceX", face[0])
                j.put("faceY", face[1])
                j.put("faceW", face[2])
                j.put("faceH", face[3])
                j.put("cropCx", face[4])
                j.put("cropCy", face[5])
                j.put("cropZoom", face[6])
                if (face.size >= 8) {
                    j.put("faceCount", face[7].toInt())
                }
            }
            StreamStats.snapshot = j.toString()
            (sender as? AoaH264Sender)?.let { aoa -> try { aoa.sendTelemetry(j.toString()) } catch (_: Exception) {} }
            Log.i("S8CamStats", j.toString())
            previousTime = now; previousFrames = frames; previousBytes = bytes; previousSent = sent
            worker.postDelayed(this, 1500)
        }
    }
    private fun stopPipeline() {
        try { camera?.stop() } catch (_: Exception) {}
        camera = null
        try { sender?.close() } catch (_: Exception) {}
        sender = null
        try { wifi?.let { if (it.isHeld) it.release() } } catch (_: Exception) {}
        wifi = null
        try { wake?.let { if (it.isHeld) it.release() } } catch (_: Exception) {}
        wake = null
    }
    override fun onDestroy() {
        alive = false
        worker.removeCallbacksAndMessages(null)
        worker.post {
            generation++; stopPipeline()
            StreamStats.running = false; StreamStats.state = "Остановлено"; StreamStats.codec = "stopped"
            workerThread.quitSafely()
        }
        stopForeground(STOP_FOREGROUND_REMOVE)
        super.onDestroy()
    }
    override fun onBind(intent: Intent?): IBinder? = null
}
