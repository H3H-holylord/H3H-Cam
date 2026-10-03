package com.h3h.s8cam

import android.content.Context
import android.hardware.usb.UsbAccessory
import android.hardware.usb.UsbManager
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.util.concurrent.atomic.AtomicInteger
import kotlin.concurrent.thread

/** H.264 Annex-B over Android Open Accessory bulk USB. No ADB or network is involved. */
class AoaH264Sender(
    context: Context,
    accessory: UsbAccessory,
    codec: String = "h264",
    private val requestKeyframe: () -> Unit,
    private val onCommand: (JSONObject) -> Unit
) : QueuedSender(requestKeyframe, codec = codec, queueCapacity = 6) {
    constructor(
        context: Context,
        accessory: UsbAccessory,
        requestKeyframe: () -> Unit,
        onCommand: (JSONObject) -> Unit
    ) : this(context, accessory, "h264", requestKeyframe, onCommand)
    private val descriptor = context.getSystemService(UsbManager::class.java).openAccessory(accessory)
        ?: error("Нет разрешения на USB accessory")
    private val input = BufferedInputStream(java.io.FileInputStream(descriptor.fileDescriptor), 64 * 1024)
    private val output = BufferedOutputStream(java.io.FileOutputStream(descriptor.fileDescriptor), 256 * 1024)
    private val sequence = AtomicInteger()
    private val writeLock = Any()
    private val reader = thread(name = "H3HAoaControl") { controlLoop() }

    init {
        sendJson(H3HProtocol.HELLO, JSONObject().put("agent", "H3H Cam Android").put("protocol", H3HProtocol.CURRENT_VERSION).toString())
        sendJson(H3HProtocol.CAPABILITIES, CapabilitiesProvider.buildJson(context))
    }

    override fun transmit(unit: AccessUnit) {
        send(H3HProtocol.Message(H3HProtocol.VIDEO_FRAME, sequence.incrementAndGet(), unit.pts,
            if (unit.idr) H3HProtocol.FLAG_IDR else 0, unit.bytes))
        StreamStats.sentBytes.addAndGet(unit.bytes.size.toLong())
        StreamStats.packets.incrementAndGet()
    }

    private fun controlLoop() {
        try {
            while (running) {
                val message = H3HProtocol.read(input) ?: break
                when (message.type) {
                    H3HProtocol.COMMAND -> {
                        val json = JSONObject(String(message.payload, Charsets.UTF_8))
                        val command = json.optString("command")
                        if (command == "REQUEST_IDR") requestKeyframe()
                        else onCommand(json)
                        sendJson(H3HProtocol.ACK, JSONObject().put("command", command).put("ok", true).toString())
                    }
                    H3HProtocol.HEARTBEAT -> sendJson(H3HProtocol.HEARTBEAT, "{\"ok\":true}")
                }
            }
        } catch (_: Exception) { if (running) StreamStats.state = "USB Direct отключён" }
    }

    private fun sendJson(type: Int, json: String) = send(H3HProtocol.Message(type,
        sequence.incrementAndGet(), android.os.SystemClock.elapsedRealtimeNanos() / 1000, 0, json.toByteArray()))
    private fun send(message: H3HProtocol.Message) = synchronized(writeLock) { H3HProtocol.write(output, message) }
    fun sendTelemetry(json: String) = sendJson(H3HProtocol.TELEMETRY, json)

    override fun disconnect() {
        try { descriptor.close() } catch (_: Exception) {}
    }
    override fun close() {
        super.close()
        reader.interrupt()
        disconnect()
        try { input.close() } catch (_: Exception) {}
        try { output.close() } catch (_: Exception) {}
    }
}
