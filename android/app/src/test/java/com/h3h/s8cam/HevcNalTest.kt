package com.h3h.s8cam

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayOutputStream

class HevcNalTest {
    // HEVC NAL headers (2 bytes):
    // Type 32 (VPS): 0x40, 0x01
    // Type 33 (SPS): 0x42, 0x01
    // Type 34 (PPS): 0x44, 0x01
    // Type 19 (IDR_W_RADL): 0x26, 0x01
    // Type 1 (TRAIL_R): 0x02, 0x01
    private val vps = byteArrayOf(0x40, 0x01, 0x0c, 0x01)
    private val sps = byteArrayOf(0x42, 0x01, 0x01, 0x02, 0x20)
    private val pps = byteArrayOf(0x44, 0x01, 0x05)
    private val idr = byteArrayOf(0x26, 0x01, 0x10, 0x20)

    @Test
    fun annexBAllowsLeadingAndTrailingZeroBytesAndMixedPrefixes() {
        val bytes = byteArrayOf(
            0,
            0, 0, 0, 1, 0x40, 0x01, 0x0c,
            0, 0, 1, 0x42, 0x01, 0x01,
            0, 0, 1, 0x44, 0x01, 0x05,
            0, 0
        )

        val nals = HevcNal.split(bytes)

        assertEquals(listOf(32, 33, 34), nals.map(HevcNal::type))
        assertArrayEquals(byteArrayOf(0x40, 0x01, 0x0c), nals[0])
        assertArrayEquals(byteArrayOf(0x42, 0x01, 0x01), nals[1])
        assertArrayEquals(byteArrayOf(0x44, 0x01, 0x05), nals[2])
    }

    @Test
    fun acceptsOneThroughFourByteLengths() {
        for (lengthBytes in 1..4) {
            val nals = HevcNal.split(lengthPrefixed(lengthBytes, vps, sps, pps))
            assertEquals("lengthBytes=$lengthBytes", listOf(32, 33, 34), nals.map(HevcNal::type))
            assertArrayEquals(vps, nals[0])
            assertArrayEquals(sps, nals[1])
            assertArrayEquals(pps, nals[2])
        }
    }

    @Test
    fun acceptsRawSingleNalCsdBuffers() {
        assertArrayEquals(vps, HevcNal.split(vps).single())
        assertArrayEquals(sps, HevcNal.split(sps).single())
        assertArrayEquals(pps, HevcNal.split(pps).single())
    }

    @Test
    fun malformedAndForbiddenNalHeadersReturnNoUnits() {
        // Forbidden bit set (0x80)
        assertTrue(HevcNal.split(byteArrayOf(0x80.toByte(), 1, 2, 3)).isEmpty())
        // TID == 0 (invalid temporal id plus 1)
        assertTrue(HevcNal.split(byteArrayOf(0x40, 0, 1, 2)).isEmpty())
        // Too short (< 2 bytes)
        assertTrue(HevcNal.split(byteArrayOf(0x40)).isEmpty())
        assertEquals(-1, HevcNal.type(byteArrayOf()))
    }

    @Test
    fun parameterSetCacheOwnsItsCopies() {
        val mutableVps = vps.copyOf()
        val mutableSps = sps.copyOf()
        val mutablePps = pps.copyOf()
        val sets = HevcParameterSets()
        sets.update(listOf(mutableVps, mutableSps, mutablePps))

        mutableVps[0] = 0
        mutableSps[0] = 0
        mutablePps[0] = 0

        assertTrue(sets.ready)
        assertArrayEquals(vps, sets.vps)
        assertArrayEquals(sps, sets.sps)
        assertArrayEquals(pps, sets.pps)
        assertEquals(3, sets.all().size)
    }

    private fun lengthPrefixed(lengthBytes: Int, vararg nals: ByteArray): ByteArray {
        val output = ByteArrayOutputStream()
        nals.forEach { nal ->
            for (shift in (lengthBytes - 1) * 8 downTo 0 step 8)
                output.write((nal.size ushr shift) and 0xff)
            output.write(nal)
        }
        return output.toByteArray()
    }
}
