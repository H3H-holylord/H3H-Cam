package com.h3h.s8cam

import org.junit.Assert.assertEquals
import org.junit.Test

class CaptureTimestampTest {
    @Test fun sensorTimingIsPreservedInsteadOfForcingRequestedFps() {
        var last=1_000_000_000L
        for (gap in listOf(8_000_000L,17_000_000L,33_000_000L,80_000_000L)) {
            val raw=last+gap
            assertEquals(raw,CaptureTimestamp.next(raw,last))
            last=raw
        }
    }
    @Test fun duplicateAndInvalidTimestampRemainMonotonicAtCodecMicrosecondPrecision() {
        assertEquals(1_000_001_000L,CaptureTimestamp.next(1_000_000_000L,1_000_000_000L))
        assertEquals(1_000_001_000L,CaptureTimestamp.next(0,1_000_000_000L))
        assertEquals(42L,CaptureTimestamp.next(0,0) {42L})
    }
}
