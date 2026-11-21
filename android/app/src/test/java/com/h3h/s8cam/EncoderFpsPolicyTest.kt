package com.h3h.s8cam

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class EncoderFpsPolicyTest {

    @Test
    fun `maxFpsToEncoder is disabled when input matches output fps`() {
        assertNull(H264Encoder.maxFpsToEncoder(outputFps = 60, inputFps = 60))
        assertNull(H264Encoder.maxFpsToEncoder(outputFps = 30, inputFps = 30))
        assertNull(H264Encoder.maxFpsToEncoder(outputFps = 60, inputFps = 30))
    }

    @Test
    fun `maxFpsToEncoder adds jitter headroom only when downsampling high-speed input`() {
        assertEquals(61.5f, H264Encoder.maxFpsToEncoder(outputFps = 60, inputFps = 120))
        assertEquals(61.5f, H264Encoder.maxFpsToEncoder(outputFps = 60, inputFps = 240))
    }

    @Test
    fun `preferVbr enabled for 60fps to avoid Qualcomm CBR ripple`() {
        assertTrue(H264Encoder.preferVbr(60))
        assertFalse(H264Encoder.preferVbr(30))
    }
}
