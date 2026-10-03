package com.h3h.s8cam

import android.os.SystemClock
import android.util.Log
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

data class AccessUnit(val bytes: ByteArray, val pts: Long, val idr: Boolean)

/** Never block MediaCodec on a socket. After queue overflow, resume only at an IDR. */
abstract class QueuedSender(
    private val requestIdr: () -> Unit,
    val codec: String = "h264",
    queueCapacity: Int = 6,
    private val monotonicMs: () -> Long = { SystemClock.elapsedRealtime() }
) {
    constructor(requestIdr: () -> Unit, queueCapacity: Int = 6) : this(requestIdr, "h264", queueCapacity)

    private val isHevc = codec.equals("hevc", ignoreCase = true) || codec.equals("h265", ignoreCase = true)
    private val queue = ArrayBlockingQueue<AccessUnit>(queueCapacity)
    private val h264Config = ParameterSets()
    private val hevcConfig = HevcParameterSets()
    @Volatile protected var running = true
    @Volatile private var needIdr = true
    @Volatile private var retryAfter = 0L
    private var worker: Thread? = null
    fun start() {
        worker = thread(name = "S8CamNetwork") {
            while (running) {
                val unit = queue.poll(200, TimeUnit.MILLISECONDS) ?: continue
                try {
                    transmit(unit)
                    StreamStats.state = "Передача"
                } catch (e: Exception) {
                    if (!running) break
                    Log.w("S8Cam", "Transport reconnect: ${e.message}")
                    StreamStats.state = "Переподключение: ${e.message}"
                    synchronized(this) { queue.clear(); needIdr = true; retryAfter = SystemClock.elapsedRealtime() + 200 }
                    disconnect()
                    requestIdr()
                }
            }
        }
    }
    @Synchronized fun offer(data: ByteArray, pts: Long) {
        val nals = if (isHevc) HevcNal.split(data) else H264Nal.split(data)
        if (isHevc) {
            hevcConfig.update(nals)
        } else {
            h264Config.update(nals)
        }
        val media = if (isHevc) {
            nals.filter { HevcNal.type(it) !in listOf(32, 33, 34, 35, 39, 40) }
        } else {
            nals.filter { H264Nal.type(it) !in listOf(7, 8, 9) }
        }
        val hasMedia = if (isHevc) {
            media.any { HevcNal.type(it) in 0..31 }
        } else {
            media.any { H264Nal.type(it) in 1..5 }
        }
        if (!hasMedia) return

        if (!running || monotonicMs() < retryAfter) {
            StreamStats.dropped.incrementAndGet()
            return
        }

        val idr = if (isHevc) {
            media.any { HevcNal.type(it) in 19..21 }
        } else {
            media.any { H264Nal.type(it) == 5 }
        }
        val ready = if (isHevc) hevcConfig.ready else h264Config.ready

        if (!ready || (needIdr && !idr)) {
            StreamStats.dropped.incrementAndGet()
            requestIdr()
            return
        }

        val bytes = if (isHevc) {
            HevcNal.annexB(listOf(byteArrayOf(0x46, 1, 0x50)) + (if (idr) hevcConfig.all() else emptyList()) + media)
        } else {
            H264Nal.annexB(listOf(byteArrayOf(9, 0xf0.toByte())) + (if (idr) h264Config.all() else emptyList()) + media)
        }
        val unit = AccessUnit(bytes, pts, idr)
        if (idr) {
            queue.clear()
            queue.offer(unit)
            needIdr = false
        } else {
            if (!queue.offer(unit)) {
                // Delta frames depend on earlier pictures. Dropping only the oldest P-frame
                // corrupts every dependent picture until the next keyframe, even over TCP.
                StreamStats.dropped.addAndGet(queue.size.toLong() + 1)
                queue.clear()
                needIdr = true
                requestIdr()
            }
        }
    }
    protected abstract fun transmit(unit: AccessUnit)
    protected abstract fun disconnect()
    open fun close() {
        running = false; disconnect(); queue.clear()
        worker?.join(2000)
    }
}
