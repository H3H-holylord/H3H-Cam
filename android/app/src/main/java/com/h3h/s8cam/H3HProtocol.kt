package com.h3h.s8cam

import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicInteger

object H3HProtocol {
    const val VERSION_1 = 1
    const val VERSION_2 = 2
    const val CURRENT_VERSION = VERSION_2

    const val HELLO = 1
    const val CAPABILITIES = 2
    const val COMMAND = 3
    const val ACK = 4
    const val ERROR = 5
    const val VIDEO_CONFIG = 6
    const val VIDEO_FRAME = 7
    const val HEARTBEAT = 8
    const val TELEMETRY = 9
    const val HELLO_ACK = 10
    const val CAMERA_LIST = 11
    const val PONG = 12
    const val SET_BITRATE = 13
    const val REQUEST_IDR = 14
    const val BATTERY = 15
    const val THERMAL = 16
    const val AUDIO = 17

    const val FLAG_IDR = 1
    private const val HEADER = 32
    private const val MAX_PAYLOAD = 16 * 1024 * 1024

    data class Message(
        val type: Int,
        val sequence: Int,
        val timestampUs: Long,
        val flags: Int,
        val payload: ByteArray,
        val version: Int = CURRENT_VERSION
    )

    private val headerBuffer = ByteArray(HEADER)
    private val headerByteBuffer = ByteBuffer.wrap(headerBuffer).order(ByteOrder.LITTLE_ENDIAN)

    @Synchronized fun write(output: OutputStream, message: Message) {
        require(message.payload.size <= MAX_PAYLOAD)
        headerByteBuffer.clear()
        headerByteBuffer.put('H'.code.toByte()).put('3'.code.toByte()).put('H'.code.toByte()).put('C'.code.toByte())
        headerByteBuffer.putShort(message.version.toShort()).putShort(message.type.toShort()).putInt(message.payload.size)
            .putInt(message.sequence).putLong(message.timestampUs).putInt(message.flags).putInt(0)
        output.write(headerBuffer, 0, HEADER)
        output.write(message.payload)
        output.flush()
    }

    fun read(input: InputStream): Message? {
        val header = ByteArray(HEADER)
        if (!readExact(input, header)) return null
        require(header.copyOfRange(0, 4).contentEquals(byteArrayOf(72, 51, 72, 67))) { "H3H magic mismatch" }
        val b = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN)
        b.position(4)
        val version = b.short.toInt()
        require(version in VERSION_1..VERSION_2) { "Unsupported H3H protocol $version" }
        val type = b.short.toInt() and 0xffff
        val length = b.int; require(length in 0..MAX_PAYLOAD) { "Invalid H3H payload" }
        val sequence = b.int; val timestamp = b.long; val flags = b.int
        return Message(type, sequence, timestamp, flags, ByteArray(length).also { require(readExact(input, it)) }, version)
    }

    private fun readExact(input: InputStream, bytes: ByteArray): Boolean {
        var offset = 0
        while (offset < bytes.size) {
            val count = input.read(bytes, offset, bytes.size - offset)
            if (count < 0) return false
            offset += count
        }
        return true
    }
}
