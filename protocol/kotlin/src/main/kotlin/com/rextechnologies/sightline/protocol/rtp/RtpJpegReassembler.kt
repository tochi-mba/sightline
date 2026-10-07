package com.rextechnologies.sightline.protocol.rtp

import com.rextechnologies.sightline.protocol.ByteQueue
import com.rextechnologies.sightline.protocol.readInt32BigEndian
import com.rextechnologies.sightline.protocol.readUInt16BigEndian
import com.rextechnologies.sightline.protocol.unsignedAt

/**
 * One complete picture from the camera.
 *
 * @property jpeg The JPEG, ready to decode or write to a file.
 * @property rtpTimestamp The camera's 90 kHz clock reading for this frame.
 * @property width Pixels across, as the RTP header declared.
 * @property height Pixels down, as the RTP header declared.
 */
class CameraFrame(val jpeg: ByteArray, val rtpTimestamp: UInt, val width: Int, val height: Int)

/**
 * Turns the camera's RTP packets, one datagram each, into pictures.
 *
 * RFC 2435 cuts each picture into fragments, each carrying its offset into the picture, and sets the
 * marker bit on the last. A picture is handed on only when every fragment of it arrived in order: UDP
 * drops a packet now and then, and a picture with a hole in it decodes as a smear, not a frame. One that
 * lost a fragment is counted in [picturesDropped] and the next one is waited for.
 *
 * **Each picture already carries its JFIF header.** RFC 2435 strips the quantisation and Huffman tables
 * from the wire and expects the receiver to rebuild them; this camera leaves a complete JFIF document in
 * the payload. So fragments are simply put together, and none of that reconstruction is needed or wanted.
 *
 * **Each picture is padded.** The camera follows a picture's end-of-image marker with up to seven zero
 * bytes, to a multiple of eight. Those are not the picture, and are let go of.
 *
 * This is the port of the .NET `RtpJpegReassembler`, held to the same cases.
 */
class RtpJpegReassembler {
    private val picture = ByteQueue()
    private var source: Int? = null
    private var lastSequence: Int? = null
    private var timestamp = 0
    private var width = 0
    private var height = 0
    private var building = false

    /** How many RTP/JPEG packets have been taken. */
    var packetsRead: Int = 0
        private set

    /** How many packets never arrived, going by the gaps in their sequence numbers. */
    var packetsLost: Int = 0
        private set

    /** How many pictures were thrown away because a fragment of them never arrived. */
    var picturesDropped: Int = 0
        private set

    /**
     * Takes one packet, and returns the picture it finished, if it finished one.
     *
     * @param packet One datagram as it arrived, in its first [length] bytes. Anything that is not an
     *   RTP/JPEG packet is ignored.
     */
    fun push(packet: ByteArray, length: Int = packet.size): CameraFrame? {
        val found = findPayload(packet, length) ?: return null
        packetsRead++
        val ssrc = packet.readInt32BigEndian(8)
        if (source != ssrc) {
            // A new sender, or the same camera starting a new stream: nothing in hand belongs with it.
            source = ssrc
            lastSequence = null
            abandon(counted = false)
        }

        val sequence = packet.readUInt16BigEndian(2)
        lastSequence?.let { previous ->
            val gap = (sequence - previous - 1) and 0xFFFF
            if (gap in 1 until 0x8000) {
                packetsLost += gap
            }
        }

        lastSequence = sequence
        val marker = (packet.unsignedAt(1) and 0x80) != 0
        val stamp = packet.readInt32BigEndian(4)
        val header = found.header
        val offset = (packet.unsignedAt(header + 1) shl 16) or
            (packet.unsignedAt(header + 2) shl 8) or
            packet.unsignedAt(header + 3)

        if (offset == 0) {
            // A picture starts. One still being put together never got its last fragment.
            abandon(counted = true)
            building = true
            timestamp = stamp
            width = packet.unsignedAt(header + 6) * 8
            height = packet.unsignedAt(header + 7) * 8
        } else if (!building) {
            // Joined part-way through a picture, or after one was abandoned: wait for the next.
            return null
        } else if (offset != picture.size || stamp != timestamp) {
            // A fragment before this one never arrived.
            abandon(counted = true)
            return null
        }

        picture.append(packet, found.start, found.end - found.start)
        if (!marker) {
            return null
        }

        val finished = CameraFrame(withoutPadding(picture.toByteArray()), timestamp.toUInt(), width, height)
        picture.clear()
        building = false
        return finished
    }

