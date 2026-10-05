package com.rextechnologies.sightline.protocol.rtp

import com.rextechnologies.sightline.protocol.bytes
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Reassembly, against packets shaped exactly like the reference camera's.
 *
 * The packets here are synthetic: the real capture is a picture of somebody's room and belongs in
 * an ignored folder, not in a public repository. Their shape is taken from the real one — bare
 * RTP with no interleaved framing, ssrc `0x22222222`, payload type 26, and a complete JFIF
 * document inside the payload rather than the stripped form RFC 2435 describes.
 */
class RtpJpegReassemblerTest {
    @Test
    fun `one packet carrying a whole picture produces that picture`() {
        val jpeg = fakeJpeg(600)
        val reassembler = RtpJpegReassembler()

        val frames = pushAll(reassembler, packets(jpeg, 640, 360, fragmentSize = 10_000))

        assertEquals(1, frames.size)
        assertContentEquals(jpeg, frames[0].jpeg)
        assertEquals(640, frames[0].width)
        assertEquals(360, frames[0].height)
    }

    @Test
    fun `a picture split across packets is put back together in order`() {
        val jpeg = fakeJpeg(3000)
        val reassembler = RtpJpegReassembler()

        val frames = pushAll(reassembler, packets(jpeg, 640, 360, fragmentSize = 700))

        assertEquals(1, frames.size)
        assertContentEquals(jpeg, frames[0].jpeg)
    }

    @Test
    fun `the reference geometry comes through as 640 by 360`() {
        // The camera reports size in units of eight pixels: 0x50 and 0x2d.
        val frames = pushAll(RtpJpegReassembler(), packets(fakeJpeg(400), 640, 360, 10_000))

        assertEquals(640, frames[0].width)
        assertEquals(360, frames[0].height)
    }

    @Test
    fun `bytes arriving in awkward pieces still produce whole pictures`() {
        // TCP gives no frame boundaries, and this stream has no length fields at all, so the
        // reader has to cope with a packet split anywhere - including inside its header.
        val jpeg = fakeJpeg(2000)
        // A packet carries no length, so its end is only known once the next one starts. The
        // sentinel is the next packet a live camera would always be sending anyway.
        val wire = joined(
            packets(jpeg, 640, 360, fragmentSize = 500),
            packets(fakeJpeg(50), 640, 360, 10_000, timestamp = 99_999u),
        )
        val reassembler = RtpJpegReassembler()
        val frames = mutableListOf<CameraFrame>()

        for (offset in wire.indices step 7) {
            frames += reassembler.push(wire.copyOfRange(offset, minOf(offset + 7, wire.size)))
        }

        // The final picture is only emitted once the next one starts, which is how the stream works.
        assertEquals(1, frames.size)
        assertContentEquals(jpeg, frames[0].jpeg)
    }

    @Test
    fun `two pictures in a row are separated by the fragment offset restarting`() {
        val first = fakeJpeg(500)
        val second = fakeJpeg(700)
        val wire = joined(
            packets(first, 640, 360, 10_000, timestamp = 1000u),
            packets(second, 640, 360, 10_000, timestamp = 8380u),
            packets(fakeJpeg(100), 640, 360, 10_000, timestamp = 15_760u),
        )

        val frames = RtpJpegReassembler().push(wire)

        assertEquals(2, frames.size)
        assertContentEquals(first, frames[0].jpeg)
        assertContentEquals(second, frames[1].jpeg)
        assertEquals(1000u, frames[0].rtpTimestamp)
        assertEquals(8380u, frames[1].rtpTimestamp)
    }

    @Test
    fun `a dollar byte inside the picture does not derail it`() {
        // This is the exact trap: 0x24 is '$', which is what RTSP interleaved framing starts with.
        // A reader looking for that framing finds these and produces nonsense.
        val jpeg = fakeJpeg(900, fill = 0x24)
        val reassembler = RtpJpegReassembler()

        val frames = pushAll(reassembler, packets(jpeg, 640, 360, fragmentSize = 10_000))

        assertEquals(1, frames.size)
        assertContentEquals(jpeg, frames[0].jpeg)
    }

    @Test
    fun `a truncated final fragment is still closed so the file opens`() {
        val reassembler = RtpJpegReassembler()
        val withoutEnd = bytes(0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3)

        val frames = pushAll(reassembler, packets(withoutEnd, 640, 360, 10_000))

        assertTrue(RtpJpegReassembler.looksLikeJpeg(frames[0].jpeg))
    }

