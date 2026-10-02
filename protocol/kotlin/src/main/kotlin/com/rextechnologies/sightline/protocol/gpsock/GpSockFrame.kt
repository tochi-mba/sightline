package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.readInt16LittleEndian
import com.rextechnologies.sightline.protocol.readUInt16LittleEndian
import com.rextechnologies.sightline.protocol.unsignedAt
import com.rextechnologies.sightline.protocol.writeUInt16LittleEndian

/**
 * The GPSOCKET wire format, as the camera's firmware defines it.
 *
 * A request is the eight-byte tag, a little-endian type, the command split into its two bytes,
 * and the payload. A request carries no length: the camera knows how long each command's payload
 * is. An acknowledgement is the same but with a little-endian payload size before the payload; a
 * refusal puts its reason in that slot instead and carries nothing after it.
 *
 * ```
 * request : "GPSOCKET" | uint16 LE type=1 | mode | cmd | payload
 * ack     : "GPSOCKET" | uint16 LE type=2 | mode | cmd | uint16 LE size | payload
 * refusal : "GPSOCKET" | uint16 LE type=3 | mode | cmd | int16 LE reason
 * ```
 *
 * The tag is why this port looks dead to every scanner: the firmware compares those eight bytes
 * first and silently drops anything else, with no banner and no error.
 */
object GpSockFrame {
    private val TAG_BYTES = "GPSOCKET".toByteArray(Charsets.US_ASCII)

    /** Bytes before the payload in a request. */
    const val REQUEST_HEADER_LENGTH = 12

    /**
     * Bytes before the payload in an acknowledgement: a request header plus the size. A refusal is
     * exactly this long.
     */
    const val RESPONSE_HEADER_LENGTH = 14

    /** The eight bytes every frame begins with, in both directions. A copy each time, so it stays fixed. */
    val tag: ByteArray
        get() = TAG_BYTES.copyOf()

    /**
     * Builds a request frame.
     *
     * @param command The command to send.
     * @param payload Its argument, which is empty for most commands.
     */
    fun encode(command: GpSockCommand, payload: ByteArray = ByteArray(0)): ByteArray {
        val frame = ByteArray(REQUEST_HEADER_LENGTH + payload.size)
        TAG_BYTES.copyInto(frame)
        frame.writeUInt16LittleEndian(8, GpSockType.Command.code)
        // The command's two bytes travel separately: the firmware reads (frame[10] << 8) | frame[11].
        frame[10] = (command.code shr 8).toByte()
        frame[11] = (command.code and 0xFF).toByte()
        payload.copyInto(frame, REQUEST_HEADER_LENGTH)
        return frame
    }

    /**
     * Reads one response from the front of [length] bytes of [buffer], starting at [offset].
     *
     * @param buffer Bytes received so far, which may hold part of a frame, one, or several.
     * @return The frame read and how many bytes it used, or null when the bytes do not yet hold a
     *   whole frame, which is not an error: the caller reads more and asks again.
     * @throws GpSockProtocolException The bytes are not a GPSOCKET frame at all.
     */
    fun tryDecode(buffer: ByteArray, offset: Int = 0, length: Int = buffer.size - offset): Decoded? {
        if (length < RESPONSE_HEADER_LENGTH) {
            return null
        }

        if (!TAG_BYTES.indices.all { buffer[offset + it] == TAG_BYTES[it] }) {
            // Resynchronising would hide a real desynchronisation, and there is no framing to
            // resynchronise to. Saying so immediately is what makes that bug findable.
            throw GpSockProtocolException(
                "Expected a GPSOCKET frame but the bytes began 0x${buffer.toHexString(offset, offset + 8)}.",
            )
        }

        val type = GpSockType.fromCode(buffer.readUInt16LittleEndian(offset + 8))
        val command = GpSockCommand.fromCode((buffer.unsignedAt(offset + 10) shl 8) or buffer.unsignedAt(offset + 11))
        if (type == GpSockType.Nak) {
            // A refusal carries its reason where an acknowledgement carries its size, and nothing
            // after it: the firmware builds it as gp_resp_set(NAK | cmd, reason, NULL, 0). Reading
            // the reason as a size would wait for up to 64 KB that never come — "busy" is -1, so
            // 65,535 of them — and the app would hang on the most ordinary refusal there is.
            val reason = buffer.copyOfRange(offset + 12, offset + RESPONSE_HEADER_LENGTH)
            return Decoded(GpSockResponse(type, command, reason), RESPONSE_HEADER_LENGTH)
        }

        val size = buffer.readUInt16LittleEndian(offset + 12)
        if (length < RESPONSE_HEADER_LENGTH + size) {
            return null
        }

        val payload = offset + RESPONSE_HEADER_LENGTH
        val response = GpSockResponse(type, command, buffer.copyOfRange(payload, payload + size))
        return Decoded(response, RESPONSE_HEADER_LENGTH + size)
    }

    /**
     * One frame read from the front of a buffer.
     *
     * @property response The frame read.
     * @property consumed How many bytes of the buffer the frame used.
     */
    class Decoded(val response: GpSockResponse, val consumed: Int)
}

/**
 * One answer from the camera.
 *
 * @property type Whether the camera accepted the command, or null for a type GPSOCKET does not define.
 * @property command The command being answered, or null for one this library does not know.
 * @property payload The answer, or the refusal code when this is a refusal.
 */
class GpSockResponse(val type: GpSockType?, val command: GpSockCommand?, val payload: ByteArray) {
    /** Whether the camera did what was asked. */
    val isAck: Boolean
        get() = type == GpSockType.Ack

    /** The refusal code exactly as the camera sent it, or [NakCode.Ok]'s when it did not refuse. */
    val nakCode: Int
        get() = if (type == GpSockType.Nak && payload.size >= 2) payload.readInt16LittleEndian(0) else NakCode.Ok.code

    /**
     * Why the camera refused, or [NakCode.Ok] when it did not refuse.
     *
     * Null when the camera gave a reason this library has no name for; [nakCode] still carries it.
     */
    val nak: NakCode?
        get() = NakCode.fromCode(nakCode)

    /**
     * Whether this is the empty frame that ends a chunked answer.
     *
     * Long answers — the menu XML, a thumbnail, a file's bytes — arrive as a run of acks with at
     * most about 242 bytes each, finished by one with nothing in it.
     */
    val isEndOfChunks: Boolean
        get() = isAck && payload.isEmpty()
}

/** The camera sent something that is not a GPSOCKET frame. */
class GpSockProtocolException(message: String, cause: Throwable? = null) : Exception(message, cause)
