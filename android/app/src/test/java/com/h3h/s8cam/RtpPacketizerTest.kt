package com.h3h.s8cam

import org.junit.Assert.*
import org.junit.Test

class RtpPacketizerTest {
    private val sps = byteArrayOf(0x67, 0x42, 0, 0x28)
    private val pps = byteArrayOf(0x68, 1, 2)
    private fun au(vararg nals: ByteArray) = H264Nal.annexB(nals.toList())
    private fun seq(b: ByteArray) = (b[2].toInt() and 255) * 256 + (b[3].toInt() and 255)
    private fun ts(b: ByteArray): Long = (4..7).fold(0L) { a, i -> (a shl 8) or (b[i].toLong() and 255) }

    // HEVC fixtures
    private val hevcVps = byteArrayOf(0x40, 1, 0x0c, 1)
    private val hevcSps = byteArrayOf(0x42, 1, 1, 2)
    private val hevcPps = byteArrayOf(0x44, 1, 5)
    private fun hevcAu(vararg nals: ByteArray) = HevcNal.annexB(nals.toList())

    @Test fun configIsCachedSeparatelyAndRepeatedBeforeEveryIdr() {
        val packets = mutableListOf<ByteArray>()
        val r = RtpPacketizer({ packets.add(it) }, "h264", 123, 456, 1000)
        r.send(au(sps), 0); r.send(au(pps), 0)
        assertTrue(packets.isEmpty())
        r.send(au(byteArrayOf(0x65, 3)), 1_000_000)
        r.send(au(byteArrayOf(0x65, 4)), 2_000_000)
        assertEquals(listOf(7,8,5,7,8,5), packets.map { it[12].toInt() and 31 })
        assertEquals(listOf(false,false,true,false,false,true), packets.map { it[1].toInt() and 128 != 0 })
    }

    @Test fun fuaReassemblesExactlyWithSequenceRolloverAndOneMarker() {
        val packets = mutableListOf<ByteArray>()
        val r = RtpPacketizer({ packets.add(it) }, "h264", 65534, 0x12345678, 0)
        val nal = ByteArray(3501) { (it % 251 + 1).toByte() }.apply { this[0] = 0x65 }
        r.send(au(sps, pps, nal), 0)
        assertEquals(listOf(65534,65535,0,1,2), packets.map(::seq))
        val fu = packets.drop(2)
        assertTrue(fu.all { it.size <= 1212 && (it[12].toInt() and 31) == 28 })
        assertEquals(128, fu.first()[13].toInt() and 128)
        assertEquals(64, fu.last()[13].toInt() and 64)
        val restored = byteArrayOf(0x65) + fu.flatMap { it.drop(14) }.toByteArray()
        assertArrayEquals(nal, restored)
        assertEquals(1, packets.count { it[1].toInt() and 128 != 0 })
        assertTrue(packets.all { it[8] == 0x12.toByte() && it[11] == 0x78.toByte() })
    }

    @Test fun timestampUsesPtsAt90khzAndWraps() {
        val packets = mutableListOf<ByteArray>()
        val r = RtpPacketizer({ packets.add(it) }, "h264", 0, 1, 0xfffffff0L)
        r.send(au(sps, pps, byteArrayOf(0x65, 1)), 1_000_000)
        r.send(au(byteArrayOf(0x41, 1)), 1_100_000)
        assertEquals(0xfffffff0L, ts(packets[0]))
        assertEquals((0xfffffff0L + 9000) and 0xffffffffL, ts(packets.last()))
    }

    @Test fun reconnectWaitsForIdr() {
        val packets = mutableListOf<ByteArray>()
        val r = RtpPacketizer({ packets.add(it) }, "h264")
        r.send(au(sps, pps, byteArrayOf(0x65, 1)), 0)
        r.resetAfterLoss()
        r.send(au(byteArrayOf(0x41, 1)), 100)
        assertEquals(3, packets.size)
        r.send(au(byteArrayOf(0x65, 1)), 200)
        assertEquals(6, packets.size)
    }

