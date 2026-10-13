package com.h3h.s8cam

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.hardware.usb.UsbAccessory
import android.hardware.usb.UsbManager
import android.os.*
import android.view.Gravity
import android.view.WindowManager
import android.view.View
import android.widget.*
import org.json.JSONObject
import java.util.Locale

/** The phone is controlled by Windows so two setting screens cannot conflict. */
open class MainActivity : androidx.activity.ComponentActivity() {
    private var pairing = false
    private var pairingTarget: WifiPairTarget? = null
    private val scanner = registerForActivityResult(com.journeyapps.barcodescanner.ScanContract()) { result ->
        result.contents?.let { openPairing(it) }
    }
    override fun attachBaseContext(base: Context) = super.attachBaseContext(UiLanguage.context(base))
    private val ui = Handler(Looper.getMainLooper())
    private lateinit var root: LinearLayout
    private lateinit var status: TextView
    private lateinit var permission: TextView
    private var launchPending = false
    private var commandPending = false
    private var accessoryPending = false
    private var black = false
    private var wasStreaming = false
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
        launchPending = saved?.getBoolean("launchPending") ?: (this is ControlActivity && intent.action == "com.h3h.s8cam.START")
        commandPending = saved?.getBoolean("commandPending") ?: launchPending
        accessoryPending = saved?.getBoolean("accessoryPending") ?: false
        if (saved == null && intent.action == UsbManager.ACTION_USB_ACCESSORY_ATTACHED) {
            accessoryPending = true
            launchPending = true
            status.text = getString(R.string.usb_attached)
        }
        if (saved == null && intent.action == Intent.ACTION_VIEW) intent.dataString?.let { openPairing(it) }
        intent.data = null
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
        addText(getString(R.string.language_label), 12f)
        val languageValues = listOf("auto", "ru", "en")
        val languagePicker = Spinner(this).apply {
            id = R.id.language_picker
            setPopupBackgroundDrawable(android.graphics.drawable.ColorDrawable(Color.rgb(28, 40, 57)))
            adapter = object : ArrayAdapter<String>(this@MainActivity, android.R.layout.simple_spinner_dropdown_item,
                listOf(getString(R.string.language_auto), "Русский", "English")) {
                override fun getView(position: Int, convertView: View?, parent: android.view.ViewGroup): View =
                    super.getView(position, convertView, parent).apply { (this as TextView).setTextColor(Color.WHITE) }
                override fun getDropDownView(position: Int, convertView: View?, parent: android.view.ViewGroup): View =
                    super.getDropDownView(position, convertView, parent).apply { (this as TextView).setTextColor(Color.WHITE) }
            }
            setSelection(languageValues.indexOf(UiLanguage.selection(this@MainActivity)))
            onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
                override fun onNothingSelected(parent: AdapterView<*>?) {}
                override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                    val value = languageValues[position]
                    if (value == UiLanguage.selection(this@MainActivity)) return
                    UiLanguage.save(this@MainActivity, value)
                    if (StreamStats.running) startService(Intent(this@MainActivity, StreamService::class.java).setAction("UI_LANGUAGE_CHANGED"))
                    recreate()
                }
            }
        }
        root.addView(languagePicker, LinearLayout.LayoutParams(-1, dp(48)))
        addText(getString(R.string.controlled_by_windows), 12f, Color.rgb(54, 215, 180))
        addText(getString(R.string.camera_settings_hint), 16f, Color.WHITE)

        permission = addText(permissionText(), 14f,
            if (hasCameraPermission()) Color.rgb(112, 229, 195) else Color.rgb(245, 184, 92))
        if (!hasCameraPermission()) addButton(getString(R.string.camera_allow)) {
            requestPermissions(arrayOf(Manifest.permission.CAMERA), 10)
        }

        val routes = try { CameraCatalog.list(this) } catch (_: Exception) { emptyList() }
        val modeCount = routes.sumOf { it.modes.size }
        val sixtyCount = routes.sumOf { camera -> camera.modes.count { 60 in it.fps } }
        addText(getString(R.string.camera_catalog_summary, routes.size, modeCount, sixtyCount))

        status = addText(if (StreamStats.running) UiLanguage.state(this@MainActivity, StreamStats.state) else
            getString(R.string.ready_hint), 16f, Color.WHITE)
        addButton(getString(R.string.scan_qr)) {
            if (StreamStats.running || pairing) Toast.makeText(this, getString(R.string.pair_stop_first), Toast.LENGTH_LONG).show()
            else scanner.launch(com.journeyapps.barcodescanner.ScanOptions()
                .setDesiredBarcodeFormats(com.journeyapps.barcodescanner.ScanOptions.QR_CODE)
                .setPrompt(getString(R.string.scan_prompt)).setBeepEnabled(false).setOrientationLocked(false))
        }
        addButton(getString(R.string.connection_help)) {
            android.app.AlertDialog.Builder(this).setTitle(R.string.connection_help)
                .setMessage(R.string.connection_steps).setPositiveButton(android.R.string.ok, null).show()
        }
        addButton(getString(R.string.stop)) {
            launchPending = false
            stopService(Intent(this, StreamService::class.java))
            status.text = getString(R.string.state_stopped)
        }
        addButton(getString(R.string.find_computer)) { discoverWifiPc() }
        addButton(getString(R.string.black_screen)) { showBlackScreen() }
        addText(getString(R.string.transport_hint))
    }

    private fun openPairing(value: String) {
        if (pairing || StreamStats.running) { Toast.makeText(this, getString(R.string.pair_stop_first), Toast.LENGTH_LONG).show(); return }
        val target = try { WifiPairTarget.parse(value) } catch (_: Exception) {
            Toast.makeText(this, getString(R.string.qr_invalid), Toast.LENGTH_LONG).show(); return
        }
        android.app.AlertDialog.Builder(this).setTitle(R.string.wifi_found_title)
            .setMessage(getString(R.string.qr_confirm, target.ip))
            .setPositiveButton(R.string.start) { _, _ ->
                pairingTarget = target
                if (!hasCameraPermission()) requestPermissions(arrayOf(Manifest.permission.CAMERA), 10)
                else connectPairedWifi()
            }.setNegativeButton(R.string.cancel, null).show()
    }

    private fun connectPairedWifi() {
        val target = pairingTarget ?: return
        pairingTarget = null
        pairing = true
        status.text = getString(R.string.wifi_searching)
        Thread {
            try {
                val settings = WifiPairClient.connect(applicationContext, target)
                ui.post {
                    pairing = false
                    if (isFinishing || isDestroyed || !lifecycle.currentState.isAtLeast(androidx.lifecycle.Lifecycle.State.RESUMED)) return@post
                    try {
                        settings.save(this)
                        startForegroundService(settings.applyTo(Intent(this, StreamService::class.java)))
                        status.text = getString(R.string.wifi_started, settings.ip, settings.wifiPort)
                    } catch (e: Exception) { status.text = getString(R.string.wifi_search_failed, e.message ?: "") }
                }
            } catch (e: Exception) {
                ui.post { pairing = false; if (!isFinishing && !isDestroyed) status.text = getString(R.string.wifi_search_failed, e.message ?: "") }
            }
        }.start()
    }

    private fun discoverWifiPc() {
        status.text = getString(R.string.wifi_searching)
        Thread {
            try {
                java.net.DatagramSocket().apply {
                    broadcast = true
                    soTimeout = 2500
                }.use { socket ->
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
                            val pcName = obj.optString("pc_name", getString(R.string.computer))
                            val rawPcIp = obj.optString("pc_ip", "").trim()
                            val pcIp = if (rawPcIp.isNotEmpty() && rawPcIp != "127.0.0.1" && rawPcIp != "0.0.0.0") rawPcIp else (packet.address.hostAddress ?: "")
                            val port = obj.optInt("port", 5000)

                            ui.post {
                                if (isFinishing || isDestroyed) return@post
                                status.text = getString(R.string.wifi_found, pcName, pcIp)
                                val dialog = android.app.AlertDialog.Builder(this)
                                    .setTitle(getString(R.string.wifi_found_title))
                                    .setMessage(getString(R.string.wifi_use_qr, pcName))
                                    .setPositiveButton(android.R.string.ok, null)
                                    .setNegativeButton(getString(R.string.cancel), null)
                                    .create()
                                dialog.show()
                            }
                            return@Thread
                        }
                    }
                }
                }
                ui.post { if (!isFinishing && !isDestroyed) status.text = getString(R.string.wifi_no_response) }
            } catch (e: Exception) {
                ui.post { if (!isFinishing && !isDestroyed) status.text = getString(R.string.wifi_search_failed, e.message ?: getString(R.string.timeout)) }
            }
        }.start()
    }

    private fun hasCameraPermission() =
        checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
    private fun permissionText() =
        if (hasCameraPermission()) getString(R.string.camera_allowed) else getString(R.string.camera_permission_needed)

    private fun showBlackScreen() {
        black = true
        window.attributes = window.attributes.apply { screenBrightness = 0f }
        setContentView(TextView(this).apply {
            setBackgroundColor(Color.BLACK)
            setTextColor(Color.DKGRAY)
            text = getString(R.string.black_screen_hint)
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
                requireNotNull(accessory) { getString(R.string.usb_accessory_missing) }
                accessoryPending = false
                val settings = StreamSettings.load(this).copy(transport = "direct", sessionId = "aoa")
                startForegroundService(settings.applyTo(Intent(this, StreamService::class.java))
                    .putExtra(UsbManager.EXTRA_ACCESSORY, accessory))
                status.text = getString(R.string.usb_waiting)
                return
            }
            if (!commandPending) return
            commandPending = false
            val settings = StreamSettings.load(this).withIntent(intent)
            settings.save(this)
            startForegroundService(settings.applyTo(Intent(this, StreamService::class.java)))
            status.text = getString(R.string.settings_received)
        } catch (e: Exception) {
            Toast.makeText(this, e.message ?: getString(R.string.start_failed), Toast.LENGTH_LONG).show()
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

    override fun onSaveInstanceState(out: Bundle) {
        out.putBoolean("launchPending", launchPending)
        out.putBoolean("commandPending", commandPending)
        out.putBoolean("accessoryPending", accessoryPending)
        super.onSaveInstanceState(out)
    }

    override fun onNewIntent(next: Intent) {
        super.onNewIntent(next)
        setIntent(next)
        if (next.action == Intent.ACTION_VIEW) {
            next.dataString?.let { openPairing(it) }
            next.data = null
        }
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
        if (code == 10 && results.firstOrNull() == PackageManager.PERMISSION_GRANTED) {
            if (pairingTarget != null) connectPairedWifi() else startIfReady()
        }
        else if (code == 10) {
            launchPending = false
            pairingTarget = null
            Toast.makeText(this, getString(R.string.camera_permission_error), Toast.LENGTH_LONG).show()
        }
    }

    private val refresh = object : Runnable {
        override fun run() {
            if (!black && ::status.isInitialized) {
                if (!StreamStats.running && wasStreaming) {
                    wasStreaming = false
                    status.text = getString(R.string.state_stopped)
                }
                if (StreamStats.running) wasStreaming = true
                if (!StreamStats.running && launchPending) status.text = UiLanguage.state(this@MainActivity, StreamStats.state)
                else if (!StreamStats.running) { /* Keep connection instructions and errors visible. */ }
                else try {
                    val j = JSONObject(StreamStats.snapshot)
                    status.text = String.format(
                        Locale.US,
                        getString(R.string.stream_summary),
                        UiLanguage.state(this@MainActivity, StreamStats.state),
                        UiLanguage.camera(this@MainActivity, j.optString("camera", "—")), j.optString("resolution", "—"),
                        j.optInt("requestedFps"), j.optDouble("fps", 0.0), j.optDouble("bitrateMbps", 0.0),
                        j.optLong("packets"), j.optLong("dropped"), StreamStats.codec,
                        j.optString("batteryPercent", "—"), j.optString("batteryStatus", "N/A"),
                        j.optString("batteryTemperatureC", "—")
                    )
                } catch (_: Exception) { status.text = UiLanguage.state(this@MainActivity, StreamStats.state) }
            }
            ui.postDelayed(this, 1000)
        }
    }
}

/** Only ADB shell/system can invoke this entry point (manifest DUMP permission). */
class ControlActivity : MainActivity()
