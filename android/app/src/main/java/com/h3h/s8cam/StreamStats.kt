package com.h3h.s8cam

import org.json.JSONObject
import java.util.concurrent.atomic.AtomicLong

object StreamStats {
    val frames = AtomicLong()
    val encodedBytes = AtomicLong()
    val sentBytes = AtomicLong()
    val packets = AtomicLong()
    val retransmitted = AtomicLong()
    val nackRequests = AtomicLong()
    val dropped = AtomicLong()
    @Volatile var running = false
    @Volatile var state = "Остановлено"
    @Volatile var codec = "—"
    @Volatile var actualFps = 0
    @Volatile var highSpeed = false
    @Volatile var resolution = "—"
    @Volatile var camera = "—"
    @Volatile var controls = ""
    @Volatile var targetBitrate = 0
    @Volatile var snapshot = "{}"
    fun reset() {
        frames.set(0); encodedBytes.set(0); sentBytes.set(0); packets.set(0); dropped.set(0)
        retransmitted.set(0); nackRequests.set(0); targetBitrate = 0
        codec = "starting"; actualFps = 0; highSpeed = false; resolution = "—"; camera = "—"
        controls = ""; state = "Запуск"; running = true
    }
}
