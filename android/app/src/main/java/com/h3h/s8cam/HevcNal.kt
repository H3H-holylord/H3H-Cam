package com.h3h.s8cam

import java.io.ByteArrayOutputStream

/**
 * Normalizes the HEVC (H.265) buffer forms emitted by Android vendor codecs.
 * Supports Annex B start codes (00 00 01 / 00 00 00 01), length prefixes, and raw CSD NALs.
 */
object HevcNal {
    private data class StartCode(val offset: Int, val size: Int)

    fun split(bytes: ByteArray): List<ByteArray> {
        if (bytes.isEmpty()) return emptyList()

        splitAnnexB(bytes)?.let { return it }

        val candidates = (4 downTo 1).mapNotNull { splitLengthPrefixed(bytes, it) }
        val unique = mutableListOf<List<ByteArray>>()
        candidates.forEach { candidate ->
            if (unique.none { sameNals(it, candidate) }) unique += candidate
        }
        if (unique.size == 1 && (unique.first().size > 1 || !validHeader(bytes[0], bytes[1]))) return unique.first()

        return if (bytes.size >= 2 && validHeader(bytes[0], bytes[1])) listOf(bytes.copyOf()) else emptyList()
    }

    private fun splitAnnexB(bytes: ByteArray): List<ByteArray>? {
        var code = findStartCode(bytes, 0) ?: return null
        if ((0 until code.offset).any { bytes[it] != 0.toByte() }) return null

        val out = mutableListOf<ByteArray>()
        while (true) {
            val start = code.offset + code.size
            val next = findStartCode(bytes, start)
            var end = next?.offset ?: bytes.size
            while (end > start && bytes[end - 1] == 0.toByte()) end--
            if (end - start >= 2 && validHeader(bytes[start], bytes[start + 1])) {
                out += bytes.copyOfRange(start, end)
            }
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
            if (end - offset < 2 || !validHeader(bytes[offset], bytes[offset + 1])) return null
            out += bytes.copyOfRange(offset, end)
            offset = end
        }
        return out.takeIf { it.isNotEmpty() }
    }

    private fun validHeader(b0: Byte, b1: Byte): Boolean {
        // forbidden_zero_bit must be 0
        if (b0.toInt() and 0x80 != 0) return false
        val type = (b0.toInt() ushr 1) and 0x3F
        if (type !in 0..63) return false
        val tidPlus1 = b1.toInt() and 0x07
        return tidPlus1 > 0
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

    fun type(nal: ByteArray): Int = if (nal.size < 2) -1 else (nal[0].toInt() ushr 1) and 0x3F
}

/** Cache VPS, SPS and PPS for HEVC. */
class HevcParameterSets {
    var vps: ByteArray? = null
    var sps: ByteArray? = null
    var pps: ByteArray? = null

    fun update(nals: List<ByteArray>) {
        nals.forEach {
            when (HevcNal.type(it)) {
                32 -> vps = it.copyOf()
                33 -> sps = it.copyOf()
                34 -> pps = it.copyOf()
            }
        }
    }

    val ready get() = sps != null && pps != null
    fun all(): List<ByteArray> = listOfNotNull(vps, sps, pps)
}
