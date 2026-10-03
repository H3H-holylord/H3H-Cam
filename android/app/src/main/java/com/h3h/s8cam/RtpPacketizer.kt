package com.h3h.s8cam

import java.security.SecureRandom

/** RFC 3550 / RFC 6184 (H.264) and RFC 7798 (H.265 / HEVC). */
class RtpPacketizer(
    private val emit: (ByteArray) -> Unit,
    private val codec: String = "h264",
    private var sequence: Int = SecureRandom().nextInt(65536),
    private val ssrc: Int = SecureRandom().nextInt(),
    private val timestampBase: Long = SecureRandom().nextInt().toLong() and 0xffffffffL
) {
    private val isHevc = codec.equals("hevc", ignoreCase = true)
    private var firstPts: Long? = null
    private val h264Config = ParameterSets()
    private val hevcConfig = HevcParameterSets()
    private var waitingIdr = true

    fun resetAfterLoss() { waitingIdr = true }

    fun send(data: ByteArray, ptsUs: Long) {
        if (isHevc) sendHevc(data, ptsUs) else sendH264(data, ptsUs)
    }

    private fun sendH264(data: ByteArray, ptsUs: Long) {
        val nals = H264Nal.split(data)
        h264Config.update(nals)
        val media = nals.filter { H264Nal.type(it) !in listOf(7, 8, 9) }
        if (media.none { H264Nal.type(it) in 1..5 }) return
        val idr = media.any { H264Nal.type(it) == 5 }
        if (!h264Config.ready || (waitingIdr && !idr)) return
        waitingIdr = false
        if (firstPts == null) firstPts = ptsUs
        val timestamp = (timestampBase + (ptsUs - firstPts!!) * 90 / 1000) and 0xffffffffL
        val outgoing = (if (idr) h264Config.all() else emptyList()) + media
        outgoing.forEachIndexed { index, nal ->
            val marker = index == outgoing.lastIndex
            if (nal.size <= 1200) packet(nal, timestamp, marker)
            else {
                var offset = 1
                while (offset < nal.size) {
                    val count = minOf(1198, nal.size - offset)
                    val last = offset + count == nal.size
                    val payload = ByteArray(count + 2)
                    payload[0] = ((nal[0].toInt() and 0xe0) or 28).toByte()
                    payload[1] = (H264Nal.type(nal) or (if (offset == 1) 128 else 0) or (if (last) 64 else 0)).toByte()
                    System.arraycopy(nal, offset, payload, 2, count)
                    packet(payload, timestamp, marker && last)
                    offset += count
                }
            }
        }
    }

    private fun sendHevc(data: ByteArray, ptsUs: Long) {
        val nals = HevcNal.split(data)
        hevcConfig.update(nals)
        val media = nals.filter { HevcNal.type(it) !in listOf(32, 33, 34, 35, 39, 40) }
        if (media.none { HevcNal.type(it) in 0..31 }) return
        val idr = media.any { HevcNal.type(it) in 19..21 }
        if (!hevcConfig.ready || (waitingIdr && !idr)) return
        waitingIdr = false
        if (firstPts == null) firstPts = ptsUs
        val timestamp = (timestampBase + (ptsUs - firstPts!!) * 90 / 1000) and 0xffffffffL
        val outgoing = (if (idr) hevcConfig.all() else emptyList()) + media
        outgoing.forEachIndexed { index, nal ->
            val marker = index == outgoing.lastIndex
            if (nal.size <= 1200) packet(nal, timestamp, marker)
            else {
                val nalType = (nal[0].toInt() ushr 1) and 0x3F
                var offset = 2
                while (offset < nal.size) {
                    val count = minOf(1197, nal.size - offset)
                    val last = offset + count == nal.size
                    val isFirst = offset == 2
                    val payload = ByteArray(count + 3)
                    payload[0] = ((nal[0].toInt() and 0x81) or (49 shl 1)).toByte()
                    payload[1] = nal[1]
                    payload[2] = ((if (isFirst) 128 else 0) or (if (last) 64 else 0) or nalType).toByte()
                    System.arraycopy(nal, offset, payload, 3, count)
                    packet(payload, timestamp, marker && last)
                    offset += count
                }
            }
        }
    }

    private fun packet(payload: ByteArray, ts: Long, marker: Boolean) {
        val b = ByteArray(12 + payload.size)
        b[0] = 0x80.toByte(); b[1] = (96 or (if (marker) 128 else 0)).toByte()
        b[2] = (sequence ushr 8).toByte(); b[3] = sequence.toByte()
        sequence = (sequence + 1) and 65535
        for (i in 0..3) {
            b[4+i] = (ts ushr (24 - i*8)).toByte()
            b[8+i] = (ssrc ushr (24 - i*8)).toByte()
        }
        System.arraycopy(payload, 0, b, 12, payload.size)
        emit(b)
    }
}
