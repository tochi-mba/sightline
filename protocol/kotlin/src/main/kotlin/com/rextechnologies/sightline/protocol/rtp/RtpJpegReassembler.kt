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
 * Turns the camera's stream into pictures.
 *
 * Two things about this camera make a stock RTSP stack the wrong tool, and both are why this
 * class exists.
 *
 * **There is no interleaved framing.** The camera answers a TCP transport request with
 * `interleaved=0-1` and then sends bare RTP packets down the connection, with none of the
 * `$`, channel and length bytes that RFC 2326 requires. A reader that looks for that framing
 * finds `$` bytes at random points inside JPEG data and produces nonsense. Packets are
 * instead split on the RTP header itself, anchored to the stream's own synchronisation source.
 *
 * **Each frame already carries its JFIF header.** RFC 2435 strips the quantisation and Huffman
 * tables from the wire and expects the receiver to rebuild them; this camera leaves a complete
 * JFIF document in the payload. So fragments are simply concatenated, and none of that
 * reconstruction is needed or wanted.
 */
class RtpJpegReassembler {
    private val stream = ByteQueue()
    private val frame = ByteQueue()
    private var synchronisationSource: Int? = null
    private var currentTimestamp = 0u
    private var width = 0
    private var height = 0
    private var building = false
    private var lastSequence: Int? = null

    /** How many packets have been read. */
    var packetsRead: Int = 0
        private set

    /** How many packets were dropped because the sequence number jumped. */
    var packetsLost: Int = 0
        private set

    /** How many bytes are waiting for a packet to finish, which a desynchronised stream must not grow. */
    internal val buffered: Int
        get() = stream.size

    /**
     * Adds bytes from the connection and returns any pictures they completed.
     *
     * @param bytes Whatever the last read produced; boundaries do not matter.
     */
    fun push(bytes: ByteArray): List<CameraFrame> {
        stream.append(bytes)
        val finished = mutableListOf<CameraFrame>()

        while (true) {
            val start = findPacketStart(0)
            if (start < 0) {
                // Nothing usable yet. Keep a little context so a header split across two reads is
                // still found, and drop the rest so a desynchronised stream cannot grow forever.
                if (stream.size > 1 shl 20) {
                    stream.removeFirst(stream.size - RTP_HEADER_LENGTH)
                }

                return finished
            }

            // The stream's sender is fixed by its first packet before that packet's end is looked for.
            // Otherwise the end is found by accepting a header from anyone, and twelve bytes of
            // picture data shaped like one cut the first packet short; the rest is then thrown away as
            // noise and the first picture arrives without its start.
            if (synchronisationSource == null) {
                synchronisationSource = stream.array.readInt32BigEndian(start + 8)
            }

            val next = findPacketStart(start + RTP_HEADER_LENGTH)
            if (next < 0) {
                // The last packet in the buffer is only complete once the next one has begun, so
                // wait rather than emitting a half-read fragment.
                if (start > 0) {
                    stream.removeFirst(start)
                }

                return finished
            }

            val packet = stream.copyOfRange(start, next)
            stream.removeFirst(next)
            consume(packet, finished)
        }
    }

    /**
     * Whether a packet begins at [offset], which the caller has checked leaves room for a header.
     *
     * A header from another synchronisation source is not a packet start. With no length on the wire
     * such a packet cannot be cut out of the stream — its bytes stay inside the packet around them —
     * but it can never start a picture or set a frame's size. The camera sends one source, since only
     * the video track is set up, so this costs nothing in practice.
     */
    private fun isPacketStart(offset: Int): Boolean {
        val bytes = stream.array

        // Version 2, no padding, extension or contributing sources, and the JPEG payload type. The
        // marker bit varies.
        if (bytes.unsignedAt(offset) != 0x80 || (bytes.unsignedAt(offset + 1) and 0x7F) != JPEG_PAYLOAD_TYPE) {
            return false
        }

        val source = synchronisationSource
        return source == null || bytes.readInt32BigEndian(offset + 8) == source
    }

    private fun findPacketStart(from: Int): Int {
        var offset = from
        while (offset + RTP_HEADER_LENGTH <= stream.size) {
            if (isPacketStart(offset)) {
                return offset
            }
            offset++
        }

        return -1
    }

