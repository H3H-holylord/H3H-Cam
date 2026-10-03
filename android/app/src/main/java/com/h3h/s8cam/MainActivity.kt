package com.h3h.s8cam

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.hardware.usb.UsbAccessory
import android.hardware.usb.UsbManager
import android.os.*
import android.view.Gravity
import android.view.WindowManager
import android.widget.*
import org.json.JSONObject
import java.util.Locale

/** The phone is controlled by Windows so two setting screens cannot conflict. */
open class MainActivity : Activity() {
    private val ui = Handler(Looper.getMainLooper())
    private lateinit var root: LinearLayout
    private lateinit var status: TextView
    private lateinit var permission: TextView
    private var launchPending = false
    private var commandPending = false
    private var accessoryPending = false
    private var black = false
    private val muted = Color.rgb(147, 163, 186)

    override fun onCreate(saved: Bundle?) {
        super.onCreate(saved)
        if (this is ControlActivity) {
            @Suppress("DEPRECATION")
            window.addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
        }
        @Suppress("DEPRECATION")
        window.statusBarColor = Color.rgb(12, 18, 29)
        @Suppress("DEPRECATION")
        window.navigationBarColor = Color.rgb(12, 18, 29)
        buildUi()
        launchPending = this is ControlActivity && intent.action == "com.h3h.s8cam.START"
        commandPending = launchPending
        if (intent.action == UsbManager.ACTION_USB_ACCESSORY_ATTACHED) {
            accessoryPending = true
            launchPending = true
            status.text = "USB Direct подключён · ожидание параметров Windows"
        }
    }

    private fun dp(n: Int) = (resources.displayMetrics.density * n).toInt()

    private fun addText(text: String, size: Float = 14f, color: Int = muted): TextView =
        TextView(this).apply {
            this.text = text
            textSize = size
            setTextColor(color)
            setPadding(0, dp(9), 0, dp(7))
            root.addView(this)
        }

    private fun addButton(text: String, action: () -> Unit) {
        root.addView(Button(this).apply {
            this.text = text
            setOnClickListener { action() }
        }, LinearLayout.LayoutParams(-1, dp(54)).apply { topMargin = dp(8) })
    }

