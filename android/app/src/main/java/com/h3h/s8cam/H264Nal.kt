package com.h3h.s8cam

import java.io.ByteArrayOutputStream

/**
 * Normalizes the H.264 buffer forms emitted by Android vendor codecs.
 *
 * MediaCodec normally returns Annex B or four-byte length-prefixed access units, but CSD buffers
 * in the field also arrive as raw SPS/PPS, AVCDecoderConfigurationRecord (avcC), or one-to-four
 * byte length-prefixed NAL units. A malformed buffer is ignored instead of terminating the codec
 * drain thread.
 */
object H264Nal {
    private data class StartCode(val offset: Int, val size: Int)

    fun split(bytes: ByteArray): List<ByteArray> {
        if (bytes.isEmpty()) return emptyList()

        splitAnnexB(bytes)?.let { return it }
        splitAvcConfigurationRecord(bytes)?.let { return it }

        val candidates = (4 downTo 1).mapNotNull { splitLengthPrefixed(bytes, it) }
        val unique = mutableListOf<List<ByteArray>>()
        candidates.forEach { candidate ->
            if (unique.none { sameNals(it, candidate) }) unique += candidate
        }
        // A one-byte AVCC length can look exactly like the header of a raw single NAL. Prefer the
        // raw interpretation in that genuinely ambiguous case; larger prefixes start with zero.
        if (unique.size == 1 && !validHeader(bytes[0])) return unique.first()

        return if (validHeader(bytes[0])) listOf(bytes.copyOf()) else emptyList()
    }

    private fun splitAnnexB(bytes: ByteArray): List<ByteArray>? {
        var code = findStartCode(bytes, 0) ?: return null
        // Annex B permits leading_zero_8bits, but arbitrary bytes before the first prefix usually
        // mean this is a length-prefixed buffer containing a start-code-like byte sequence.
        if ((0 until code.offset).any { bytes[it] != 0.toByte() }) return null

        val out = mutableListOf<ByteArray>()
        while (true) {
            val start = code.offset + code.size
            val next = findStartCode(bytes, start)
            var end = next?.offset ?: bytes.size
            while (end > start && bytes[end - 1] == 0.toByte()) end--
            if (end > start && validHeader(bytes[start])) out += bytes.copyOfRange(start, end)
            if (next == null) break
            code = next
        }
        return out
    }

    private fun findStartCode(bytes: ByteArray, from: Int): StartCode? {
        var i = from.coerceAtLeast(0)
        while (i + 2 < bytes.size) {
            if (bytes[i] == 0.toByte() && bytes[i + 1] == 0.toByte()) {
                if (i + 3 < bytes.size && bytes[i + 2] == 0.toByte() && bytes[i + 3] == 1.toByte())
                    return StartCode(i, 4)
                if (bytes[i + 2] == 1.toByte()) return StartCode(i, 3)
            }
            i++
        }
        return null
    }

    private fun splitLengthPrefixed(bytes: ByteArray, lengthBytes: Int): List<ByteArray>? {
        val out = mutableListOf<ByteArray>()
        var offset = 0
        while (offset < bytes.size) {
            if (bytes.size - offset < lengthBytes) return null
            var length = 0L
            repeat(lengthBytes) { length = (length shl 8) or (bytes[offset++].toLong() and 0xff) }
            if (length <= 0 || length > bytes.size - offset) return null
            val end = offset + length.toInt()
            if (!validHeader(bytes[offset])) return null
            out += bytes.copyOfRange(offset, end)
            offset = end
        }
        return out.takeIf { it.isNotEmpty() }
    }

    /** Parse ISO/IEC 14496-15 AVCDecoderConfigurationRecord often exposed as vendor csd-0. */
    private fun splitAvcConfigurationRecord(bytes: ByteArray): List<ByteArray>? {
        if (bytes.size < 7 || bytes[0] != 1.toByte()) return null
        if ((bytes[4].toInt() and 0xfc) != 0xfc || (bytes[5].toInt() and 0xe0) != 0xe0) return null
        var offset = 6
        val out = mutableListOf<ByteArray>()

        val spsCount = bytes[5].toInt() and 0x1f
        if (spsCount == 0) return null
        repeat(spsCount) {
            val nal = readU16Nal(bytes, offset) ?: return null
            if (type(nal.first) != 7) return null
            out += nal.first
            offset = nal.second
        }
        if (offset >= bytes.size) return null
        val ppsCount = bytes[offset++].toInt() and 0xff
        if (ppsCount == 0) return null
        repeat(ppsCount) {
            val nal = readU16Nal(bytes, offset) ?: return null
            if (type(nal.first) != 8) return null
            out += nal.first
            offset = nal.second
        }
        // High-profile avcC may contain an extension after PPS. SPS/PPS above are complete and are
        // all the stream bootstrap data needed by the packetizers.
        return out
    }

    private fun readU16Nal(bytes: ByteArray, offset: Int): Pair<ByteArray, Int>? {
        if (offset < 0 || offset + 2 > bytes.size) return null
        val length = ((bytes[offset].toInt() and 0xff) shl 8) or (bytes[offset + 1].toInt() and 0xff)
        val start = offset + 2
        val end = start + length
        if (length <= 0 || end > bytes.size || !validHeader(bytes[start])) return null
        return bytes.copyOfRange(start, end) to end
    }

    private fun validHeader(value: Byte): Boolean {
        if (value.toInt() and 0x80 != 0) return false
        return (value.toInt() and 0x1f) in 1..23
    }

    private fun sameNals(first: List<ByteArray>, second: List<ByteArray>): Boolean =
        first.size == second.size && first.indices.all { first[it].contentEquals(second[it]) }

    fun annexB(nals: List<ByteArray>): ByteArray {
        val out = ByteArrayOutputStream()
        nals.filter { it.isNotEmpty() }.forEach {
            out.write(byteArrayOf(0, 0, 0, 1))
            out.write(it)
        }
        return out.toByteArray()
    }

    fun type(nal: ByteArray) = if (nal.isEmpty()) -1 else nal[0].toInt() and 31
}

/** Cache SPS and PPS independently: Samsung can emit them in separate buffers. */
class ParameterSets {
    var sps: ByteArray? = null
    var pps: ByteArray? = null
    fun update(nals: List<ByteArray>) {
        nals.forEach { when (H264Nal.type(it)) { 7 -> sps = it.copyOf(); 8 -> pps = it.copyOf() } }
    }
    val ready get() = sps != null && pps != null
    fun all() = listOfNotNull(sps, pps)
}
