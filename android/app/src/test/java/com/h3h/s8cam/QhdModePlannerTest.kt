package com.h3h.s8cam

import org.junit.Assert.*
import org.junit.Test

class QhdModePlannerTest {
    private val fhd = VideoMode(1920, 1080, listOf(30,60))
    @Test fun nativeQhdRemainsDirect() {
        val qhd = VideoMode(2560,1440,listOf(30))
        val modes = QhdModePlanner.add(listOf(fhd,qhd),listOf(VideoMode(3840,2160,listOf(30,60)))) { true }
        assertEquals(2,modes.size); assertFalse(modes.last().scaled); assertEquals(listOf(30),modes.last().fps)
    }
    @Test fun fourKOnlyPhoneGetsQhdAtSourceFps() {
        val modes = QhdModePlanner.add(listOf(fhd),listOf(VideoMode(3840,2160,listOf(30)))) { true }
        val qhd = modes.single { it.width == 2560 }
        assertEquals(1440,qhd.height); assertTrue(qhd.scaled)
        assertEquals(3840,qhd.captureWidth); assertEquals(2160,qhd.captureHeight)
        assertEquals(listOf(30),qhd.fps); assertFalse(qhd.isHighSpeed(30))
    }
    @Test fun noUpscalingOrAspectStretching() {
        val modes = QhdModePlanner.add(listOf(fhd),listOf(fhd,VideoMode(4096,2160,listOf(60)))) { true }
        assertEquals(listOf(fhd),modes)
    }
    @Test fun encoderMustSupportQhdAndFps() {
        val source = listOf(VideoMode(3840,2160,listOf(30,60)))
        assertEquals(listOf(fhd),QhdModePlanner.add(listOf(fhd),source) { false })
        assertEquals(listOf(30),QhdModePlanner.add(listOf(fhd),source) { it == 30 }.last().fps)
    }
    @Test fun largerSixteenByNineSensorModeCanDownscale() {
        val qhd = QhdModePlanner.add(listOf(fhd),listOf(VideoMode(4032,2268,listOf(30)))) { true }.last()
        assertEquals(4032,qhd.captureWidth); assertEquals(2268,qhd.captureHeight)
        assertEquals(2560,qhd.width); assertEquals(1440,qhd.height)
    }
    @Test fun preferUsableFpsThenSmallestSource() {
        val source = listOf(VideoMode(2688,1512,listOf(30)),VideoMode(3840,2160,listOf(30,60)),VideoMode(3200,1800,listOf(30,60)))
        val qhd = QhdModePlanner.add(listOf(fhd),source) { true }.last()
        assertEquals(3200,qhd.captureWidth); assertEquals(listOf(30,60),qhd.fps)
    }
}
