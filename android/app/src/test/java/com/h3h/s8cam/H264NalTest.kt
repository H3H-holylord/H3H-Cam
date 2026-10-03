package com.h3h.s8cam

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayOutputStream

class H264NalTest {
    private val sps = byteArrayOf(0x67, 0x42, 0, 0x28)
    private val pps = byteArrayOf(0x68, 1, 2)

    @Test
    fun annexBAllowsLeadingAndTrailingZeroBytesAndMixedPrefixes() {
        val bytes = byteArrayOf(
            0,
            0, 0, 0, 1, 0x67, 0x42,
            0, 0, 1, 0x68, 1,
            0, 0
        )

        val nals = H264Nal.split(bytes)

        assertEquals(listOf(7, 8), nals.map(H264Nal::type))
        assertArrayEquals(byteArrayOf(0x67, 0x42), nals[0])
        assertArrayEquals(byteArrayOf(0x68, 1), nals[1])
    }

    @Test
    fun acceptsOneThroughFourByteAvccLengths() {
        for (lengthBytes in 1..4) {
            val first = if (lengthBytes == 1) ByteArray(30) { (it + 1).toByte() }.apply {
                this[0] = 0x67
            } else sps
            val nals = H264Nal.split(lengthPrefixed(lengthBytes, first, pps))
            assertEquals("lengthBytes=$lengthBytes", listOf(7, 8), nals.map(H264Nal::type))
            assertArrayEquals(first, nals[0])
            assertArrayEquals(pps, nals[1])
        }
    }

    @Test
    fun rawNalWinsWhenOneByteLengthWouldBeAmbiguous() {
        val raw = byteArrayOf(1, 0x68)
        assertArrayEquals(raw, H264Nal.split(raw).single())
    }

    @Test
    fun acceptsRawSingleNalCsdBuffers() {
        assertArrayEquals(sps, H264Nal.split(sps).single())
        assertArrayEquals(pps, H264Nal.split(pps).single())
    }

    @Test
    fun parsesAvcDecoderConfigurationRecord() {
        val avcC = byteArrayOf(
            1, 0x42, 0, 0x28, 0xff.toByte(), 0xe1.toByte(),
            0, sps.size.toByte(), *sps,
            1,
            0, pps.size.toByte(), *pps
        )

        val nals = H264Nal.split(avcC)

        assertEquals(listOf(7, 8), nals.map(H264Nal::type))
        assertArrayEquals(sps, nals[0])
        assertArrayEquals(pps, nals[1])
    }

    @Test
    fun malformedAndForbiddenNalHeadersReturnNoUnits() {
        assertTrue(H264Nal.split(byteArrayOf(0, 0, 0, 20, 0x65, 1)).isEmpty())
        assertTrue(H264Nal.split(byteArrayOf(0xe7.toByte(), 1, 2)).isEmpty())
        assertTrue(H264Nal.split(byteArrayOf(0, 4, 0x67, 1)).isEmpty())
        assertEquals(-1, H264Nal.type(byteArrayOf()))
    }

    @Test
    fun parameterSetCacheOwnsItsCopies() {
        val mutableSps = sps.copyOf()
        val mutablePps = pps.copyOf()
        val sets = ParameterSets()
        sets.update(listOf(mutableSps, mutablePps))

        mutableSps[0] = 0
        mutablePps[0] = 0

        assertTrue(sets.ready)
        assertArrayEquals(sps, sets.sps)
        assertArrayEquals(pps, sets.pps)
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