    private fun buildUi() {
        root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(24), dp(20), dp(24), dp(30))
            setBackgroundColor(Color.rgb(12, 18, 29))
        }
        setContentView(ScrollView(this).apply { addView(root); isFillViewport = true })
        addText("H3H CAM", 34f, Color.WHITE).setTypeface(null, Typeface.BOLD)
        addText("CONTROLLED BY WINDOWS  /  H.264", 12f, Color.rgb(54, 215, 180))
        addText("Разрешение, FPS, битрейт, камера, фокус, экспозиция и баланс белого задаются только в Windows-клиенте. На телефоне ничего выравнивать не нужно.", 16f, Color.WHITE)

        permission = addText(permissionText(), 14f,
            if (hasCameraPermission()) Color.rgb(112, 229, 195) else Color.rgb(245, 184, 92))
        if (!hasCameraPermission()) addButton("РАЗРЕШИТЬ КАМЕРУ") {
            requestPermissions(arrayOf(Manifest.permission.CAMERA), 10)
        }

        val routes = try { CameraCatalog.list(this) } catch (_: Exception) { emptyList() }
        val modeCount = routes.sumOf { it.modes.size }
        val sixtyCount = routes.sumOf { camera -> camera.modes.count { 60 in it.fps } }
        addText("Windows-клиент увидит: ${routes.size} модулей, $modeCount режимов, $sixtyCount режимов с 60 FPS.")

        status = addText(if (StreamStats.running) StreamStats.state else
            "Готово · откройте H3H Cam на компьютере", 16f, Color.WHITE)
        addButton("STOP") {
            launchPending = false
            stopService(Intent(this, StreamService::class.java))
            status.text = "Остановлено"
        }
        addButton("📡 НАЙТИ КОМПЬЮТЕР (WI-FI)") { discoverWifiPc() }
        addButton("ЧЁРНЫЙ ЭКРАН") { showBlackScreen() }
        addText("Для Wi-Fi и USB через ADB поток запускается с компьютера. USB Direct запускается после системного запроса Android. Кнопка питания может физически выключить экран, поток продолжит работать.")
    }

    private fun discoverWifiPc() {
        status.text = "Поиск H3H Cam в локальной сети Wi-Fi..."
        Thread {
            try {
                val socket = java.net.DatagramSocket().apply {
                    broadcast = true
                    soTimeout = 2500
                }
                val pingMsg = "H3HCAM_DISCOVERY_PING " + JSONObject().put("model", Build.MODEL).toString()
                val pingBytes = pingMsg.toByteArray(Charsets.UTF_8)
                val broadcastGlobal = java.net.InetAddress.getByName("255.255.255.255")
                socket.send(java.net.DatagramPacket(pingBytes, pingBytes.size, broadcastGlobal, 5005))

                // Also send to subnet directed broadcast
                try {
                    val wm = applicationContext.getSystemService(WIFI_SERVICE) as? android.net.wifi.WifiManager
                    val dhcp = wm?.dhcpInfo
                    if (dhcp != null && dhcp.ipAddress != 0) {
                        val broadcastInt = (dhcp.ipAddress and dhcp.netmask) or dhcp.netmask.inv()
                        val quads = ByteArray(4) { i -> ((broadcastInt shr (i * 8)) and 0xFF).toByte() }
                        val subnetBroadcast = java.net.InetAddress.getByAddress(quads)
                        socket.send(java.net.DatagramPacket(pingBytes, pingBytes.size, subnetBroadcast, 5005))
                    }
                } catch (_: Exception) {}

                val buf = ByteArray(2048)
                val packet = java.net.DatagramPacket(buf, buf.size)
                val deadline = System.currentTimeMillis() + 2500
                while (System.currentTimeMillis() < deadline) {
                    socket.receive(packet)
                    val reply = String(packet.data, 0, packet.length, Charsets.UTF_8)
                    if (reply.contains("H3HCAM_PONG") || reply.contains("H3HCAM_BEACON")) {
                        val jsonStart = reply.indexOf('{')
                        if (jsonStart >= 0) {
                            val obj = JSONObject(reply.substring(jsonStart))
                            val pcName = obj.optString("pc_name", "Компьютер")
                            val rawPcIp = obj.optString("pc_ip", "").trim()
                            val pcIp = if (rawPcIp.isNotEmpty() && rawPcIp != "127.0.0.1" && rawPcIp != "0.0.0.0") rawPcIp else (packet.address.hostAddress ?: "")
                            val port = obj.optInt("port", 5000)

                            socket.close()
                            ui.post {
                                status.text = "Найден: $pcName ($pcIp)"
                                val dialog = android.app.AlertDialog.Builder(this)
                                    .setTitle("H3H Cam найден!")
                                    .setMessage("Подключиться к $pcName ($pcIp:$port) по Wi-Fi?")
                                    .setPositiveButton("СТАРТ") { _, _ ->
                                        val current = StreamSettings.load(this).copy(
                                            transport = "wifi",
                                            ip = pcIp,
                                            wifiPort = port
                                        )
                                        current.save(this)
                                        val serviceIntent = current.applyTo(Intent(this, StreamService::class.java))
                                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                                            startForegroundService(serviceIntent)
                                        } else {
                                            startService(serviceIntent)
                                        }
                                        status.text = "Wi-Fi поток запущен на $pcIp:$port"
                                    }
                                    .setNegativeButton("Отмена", null)
                                    .create()
                                dialog.show()
                            }
                            return@Thread
                        }
                    }
                }
                socket.close()
                ui.post { status.text = "Компьютер не ответил. Убедитесь, что H3H Cam открыта на ПК и они в одной Wi-Fi сети." }
            } catch (e: Exception) {
                ui.post { status.text = "Поиск не удался: ${e.message ?: "таймаут"}" }
            }
        }.start()
    }

    private fun hasCameraPermission() =
        checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
    private fun permissionText() =
        if (hasCameraPermission()) "● Камера разрешена" else "● Нужно один раз разрешить камеру"

    private fun showBlackScreen() {
        black = true
        window.attributes = window.attributes.apply { screenBrightness = 0f }
        setContentView(TextView(this).apply {
            setBackgroundColor(Color.BLACK)
            setTextColor(Color.DKGRAY)
            text = "Коснитесь для возврата\nКнопка питания выключает экран"
            gravity = Gravity.CENTER
            setOnClickListener {
                black = false
                window.attributes = window.attributes.apply { screenBrightness = -1f }
                buildUi()
            }
        })
    }

    private fun startIfReady() {
        if (!launchPending) return
        if (!hasCameraPermission()) {
            requestPermissions(arrayOf(Manifest.permission.CAMERA), 10)
            return
        }
        launchPending = false
        try {
            if (accessoryPending) {
                val accessory = if (Build.VERSION.SDK_INT >= 33)
                    intent.getParcelableExtra(UsbManager.EXTRA_ACCESSORY, UsbAccessory::class.java)
                else @Suppress("DEPRECATION") intent.getParcelableExtra<UsbAccessory>(UsbManager.EXTRA_ACCESSORY)
                requireNotNull(accessory) { "USB accessory не найден" }
                accessoryPending = false
                val settings = StreamSettings.load(this).copy(transport = "direct", sessionId = "aoa")
                startForegroundService(settings.applyTo(Intent(this, StreamService::class.java))
                    .putExtra(UsbManager.EXTRA_ACCESSORY, accessory))
                status.text = "USB Direct · ожидание команды Windows"
                return
            }
            if (!commandPending) return
            commandPending = false
            val settings = StreamSettings.load(this).withIntent(intent)
            settings.save(this)
            startForegroundService(settings.applyTo(Intent(this, StreamService::class.java)))
            status.text = "Параметры Windows приняты · запуск камеры"
        } catch (e: Exception) {
            Toast.makeText(this, e.message ?: "Не удалось запустить", Toast.LENGTH_LONG).show()
        }
    }

    override fun onResume() {
        super.onResume()
        ui.post { startIfReady() }
        ui.post(refresh)
    }

    override fun onPause() {
        ui.removeCallbacks(refresh)
        super.onPause()
    }

    override fun onNewIntent(next: Intent) {
        super.onNewIntent(next)
        setIntent(next)
        if (this is ControlActivity) {
            launchPending = next.action == "com.h3h.s8cam.START"
            commandPending = launchPending
            ui.post { startIfReady() }
        }
    }

    override fun onRequestPermissionsResult(code: Int, permissions: Array<out String>, results: IntArray) {
        super.onRequestPermissionsResult(code, permissions, results)
        if (::permission.isInitialized) {
            permission.text = permissionText()
            permission.setTextColor(if (hasCameraPermission()) Color.rgb(112, 229, 195) else Color.rgb(245, 184, 92))
        }
        if (code == 10 && results.firstOrNull() == PackageManager.PERMISSION_GRANTED) startIfReady()
        else if (code == 10) {
            launchPending = false
            Toast.makeText(this, "Для передачи нужно разрешение камеры", Toast.LENGTH_LONG).show()
        }
    }

    private val refresh = object : Runnable {
        override fun run() {
            if (!black && ::status.isInitialized) {
                if (!StreamStats.running) status.text = StreamStats.state
                else try {
                    val j = JSONObject(StreamStats.snapshot)
                    status.text = String.format(
                        Locale.US,
                        "%s\n%s · %s\n%d → %.1f fps · %.2f Mbps\n%d packets · dropped %d\n%s\nБатарея %s%% · %s · %s °C",
                        StreamStats.state,
                        j.optString("camera", "—"), j.optString("resolution", "—"),
                        j.optInt("requestedFps"), j.optDouble("fps", 0.0), j.optDouble("bitrateMbps", 0.0),
                        j.optLong("packets"), j.optLong("dropped"), StreamStats.codec,
                        j.optString("batteryPercent", "—"), j.optString("batteryStatus", "N/A"),
                        j.optString("batteryTemperatureC", "—")
                    )
                } catch (_: Exception) { status.text = StreamStats.state }
            }
            ui.postDelayed(this, 1000)
        }
    }
}

/** Only ADB shell/system can invoke this entry point (manifest DUMP permission). */
class ControlActivity : MainActivity()
