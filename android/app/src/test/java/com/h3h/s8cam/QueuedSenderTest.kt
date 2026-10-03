package com.h3h.s8cam

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

class QueuedSenderTest {
    @Test fun overflowDiscardsDependentFramesUntilFreshIdr() {
        var idrRequests = 0
        val received = LinkedBlockingQueue<AccessUnit>()
        val sender = object : QueuedSender({ idrRequests++ }, queueCapacity = 2, monotonicMs = { 1000L }) {
            override fun transmit(unit: AccessUnit) { received.add(unit) }
            override fun disconnect() {}
        }
        StreamStats.dropped.set(0)
        fun frame(type: Int) = H264Nal.annexB(listOf(byteArrayOf(type.toByte(), 1, 2, 3)))
        try {
            sender.offer(H264Nal.annexB(listOf(byteArrayOf(0x67, 1), byteArrayOf(0x68, 1))), 0)
            sender.offer(frame(0x65), 10) // IDR
            sender.offer(frame(0x41), 20) // queue full
            sender.offer(frame(0x41), 30) // discard the entire dependency chain
            assertEquals(1, idrRequests)
            assertEquals(3L, StreamStats.dropped.get())
            sender.offer(frame(0x41), 40) // must not escape while waiting for IDR
            sender.offer(frame(0x65), 50)
            sender.start()
            val recovered = received.poll(2, TimeUnit.SECONDS)
            assertNotNull(recovered)
            assertTrue(recovered!!.idr)
            assertEquals(50L, recovered.pts)
            assertTrue(received.isEmpty())
            val types = H264Nal.split(recovered.bytes).map(H264Nal::type)
            assertTrue(types.containsAll(listOf(7, 8, 5)))
        } finally { sender.close() }
    }
}