    /**
     * Takes one packet, adding any pictures it finished to [finished].
     *
     * A single packet can finish two pictures: its fragment offset of zero ends the one before it,
     * and its marker bit ends its own. That happens whenever a picture fits in one packet.
     */
    private fun consume(packet: ByteArray, finished: MutableList<CameraFrame>) {
        if (packet.size < RTP_HEADER_LENGTH + JPEG_HEADER_LENGTH) {
            return
        }

        val marker = (packet.unsignedAt(1) and 0x80) != 0
        val sequence = packet.readUInt16BigEndian(2)
        val previous = lastSequence
        if (previous != null && ((previous + 1) and 0xFFFF) != sequence) {
            packetsLost++
        }

        lastSequence = sequence
        packetsRead++

        val timestamp = packet.readInt32BigEndian(4).toUInt()

        // RFC 2435: type-specific, a 24-bit fragment offset, type, Q, then width and height in
        // units of eight pixels. A packet start has no contributing sources, so this header always
        // follows the fixed RTP header directly.
        val jpegHeader = RTP_HEADER_LENGTH
        val fragmentOffset = (packet.unsignedAt(jpegHeader + 1) shl 16) or
            (packet.unsignedAt(jpegHeader + 2) shl 8) or
            packet.unsignedAt(jpegHeader + 3)
        val quantisation = packet.unsignedAt(jpegHeader + 5)
        var payload = jpegHeader + JPEG_HEADER_LENGTH

        if (quantisation >= 128 && fragmentOffset == 0 && packet.size >= payload + 4) {
            // Tables are inline. This camera does not use them — it sends a whole JFIF header
            // instead — but skipping them correctly costs one line and keeps this honest RFC 2435.
            val tableLength = packet.readUInt16BigEndian(payload + 2)
            payload += 4 + tableLength
        }

        if (payload > packet.size) {
            return
        }

        if (fragmentOffset == 0) {
            // A new picture starts. Anything still being built belongs to the one before it, which
            // is how a camera that never sets the marker bit still produces frames.
            if (building && frame.size > 0) {
                finished += finish()
            }

            frame.clear()
            building = true
            currentTimestamp = timestamp
            width = packet.unsignedAt(jpegHeader + 6) * 8
            height = packet.unsignedAt(jpegHeader + 7) * 8
        }

        if (!building) {
            // Joined the stream mid-picture. Those fragments can never make a whole file, so they
            // are dropped rather than written out as a broken one.
            return
        }

        frame.append(packet, payload, packet.size - payload)

        if (marker) {
            // RFC 2435 marks the last packet of a picture, which is what lets a frame be delivered
            // as soon as it is whole rather than when the next one begins.
            finished += finish()
            frame.clear()
            building = false
        }
    }

    private fun finish(): CameraFrame {
        var jpeg = frame.toByteArray()
        // The camera ends its frames properly, but a dropped last fragment would otherwise produce
        // a file no decoder will open.
        if (jpeg.size < 2 || jpeg.unsignedAt(jpeg.size - 2) != 0xFF || jpeg.unsignedAt(jpeg.size - 1) != 0xD9) {
            jpeg += END_OF_IMAGE
        }

        return CameraFrame(jpeg, currentTimestamp, width, height)
    }

    companion object {
        /** The RTP payload type RFC 2435 assigns to JPEG. */
        const val JPEG_PAYLOAD_TYPE = 26

        private const val RTP_HEADER_LENGTH = 12
        private const val JPEG_HEADER_LENGTH = 8
        private val END_OF_IMAGE = byteArrayOf(0xFF.toByte(), 0xD9.toByte())

        /** Whether a block of bytes looks like a complete JPEG. */
        fun looksLikeJpeg(bytes: ByteArray): Boolean =
            bytes.size > 4 &&
                bytes.unsignedAt(0) == 0xFF &&
                bytes.unsignedAt(1) == 0xD8 &&
                bytes.unsignedAt(2) == 0xFF &&
                bytes.unsignedAt(bytes.size - 2) == 0xFF &&
                bytes.unsignedAt(bytes.size - 1) == 0xD9
    }
}