    /**
     * The picture without the zeros that follow its end-of-image marker. Zeros with no marker before them
     * are kept: they could be the picture's own, and a picture with no end is judged on its own.
     */
    private fun withoutPadding(picture: ByteArray): ByteArray {
        var end = picture.size
        while (end > 0 && picture[end - 1].toInt() == 0) {
            end--
        }

        val ended = end >= 2 && picture.unsignedAt(end - 2) == 0xFF && picture.unsignedAt(end - 1) == 0xD9
        return if (ended) picture.copyOf(end) else picture
    }

    /** Lets go of any picture being put together, counting it as dropped when asked to. */
    private fun abandon(counted: Boolean) {
        if (building && counted) {
            picturesDropped++
        }

        picture.clear()
        building = false
    }

    /** Where in a packet its JPEG header starts, and where the picture's bytes start and end. */
    private class Payload(val header: Int, val start: Int, val end: Int)

    companion object {
        /** The RTP payload type RFC 2435 assigns to JPEG. */
        const val JPEG_PAYLOAD_TYPE = 26

        private const val RTP_HEADER_LENGTH = 12
        private const val JPEG_HEADER_LENGTH = 8
        private const val RESTART_HEADER_LENGTH = 4

        /** Whether a block of bytes looks like a complete JPEG. */
        fun looksLikeJpeg(bytes: ByteArray): Boolean =
            bytes.size > 4 &&
                bytes.unsignedAt(0) == 0xFF &&
                bytes.unsignedAt(1) == 0xD8 &&
                bytes.unsignedAt(2) == 0xFF &&
                bytes.unsignedAt(bytes.size - 2) == 0xFF &&
                bytes.unsignedAt(bytes.size - 1) == 0xD9

        /**
         * Finds the picture's bytes in a packet: after the RTP header with any contributing sources and
         * extension, the JPEG header, any restart header and any inline tables, and before any padding.
         *
         * @return Null for anything that is not a well-formed RTP/JPEG packet.
         */
        private fun findPayload(packet: ByteArray, length: Int): Payload? {
            if (length < RTP_HEADER_LENGTH ||
                (packet.unsignedAt(0) shr 6) != 2 ||
                (packet.unsignedAt(1) and 0x7F) != JPEG_PAYLOAD_TYPE
            ) {
                return null
            }

            var end = length
            if ((packet.unsignedAt(0) and 0x20) != 0) {
                // Padding: its last byte says how much there is.
                end -= packet.unsignedAt(length - 1)
            }

            var at = RTP_HEADER_LENGTH + 4 * (packet.unsignedAt(0) and 0x0F)
            if ((packet.unsignedAt(0) and 0x10) != 0) {
                if (at + 4 > end) {
                    return null
                }

                at += 4 + 4 * packet.readUInt16BigEndian(at + 2)
            }

            // RFC 2435: type-specific, a 24-bit fragment offset, type, Q, then width and height in eights.
            if (at + JPEG_HEADER_LENGTH > end) {
                return null
            }

            val header = at
            val type = packet.unsignedAt(at + 4)
            val quality = packet.unsignedAt(at + 5)
            val fragmentStart = packet.unsignedAt(at + 1) == 0 && packet.unsignedAt(at + 2) == 0 &&
                packet.unsignedAt(at + 3) == 0
            at += JPEG_HEADER_LENGTH
            if (type >= 64) {
                at += RESTART_HEADER_LENGTH
            }

            if (quality >= 128 && fragmentStart) {
                // Tables inline. This camera sends a whole JFIF header instead, but skipping them correctly
                // costs a few lines and keeps this honest RFC 2435.
                if (at + 4 > end) {
                    return null
                }

                at += 4 + packet.readUInt16BigEndian(at + 2)
            }

            return if (at <= end) Payload(header, at, end) else null
        }
    }
}