    @Test
    fun `lost packets are counted rather than passed off as a clean stream`() {
        val packets = packets(fakeJpeg(3000), 640, 360, fragmentSize = 500).toMutableList()
        val reassembler = RtpJpegReassembler()

        // Drop one from the middle, as a lossy link would.
        packets.removeAt(2)
        for (packet in packets) {
            reassembler.push(packet)
        }

        assertEquals(1, reassembler.packetsLost)
    }

    @Test
    fun `a header from another source never starts a packet so it never sets the geometry`() {
        // With no length on the wire another sender's packet cannot be cut out of the stream; what
        // the source lock guarantees is that its header is never taken for one of the camera's.
        val mine = packets(fakeJpeg(400), 640, 360, 10_000).single()
        val theirs = packets(fakeJpeg(400), 320, 240, 10_000, ssrc = 0x99999999.toInt()).single()
        val reassembler = RtpJpegReassembler()

        reassembler.push(mine)
        val frames = reassembler.push(theirs + mine)

        assertEquals(1, frames.size)
        assertTrue(frames.all { it.width == 640 && it.height == 360 })
    }

    @Test
    fun `picture data that looks like another senders header does not cut the first packet short`() {
        // Found by the benchmark suite: before the stream's source was known, the end of the very
        // first packet was looked for by accepting a header from any sender, so twelve bytes of
        // picture data shaped like one cut the first packet there and the rest was thrown away.
        val lookalike = packet(ByteArray(0), ssrc = 0x11111111).copyOfRange(0, 12)
        val jpeg = fakeJpeg(1000)
        lookalike.copyInto(jpeg, 300)
        val reassembler = RtpJpegReassembler()

        val first = packet(jpeg, sequence = 0)
        val second = packet(fakeJpeg(40), sequence = 1, timestamp = 2000u)

        val frames = reassembler.push(first + second)

        assertEquals(1, frames.size)
        assertContentEquals(jpeg, frames.single().jpeg)
    }

    @Test
    fun `a block that is not a jpeg is not called one`() {
        assertFalse(RtpJpegReassembler.looksLikeJpeg(bytes(1, 2, 3, 4)))
        assertFalse(RtpJpegReassembler.looksLikeJpeg(ByteArray(0)))
    }

    @Test
    fun `a jpeg is recognised by both its start and its end`() {
        assertTrue(RtpJpegReassembler.looksLikeJpeg(bytes(0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xD9)))
        assertFalse(RtpJpegReassembler.looksLikeJpeg(bytes(0x00, 0xD8, 0xFF, 0xE0, 0xFF, 0xD9)))
        assertFalse(RtpJpegReassembler.looksLikeJpeg(bytes(0xFF, 0x00, 0xFF, 0xE0, 0xFF, 0xD9)))
        assertFalse(RtpJpegReassembler.looksLikeJpeg(bytes(0xFF, 0xD8, 0x00, 0xE0, 0xFF, 0xD9)))
        assertFalse(RtpJpegReassembler.looksLikeJpeg(bytes(0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0xD9)))
        assertFalse(RtpJpegReassembler.looksLikeJpeg(bytes(0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0x00)))
    }

    @Test
    fun `a camera that never sets the marker bit still produces pictures, one behind`() {
        val first = fakeJpeg(300)
        val second = fakeJpeg(400)
        val reassembler = RtpJpegReassembler()

        val early = reassembler.push(
            packet(first, marker = false, sequence = 0, timestamp = 1000u) +
                packet(second, marker = false, sequence = 1, timestamp = 4000u) +
                packet(fakeJpeg(50), marker = false, sequence = 2, timestamp = 7000u),
        )
        val late = reassembler.push(packet(fakeJpeg(50), marker = false, sequence = 3, timestamp = 10_000u))

        assertEquals(1, early.size)
        assertContentEquals(first, early[0].jpeg)
        assertEquals(1000u, early[0].rtpTimestamp)
        assertEquals(1, late.size)
        assertContentEquals(second, late[0].jpeg)
    }

    @Test
    fun `joining the stream mid-picture drops the fragments that cannot make a whole file`() {
        val whole = fakeJpeg(300)
        val reassembler = RtpJpegReassembler()

        val frames = pushAll(
            reassembler,
            listOf(
                packet(fakeJpeg(200), fragmentOffset = 700, sequence = 0),
                packet(whole, sequence = 1, timestamp = 4000u),
            ),
        )

        assertEquals(1, frames.size)
        assertContentEquals(whole, frames[0].jpeg)
    }