    @Test fun strictAvccAndMixedAnnexB() {
        val avcc = byteArrayOf(0,0,0,2,0x67,9,0,0,0,2,0x68,8)
        assertEquals(listOf(7,8), H264Nal.split(avcc).map(H264Nal::type))
        val mixed = byteArrayOf(0,0,1,0x67,9,0,0,0,1,0x68,8)
        assertEquals(listOf(7,8), H264Nal.split(mixed).map(H264Nal::type))
    }

    @Test fun truncatedAvccIsIgnoredWithoutCrashingTheStream() {
        assertTrue(H264Nal.split(byteArrayOf(0,0,0,8,0x65,1)).isEmpty())
    }

    // HEVC (H.265 / RFC 7798) Tests

    @Test fun hevcConfigIsCachedAndRepeatedBeforeEveryIdr() {
        val packets = mutableListOf<ByteArray>()
        val r = RtpPacketizer({ packets.add(it) }, "hevc", 100, 200, 5000)
        r.send(hevcAu(hevcVps), 0)
        r.send(hevcAu(hevcSps), 0)
        r.send(hevcAu(hevcPps), 0)
        assertTrue(packets.isEmpty())
        val idrNal = byteArrayOf(0x26, 1, 0x10, 0x20) // IDR type 19
        r.send(hevcAu(idrNal), 1_000_000)
        // Expected packets: VPS(32), SPS(33), PPS(34), IDR(19)
        assertEquals(4, packets.size)
        val types = packets.map { (it[12].toInt() ushr 1) and 0x3F }
        assertEquals(listOf(32, 33, 34, 19), types)
        assertEquals(listOf(false, false, false, true), packets.map { it[1].toInt() and 128 != 0 })
    }

    @Test fun hevcFuReassemblesExactlyWithRfc7798Headers() {
        val packets = mutableListOf<ByteArray>()
        val r = RtpPacketizer({ packets.add(it) }, "hevc", 1000, 0x55aa55aa, 0)
        // 3500-byte IDR frame: header 0x26, 0x01 (type 19)
        val nal = ByteArray(3502) { (it % 251 + 1).toByte() }.apply {
            this[0] = 0x26
            this[1] = 0x01
        }
        r.send(hevcAu(hevcVps, hevcSps, hevcPps, nal), 0)
        // 3 config packets (single NAL) + fragmented IDR packets
        assertTrue(packets.size >= 4)
        val fuPackets = packets.drop(3)
        // RFC 7798 PayloadHdr: Type 49 in bits 6..1 -> (49 shl 1) = 98 = 0x62
        assertTrue(fuPackets.all { (it[12].toInt() and 0x7E) ushr 1 == 49 })
        // FU header byte is at offset 14 (12 RTP + 2 PayloadHdr)
        val firstFuHeader = fuPackets.first()[14].toInt() and 0xFF
        val lastFuHeader = fuPackets.last()[14].toInt() and 0xFF
        // First has S=1 (bit 7), last has E=1 (bit 6), both have FuType=19
        assertEquals(128 or 19, firstFuHeader)
        assertEquals(64 or 19, lastFuHeader)
        // Middle packets have neither S nor E
        for (i in 1 until fuPackets.lastIndex) {
            val midHeader = fuPackets[i][14].toInt() and 0xFF
            assertEquals(19, midHeader)
        }
        // Reassemble the original NAL: 2 bytes header + FU payloads (starting at offset 15)
        val restored = byteArrayOf(0x26, 0x01) + fuPackets.flatMap { it.drop(15) }.toByteArray()
        assertArrayEquals(nal, restored)
        // Marker only on last packet of AU
        assertEquals(1, packets.count { it[1].toInt() and 128 != 0 })
        assertTrue(packets.last()[1].toInt() and 128 != 0)
    }
}
