package com.h3h.s8cam

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LegacyCameraSelectorTest {

    @Test
    fun `camera1 mode enables legacy capture on any manufacturer and any camera`() {
        assertTrue(
            SamsungLegacyCapture.shouldUseLegacy(
                cameraApi = "camera1",
                forceLegacy = false,
                manufacturer = "Xiaomi",
                model = "2201116SG",
                cameraId = "0",
                physicalId = null,
                width = 1920,
                height = 1080,
                camera1Count = 2
            )
        )
        assertTrue(
            SamsungLegacyCapture.shouldUseLegacy(
                cameraApi = "camera1",
                forceLegacy = false,
                manufacturer = "vivo",
                model = "V2324A",
                cameraId = "legacy:1",
                physicalId = null,
                width = 1280,
                height = 720,
                camera1Count = 2
            )
        )
    }

    @Test
    fun `camera2 mode always disables legacy capture even on supported samsung`() {
        assertFalse(
            SamsungLegacyCapture.shouldUseLegacy(
                cameraApi = "camera2",
                forceLegacy = true,
                manufacturer = "samsung",
                model = "SM-N960N",
                cameraId = "0",
                physicalId = null,
                width = 1920,
                height = 1080,
                camera1Count = 2
            )
        )
    }

    @Test
    fun `auto mode keeps legacy for verified samsung and legacy keys only`() {
        assertTrue(
            SamsungLegacyCapture.shouldUseLegacy(
                cameraApi = "auto",
                forceLegacy = false,
                manufacturer = "samsung",
                model = "SM-N960N",
                cameraId = "0",
                physicalId = null,
                width = 1920,
                height = 1080,
                camera1Count = 2
            )
        )
        assertFalse(
            SamsungLegacyCapture.shouldUseLegacy(
                cameraApi = "auto",
                forceLegacy = false,
                manufacturer = "Xiaomi",
                model = "M2102J20SG",
                cameraId = "0",
                physicalId = null,
                width = 1920,
                height = 1080,
                camera1Count = 2
            )
        )
        assertTrue(
            SamsungLegacyCapture.shouldUseLegacy(
                cameraApi = "auto",
                forceLegacy = false,
                manufacturer = "Xiaomi",
                model = "M2102J20SG",
                cameraId = "legacy:0",
                physicalId = null,
                width = 1920,
                height = 1080,
                camera1Count = 2
            )
        )
    }

    @Test
    fun `selectBestFpsRange prefers exact fixed 60fps then narrowest range reaching 60fps`() {
        val rangesWithExact = listOf(15000 to 30000, 30000 to 30000, 30000 to 60000, 60000 to 60000)
        assertEquals(60000 to 60000, SamsungLegacyCapture.selectBestFpsRange(rangesWithExact, 60))

        val rangesVariable = listOf(15000 to 30000, 15000 to 60000, 30000 to 60000)
        assertEquals(30000 to 60000, SamsungLegacyCapture.selectBestFpsRange(rangesVariable, 60))

        val ranges30 = listOf(15000 to 30000, 30000 to 30000, 30000 to 60000)
        assertEquals(30000 to 30000, SamsungLegacyCapture.selectBestFpsRange(ranges30, 30))
    }

    @Test
    fun `selectBestPreviewSize chooses exact match or closest aspect and area`() {
        val sizes = listOf(640 to 480, 1280 to 720, 1920 to 1080, 3840 to 2160)
        assertEquals(1920 to 1080, SamsungLegacyCapture.selectBestPreviewSize(sizes, 1920, 1080))
        assertEquals(1280 to 720, SamsungLegacyCapture.selectBestPreviewSize(sizes, 1280, 720))
        assertEquals(1920 to 1080, SamsungLegacyCapture.selectBestPreviewSize(listOf(640 to 480, 1920 to 1080), 1600, 900))
    }

    @Test
    fun `parseLegacyCameraIndex handles legacy prefix and plain numeric id`() {
        assertEquals(0, SamsungLegacyCapture.parseLegacyCameraIndex("legacy:0", 2))
        assertEquals(1, SamsungLegacyCapture.parseLegacyCameraIndex("legacy:1", 2))
        assertEquals(1, SamsungLegacyCapture.parseLegacyCameraIndex("1", 2))
        assertEquals(0, SamsungLegacyCapture.parseLegacyCameraIndex("auto", 2))
        assertEquals(-1, SamsungLegacyCapture.parseLegacyCameraIndex("5", 2))
    }
}