    @Test
    fun `a packet too short to carry a jpeg header is skipped`() {
        val whole = fakeJpeg(300)
        val reassembler = RtpJpegReassembler()
        val short = packet(ByteArray(0)).copyOfRange(0, 16)

        val frames = pushAll(reassembler, listOf(short, packet(whole, sequence = 1)))

        assertEquals(1, frames.size)
        assertContentEquals(whole, frames[0].jpeg)
        assertEquals(1, reassembler.packetsRead)
    }

    @Test
    fun `inline quantisation tables are skipped rather than written into the picture`() {
        val jpeg = fakeJpeg(400)
        val tables = bytes(0, 0, 0, 128) + ByteArray(128) { 0x11 }

        val frames = pushAll(RtpJpegReassembler(), listOf(packet(tables + jpeg, quantisation = 255)))

        assertContentEquals(jpeg, frames[0].jpeg)
    }

    @Test
    fun `tables are only looked for in a picture's first fragment`() {
        val jpeg = fakeJpeg(1000)
        val tables = bytes(0, 0, 0, 64) + ByteArray(64) { 0x11 }
        val packets = listOf(
            packet(tables + jpeg.copyOfRange(0, 600), marker = false, quantisation = 255, sequence = 0),
            packet(jpeg.copyOfRange(600, 1000), fragmentOffset = 600, quantisation = 255, sequence = 1),
        )

        val frames = pushAll(RtpJpegReassembler(), packets)

        assertContentEquals(jpeg, frames[0].jpeg)
    }

    @Test
    fun `a first fragment too short to declare its tables is taken as picture`() {
        val frames = pushAll(RtpJpegReassembler(), listOf(packet(bytes(1, 2, 3), quantisation = 255)))

        assertContentEquals(bytes(1, 2, 3, 0xFF, 0xD9), frames[0].jpeg)
    }

    @Test
    fun `tables declared longer than their packet drop the packet`() {
        val whole = fakeJpeg(300)
        val reassembler = RtpJpegReassembler()
        val overlong = packet(bytes(0, 0, 0x03, 0xE8) + ByteArray(10), quantisation = 255, sequence = 0)

        val frames = pushAll(reassembler, listOf(overlong, packet(whole, sequence = 1)))

        assertEquals(1, frames.size)
        assertContentEquals(whole, frames[0].jpeg)
        assertEquals(2, reassembler.packetsRead)
    }

    @Test
    fun `a picture too short to have an end is still closed`() {
        val frames = pushAll(RtpJpegReassembler(), listOf(packet(bytes(0x42), sequence = 0)))

        assertContentEquals(bytes(0x42, 0xFF, 0xD9), frames[0].jpeg)
    }

    @Test
    fun `a picture ending in a marker other than end-of-image is still closed`() {
        val frames = pushAll(RtpJpegReassembler(), listOf(packet(bytes(0xFF, 0xD8, 0xFF, 0x00), sequence = 0)))

        assertContentEquals(bytes(0xFF, 0xD8, 0xFF, 0x00, 0xFF, 0xD9), frames[0].jpeg)
    }

    @Test
    fun `an empty first fragment followed by a restart produces no empty picture`() {
        val whole = fakeJpeg(300)

        val frames = pushAll(
            RtpJpegReassembler(),
            listOf(packet(ByteArray(0), marker = false, sequence = 0), packet(whole, sequence = 1)),
        )

        assertEquals(1, frames.size)
        assertContentEquals(whole, frames[0].jpeg)
    }

    @Test
    fun `a version byte inside the picture does not split it`() {
        // 0x80 is how every packet this camera sends begins, so the picture is full of near misses.
        val jpeg = fakeJpeg(900, fill = 0x80)

        val frames = pushAll(RtpJpegReassembler(), packets(jpeg, 640, 360, fragmentSize = 10_000))

        assertEquals(1, frames.size)
        assertContentEquals(jpeg, frames[0].jpeg)
    }

    @Test
    fun `the sequence number wrapping round is not counted as a loss`() {
        val reassembler = RtpJpegReassembler()
        val jpeg = fakeJpeg(2000)
        val packets = (0 until 4).map { index ->
            val fragment = jpeg.copyOfRange(index * 500, index * 500 + 500)
            packet(fragment, fragmentOffset = index * 500, marker = index == 3, sequence = (65_534 + index) and 0xFFFF)
        }

        val frames = pushAll(reassembler, packets)

        assertContentEquals(jpeg, frames[0].jpeg)
        assertEquals(0, reassembler.packetsLost)
        assertEquals(4, reassembler.packetsRead)
    }

