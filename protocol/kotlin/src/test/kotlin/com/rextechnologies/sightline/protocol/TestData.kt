package com.rextechnologies.sightline.protocol

import com.rextechnologies.sightline.protocol.gpsock.GpSockFrame

/** The committed vectors in protocol/golden, which the .NET tests read as well. */
object Golden {
    /** The bytes of one golden file, by its path inside protocol/golden. */
    fun bytes(path: String): ByteArray {
        val stream = checkNotNull(Golden::class.java.getResourceAsStream("/$path")) {
            "protocol/golden/$path is not on the test classpath."
        }
        return stream.use { it.readBytes() }
    }

    /** One golden file as text. */
    fun text(path: String): String = bytes(path).toString(Charsets.UTF_8)
}

/** Bytes written as the numbers a protocol document would use, 0x00 to 0xFF. */
fun bytes(vararg values: Int): ByteArray = ByteArray(values.size) { values[it].toByte() }

/**
 * A GPSOCKET response, built by hand from raw numbers so a test can also build ones the library would
 * never produce: a type GPSOCKET does not define, or a command nobody sent.
 */
fun gpSockResponse(type: Int, command: Int, payload: ByteArray): ByteArray {
    val frame = ByteArray(GpSockFrame.RESPONSE_HEADER_LENGTH + payload.size)
    "GPSOCKET".toByteArray(Charsets.US_ASCII).copyInto(frame)
    frame[8] = (type and 0xFF).toByte()
    frame[9] = (type shr 8).toByte()
    frame[10] = (command shr 8).toByte()
    frame[11] = (command and 0xFF).toByte()
    frame[12] = (payload.size and 0xFF).toByte()
    frame[13] = (payload.size shr 8).toByte()
    payload.copyInto(frame, GpSockFrame.RESPONSE_HEADER_LENGTH)
    return frame
}
