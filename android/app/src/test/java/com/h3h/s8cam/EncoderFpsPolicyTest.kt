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
    fun `preferVbr enabled for all fps to avoid Huawei Kirin CBR stall and Qualcomm ripple`() {
        assertTrue(H264Encoder.preferVbr(60))
        assertTrue(H264Encoder.preferVbr(30))
        assertTrue(H264Encoder.preferVbr(24))
    }

    @Test
    fun `selectTunings restricts encoder to MINIMAL profile when stall fallback is active`() {
        assertEquals(
            listOf(H264Encoder.Tuning.LOW_LATENCY, H264Encoder.Tuning.STANDARD, H264Encoder.Tuning.MINIMAL),
            H264Encoder.selectTunings(minimalOnly = false)
        )
        assertEquals(
            listOf(H264Encoder.Tuning.MINIMAL),
            H264Encoder.selectTunings(minimalOnly = true)
        )
    }

    @Test
    fun `filterSupportedFps hides unsupported phantom 24 and 25 fps on phones with 30 and 60 ranges`() {
        val huaweiRanges = listOf(7 to 30, 15 to 30, 30 to 30, 30 to 60, 60 to 60)
        assertEquals(
            listOf(30, 60),
            CameraCatalog.filterSupportedFps(huaweiRanges, durationLimit = 60) { true }
        )
        // При ограничении 4K (30 FPS) отсекаем 60, но всё ещё не выдумываем 24/25
        assertEquals(
            listOf(30),
            CameraCatalog.filterSupportedFps(huaweiRanges, durationLimit = 30) { true }
        )
        // Если камера реально поддерживает 24 FPS ([12..24] или [24..24]) — 24 сохраняется
        val cinemaRanges = listOf(12 to 24, 24 to 24, 15 to 30, 30 to 30)
        assertEquals(
            listOf(24, 30),
            CameraCatalog.filterSupportedFps(cinemaRanges, durationLimit = 30) { true }
        )
    }

    @Test
    fun `chooseFpsRange prefers exact upper bound and switches to variable range on minimal fallback`() {
        val ranges = listOf(7 to 30, 15 to 30, 30 to 30, 30 to 60, 60 to 60)
        assertEquals(30 to 30, CameraStreamer.chooseFpsRange(ranges, fps = 30, highSpeed = false, preferVariable = false))
        assertEquals(15 to 30, CameraStreamer.chooseFpsRange(ranges, fps = 30, highSpeed = false, preferVariable = true))
        assertEquals(60 to 60, CameraStreamer.chooseFpsRange(ranges, fps = 60, highSpeed = false, preferVariable = false))
        // Даже если есть только [15..30] и [30..60], при запросе 30 берём [15..30], а не [30..60]
        val noFixed30 = listOf(15 to 30, 30 to 60)
        assertEquals(15 to 30, CameraStreamer.chooseFpsRange(noFixed30, fps = 30, highSpeed = false, preferVariable = false))
    }
}