    @Test
    fun `a stream that never finds a packet is trimmed rather than kept forever`() {
        val reassembler = RtpJpegReassembler()
        val whole = fakeJpeg(300)

        assertTrue(reassembler.push(ByteArray((1 shl 20) + 1)).isEmpty())
        assertEquals(12, reassembler.buffered)

        val frames = pushAll(reassembler, listOf(packet(whole)))
        assertEquals(1, frames.size)
        assertContentEquals(whole, frames[0].jpeg)
    }

    @Test
    fun `rubbish before the first packet is dropped once a packet is found`() {
        val reassembler = RtpJpegReassembler()

        reassembler.push(bytes(1, 2, 3) + packet(fakeJpeg(300)))

        assertEquals(packet(fakeJpeg(300)).size, reassembler.buffered)
    }

    private fun pushAll(reassembler: RtpJpegReassembler, packets: List<ByteArray>): List<CameraFrame> {
        val frames = mutableListOf<CameraFrame>()
        for (packet in packets) {
            frames += reassembler.push(packet)
        }

        // A picture is only known to be finished when the next one starts, so a trailing marker
        // packet is how a test gets the last one out.
        frames += reassembler.push(packets(fakeJpeg(50), 640, 360, 10_000, timestamp = 99_999u).single())
        return frames
    }

    private companion object {
        const val SSRC = 0x22222222

        /** Runs of packets laid end to end, as they arrive on the wire. */
        fun joined(vararg runs: List<ByteArray>): ByteArray = runs.toList().flatten().reduce(ByteArray::plus)

        /** A byte block shaped like a JPEG, which is all reassembly needs it to be. */
        fun fakeJpeg(length: Int, fill: Int = 0x5A): ByteArray {
            val bytes = ByteArray(length) { fill.toByte() }
            bytes[0] = 0xFF.toByte()
            bytes[1] = 0xD8.toByte()
            bytes[2] = 0xFF.toByte()
            bytes[3] = 0xE0.toByte()
            bytes[length - 2] = 0xFF.toByte()
            bytes[length - 1] = 0xD9.toByte()
            return bytes
        }

        /** Wraps a picture in RTP/JPEG packets the way the reference camera does. */
        fun packets(
            jpeg: ByteArray,
            width: Int,
            height: Int,
            fragmentSize: Int,
            timestamp: UInt = 1000u,
            ssrc: Int = SSRC,
        ): List<ByteArray> {
            val packets = mutableListOf<ByteArray>()
            var sequence = 0
            for (offset in jpeg.indices step fragmentSize) {
                val size = minOf(fragmentSize, jpeg.size - offset)
                packets += packet(
                    jpeg.copyOfRange(offset, offset + size),
                    fragmentOffset = offset,
                    marker = offset + size >= jpeg.size,
                    sequence = sequence++,
                    timestamp = timestamp,
                    ssrc = ssrc,
                    width = width,
                    height = height,
                )
            }

            return packets
        }

        /** One RTP/JPEG packet: the fixed RTP header, the RFC 2435 header, then [payload]. */
        fun packet(
            payload: ByteArray,
            fragmentOffset: Int = 0,
            marker: Boolean = true,
            sequence: Int = 0,
            timestamp: UInt = 1000u,
            ssrc: Int = SSRC,
            width: Int = 640,
            height: Int = 360,
            quantisation: Int = 1,
        ): ByteArray {
            val packet = ByteArray(12 + 8 + payload.size)
            packet[0] = 0x80.toByte()
            packet[1] = (RtpJpegReassembler.JPEG_PAYLOAD_TYPE or (if (marker) 0x80 else 0x00)).toByte()
            packet[2] = (sequence shr 8).toByte()
            packet[3] = sequence.toByte()
            for (i in 0 until 4) {
                packet[4 + i] = (timestamp.toInt() shr (24 - 8 * i)).toByte()
                packet[8 + i] = (ssrc shr (24 - 8 * i)).toByte()
            }
            packet[13] = (fragmentOffset shr 16).toByte()
            packet[14] = (fragmentOffset shr 8).toByte()
            packet[15] = fragmentOffset.toByte()
            packet[16] = 1
            packet[17] = quantisation.toByte()
            packet[18] = (width / 8).toByte()
            packet[19] = (height / 8).toByte()
            payload.copyInto(packet, 20)
            return packet
        }
    }
}
