package com.rextechnologies.sightline.protocol.rtp

import com.rextechnologies.sightline.protocol.bytes
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Reassembly, one datagram at a time, against packets shaped like the reference camera's.
 *
 * The packets here are synthetic: the real capture is a picture of somebody's room and belongs in an
 * ignored folder, not in a public repository. Their shape is taken from the real one — payload type 26, a
 * fixed synchronisation source, the marker on each picture's last fragment, and a complete JFIF document
 * inside the payload rather than the stripped form RFC 2435 describes. The same cases as the .NET tests.
 */
class RtpJpegReassemblerTest {
    @Test
    fun `one packet carrying a whole picture produces that picture at once`() {
        val jpeg = fakeJpeg(600)
        val reassembler = RtpJpegReassembler()

        val frame = assertNotNull(reassembler.push(Rtp(jpeg, timestamp = 7380).build()))

        assertContentEquals(jpeg, frame.jpeg)
        assertEquals(640, frame.width)
        assertEquals(360, frame.height)
        assertEquals(7380u, frame.rtpTimestamp)
        assertEquals(1, reassembler.packetsRead)
    }

    @Test
    fun `a picture split across packets comes out whole when its marked last fragment arrives`() {
        val jpeg = fakeJpeg(3000)
        val reassembler = RtpJpegReassembler()
        val packets = fragments(jpeg, 700)

        packets.dropLast(1).forEach { assertNull(reassembler.push(it)) }

        assertContentEquals(jpeg, reassembler.push(packets.last())?.jpeg)
        assertEquals(0, reassembler.picturesDropped)
    }

    @Test
    fun `pictures are put together whatever the size of their fragments`() {
        // Offsets under 256 have only their last byte set; offsets past 65535 have their first.
        val small = fakeJpeg(450)
        val large = fakeJpeg(120_000)
        val reassembler = RtpJpegReassembler()

        val packets = fragments(small, 100) + fragments(large, 50_000, timestamp = 8380, sequence = 5)

        val frames = pushAll(reassembler, packets)

        assertContentEquals(small, frames[0].jpeg)
        assertContentEquals(large, frames[1].jpeg)
    }

    @Test
    fun `only the length given of a reused buffer is read`() {
        val jpeg = fakeJpeg(200)
        val packet = Rtp(jpeg).build()
        val buffer = packet + ByteArray(50) { 0x77 }

        assertContentEquals(jpeg, RtpJpegReassembler().push(buffer, packet.size)?.jpeg)
    }

    @Test
    fun `the zeros the camera pads a picture with after its end are not part of it`() {
        // Measured on 2026-10-06: up to seven zeros after FF D9, to a multiple of eight bytes. Taken as part of
        // the picture, they made most pictures fail the whole-JPEG check, and the live view ran at two a second.
        val jpeg = fakeJpeg(8829)

        val frame = assertNotNull(RtpJpegReassembler().push(Rtp(jpeg + ByteArray(3)).build()))

        assertContentEquals(jpeg, frame.jpeg)
        assertTrue(RtpJpegReassembler.looksLikeJpeg(frame.jpeg))
    }

    @Test
    fun `zeros with no end of image before them are left alone`() {
        val truncated = bytes(0xFF, 0xD8, 0xFF, 0xE0, 0x5A, 0x00, 0x00)

        assertContentEquals(truncated, RtpJpegReassembler().push(Rtp(truncated).build())?.jpeg)
        assertContentEquals(bytes(0, 0), RtpJpegReassembler().push(Rtp(bytes(0, 0)).build())?.jpeg)
        assertContentEquals(bytes(0xD9, 0), RtpJpegReassembler().push(Rtp(bytes(0xD9, 0)).build())?.jpeg)
        val notAnEnd = bytes(0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0x5A, 0x00)
        assertContentEquals(notAnEnd, RtpJpegReassembler().push(Rtp(notAnEnd).build())?.jpeg)
    }

    @Test
    fun `pictures one after another each come out`() {
        val first = fakeJpeg(900, 0x11)
        val second = fakeJpeg(1300, 0x22)
        val reassembler = RtpJpegReassembler()

        val packets = fragments(first, 500) + fragments(second, 500, timestamp = 8380, sequence = 2)

        val frames = pushAll(reassembler, packets)

        assertEquals(2, frames.size)
        assertContentEquals(first, frames[0].jpeg)
        assertContentEquals(second, frames[1].jpeg)
        assertEquals(0, reassembler.packetsLost)
    }

    @Test
    fun `a picture that lost a fragment is dropped and the next one still comes`() {
        // UDP drops a packet now and then; a picture with a hole in it decodes as a smear.
        val damaged = fragments(fakeJpeg(2000, 0x11), 500)
        val next = fakeJpeg(800, 0x22)
        val reassembler = RtpJpegReassembler()

        val frames = pushAll(
            reassembler,
            listOf(damaged[0], damaged[2], damaged[3]) + fragments(next, 500, timestamp = 8380, sequence = 4),
        )

        assertContentEquals(next, frames.single().jpeg)
        assertEquals(1, reassembler.picturesDropped)
        assertEquals(1, reassembler.packetsLost)
    }

    @Test
    fun `a picture whose last fragment never came is dropped when the next one starts`() {
        val cut = fragments(fakeJpeg(1500, 0x11), 500)
        val next = fakeJpeg(400, 0x22)
        val reassembler = RtpJpegReassembler()

        val frames = pushAll(reassembler, listOf(cut[0], cut[1]) + fragments(next, 500, timestamp = 8380, sequence = 3))

        assertContentEquals(next, frames.single().jpeg)
        assertEquals(1, reassembler.picturesDropped)
    }

    @Test
    fun `fragments of a picture joined part-way through are let go without counting a drop`() {
        val joinedLate = fragments(fakeJpeg(1500, 0x11), 500)
        val next = fakeJpeg(400, 0x22)
        val reassembler = RtpJpegReassembler()

        val frames = pushAll(
            reassembler,
            listOf(joinedLate[1], joinedLate[2]) + fragments(next, 500, timestamp = 8380, sequence = 3),
        )

        assertContentEquals(next, frames.single().jpeg)
        assertEquals(0, reassembler.picturesDropped)
    }

    @Test
    fun `a fragment stamped for another picture drops the one being put together`() {
        val reassembler = RtpJpegReassembler()
        reassembler.push(Rtp(ByteArray(500), marker = false, timestamp = 1000).build())

        val stray = reassembler.push(Rtp(ByteArray(100), offset = 500, timestamp = 2000, sequence = 1).build())

        assertNull(stray)
        assertEquals(1, reassembler.picturesDropped)
    }

    @Test
    fun `lost packets are counted by the size of the gap`() {
        val reassembler = RtpJpegReassembler()

        reassembler.push(Rtp(fakeJpeg(100), sequence = 10).build())
        reassembler.push(Rtp(fakeJpeg(100), sequence = 14).build())

        assertEquals(3, reassembler.packetsLost)
    }

    @Test
    fun `a sequence number wrapping past its top is not a loss`() {
        val reassembler = RtpJpegReassembler()

        reassembler.push(Rtp(fakeJpeg(100), sequence = 65535).build())
        reassembler.push(Rtp(fakeJpeg(100), sequence = 0).build())

        assertEquals(0, reassembler.packetsLost)
    }

    @Test
    fun `a packet arriving late or twice is not taken for thousands lost`() {
        val reassembler = RtpJpegReassembler()

        reassembler.push(Rtp(fakeJpeg(100), sequence = 20).build())
        reassembler.push(Rtp(fakeJpeg(100), sequence = 19).build())
        reassembler.push(Rtp(fakeJpeg(100), sequence = 19).build())

        assertEquals(0, reassembler.packetsLost)
    }

    @Test
    fun `a new sender starts afresh without its numbering counting as loss`() {
        // The camera starting a new stream: nothing in hand belongs with it, and nothing was lost.
        val reassembler = RtpJpegReassembler()
        reassembler.push(Rtp(ByteArray(500), marker = false, sequence = 900).build())

        val jpeg = fakeJpeg(300)
        val frame = reassembler.push(Rtp(jpeg, sequence = 4, ssrc = 0x33333333).build())

        assertContentEquals(jpeg, frame?.jpeg)
        assertEquals(0, reassembler.packetsLost)
        assertEquals(0, reassembler.picturesDropped)
    }

    @Test
    fun `a datagram too short for an rtp header is ignored`() {
        val reassembler = RtpJpegReassembler()

        assertNull(reassembler.push(bytes(0x80, 0x9A, 0, 1, 0, 0, 0, 1, 0x22, 0x22, 0x22)))
        assertEquals(0, reassembler.packetsRead)
    }

    @Test
    fun `a packet of another rtp version is ignored`() {
        val reassembler = RtpJpegReassembler()

        assertNull(reassembler.push(Rtp(fakeJpeg(100), version = 1).build()))
        assertEquals(0, reassembler.packetsRead)
    }

    @Test
    fun `a packet that is not jpeg such as the camera's sound is ignored`() {
        val reassembler = RtpJpegReassembler()

        assertNull(reassembler.push(Rtp(fakeJpeg(100), payloadType = 97).build()))
        assertEquals(0, reassembler.packetsRead)
    }

    @Test
    fun `contributing sources are stepped over`() {
        val jpeg = fakeJpeg(400)

        assertContentEquals(jpeg, RtpJpegReassembler().push(Rtp(jpeg, csrcs = 2).build())?.jpeg)
    }

    @Test
    fun `a header extension is stepped over`() {
        val jpeg = fakeJpeg(400)

        val frame = RtpJpegReassembler().push(Rtp(jpeg, extension = ByteArray(8) { it.toByte() }).build())

        assertContentEquals(jpeg, frame?.jpeg)
    }

    @Test
    fun `a header extension cut short by the end of the packet is ignored`() {
        val packet = ByteArray(14)
        packet[0] = 0x90.toByte()
        packet[1] = RtpJpegReassembler.JPEG_PAYLOAD_TYPE.toByte()

        assertNull(RtpJpegReassembler().push(packet))
    }

    @Test
    fun `padding at the end is not taken for picture`() {
        val jpeg = fakeJpeg(400)

        assertContentEquals(jpeg, RtpJpegReassembler().push(Rtp(jpeg, padding = 3).build())?.jpeg)
    }

    @Test
    fun `a restart marker header is stepped over`() {
        val jpeg = fakeJpeg(400)

        val frame = RtpJpegReassembler().push(Rtp(jpeg, type = 65, restart = bytes(0, 8, 0xFF, 0xFF)).build())

        assertContentEquals(jpeg, frame?.jpeg)
    }

    @Test
    fun `inline quantisation tables are stepped over on the first fragment only`() {
        // This camera sends whole JFIF headers instead, but RFC 2435 allows tables in the first packet.
        val jpeg = fakeJpeg(1000)
        val reassembler = RtpJpegReassembler()
        val first = Rtp(jpeg.copyOfRange(0, 600), marker = false, quality = 255, tables = ByteArray(128))
        assertNull(reassembler.push(first.build()))

        val rest = Rtp(jpeg.copyOfRange(600, 1000), offset = 600, quality = 255, sequence = 1)
        val frame = reassembler.push(rest.build())

        assertContentEquals(jpeg, frame?.jpeg)
    }

    @Test
    fun `a packet too short for its jpeg header is ignored`() {
        val packet = Rtp(ByteArray(0)).build().copyOf(19)

        assertNull(RtpJpegReassembler().push(packet))
    }

    @Test
    fun `a first fragment too short for the tables it promises is ignored`() {
        assertNull(RtpJpegReassembler().push(Rtp(ByteArray(0), quality = 200).build()))
    }

    @Test
    fun `tables claiming more than the packet holds are ignored`() {
        val packet = Rtp(ByteArray(0), quality = 200, tables = ByteArray(10)).build()
        ByteBuffer.wrap(packet).putShort(20 + 2, 500)

        assertNull(RtpJpegReassembler().push(packet))
    }

    @Test
    fun `a block is called a jpeg only when it starts and ends like one`() {
        assertTrue(RtpJpegReassembler.looksLikeJpeg(fakeJpeg(100)))
        assertFalse(RtpJpegReassembler.looksLikeJpeg(bytes(0xFF, 0xD8, 0xFF, 0xD9)))
        assertFalse(RtpJpegReassembler.looksLikeJpeg(bytes(0x00, 0xD8, 0xFF, 0x00, 0xFF, 0xD9)))
        assertFalse(RtpJpegReassembler.looksLikeJpeg(bytes(0xFF, 0x00, 0xFF, 0x00, 0xFF, 0xD9)))
        assertFalse(RtpJpegReassembler.looksLikeJpeg(bytes(0xFF, 0xD8, 0x00, 0x00, 0xFF, 0xD9)))
        assertFalse(RtpJpegReassembler.looksLikeJpeg(bytes(0xFF, 0xD8, 0xFF, 0x00, 0x00, 0xD9)))
        assertFalse(RtpJpegReassembler.looksLikeJpeg(bytes(0xFF, 0xD8, 0xFF, 0x00, 0xFF, 0x00)))
    }

    private fun pushAll(reassembler: RtpJpegReassembler, packets: List<ByteArray>): List<CameraFrame> =
        packets.mapNotNull { reassembler.push(it) }

    /** A picture cut into fragments the way the reference camera cuts them, numbered from [sequence]. */
    private fun fragments(jpeg: ByteArray, size: Int, timestamp: Int = 1000, sequence: Int = 0): List<ByteArray> {
        var next = sequence
        return (jpeg.indices step size).map { offset ->
            val end = minOf(offset + size, jpeg.size)
            Rtp(jpeg.copyOfRange(offset, end), offset, end == jpeg.size, timestamp, next++).build()
        }
    }

    /** A byte block shaped like a JPEG, which is all reassembly needs it to be. */
    private fun fakeJpeg(length: Int, fill: Int = 0x5A): ByteArray {
        val bytes = ByteArray(length) { fill.toByte() }
        bytes[0] = 0xFF.toByte()
        bytes[1] = 0xD8.toByte()
        bytes[2] = 0xFF.toByte()
        bytes[3] = 0xE0.toByte()
        bytes[length - 2] = 0xFF.toByte()
        bytes[length - 1] = 0xD9.toByte()
        return bytes
    }

    /** One RTP/JPEG packet with every field under the test's control, laid out as RFC 3550 and RFC 2435 say. */
    private class Rtp(
        val payload: ByteArray,
        val offset: Int = 0,
        val marker: Boolean = true,
        val timestamp: Int = 1000,
        val sequence: Int = 0,
        val ssrc: Int = SSRC,
        val type: Int = 1,
        val quality: Int = 1,
        val csrcs: Int = 0,
        val extension: ByteArray? = null,
        val padding: Int = 0,
        val restart: ByteArray? = null,
        val tables: ByteArray? = null,
        val payloadType: Int = RtpJpegReassembler.JPEG_PAYLOAD_TYPE,
        val version: Int = 2,
    ) {
        fun build(): ByteArray {
            val out = ByteArrayOutputStream()
            val padded = if (padding > 0) 0x20 else 0
            val extended = if (extension == null) 0 else 0x10
            val flags = (version shl 6) or padded or extended or csrcs
            out.write(flags)
            out.write(payloadType or if (marker) 0x80 else 0)
            out.write16(sequence)
            out.write32(timestamp)
            out.write32(ssrc)
            repeat(csrcs) { out.write32(0x44444444) }
            extension?.let {
                out.write16(0xBEDE)
                out.write16(it.size / 4)
                out.write(it)
            }

            out.write(0)
            out.write(offset shr 16)
            out.write(offset shr 8)
            out.write(offset)
            out.write(byteArrayOf(type.toByte(), quality.toByte(), (640 / 8).toByte(), (360 / 8).toByte()))
            restart?.let { out.write(it) }
            tables?.let {
                out.write(0)
                out.write(0)
                out.write16(it.size)
                out.write(it)
            }

            out.write(payload)
            for (i in 1..padding) {
                out.write(if (i == padding) padding else 0)
            }

            return out.toByteArray()
        }

        private fun ByteArrayOutputStream.write16(value: Int) {
            write(value shr 8)
            write(value)
        }

        private fun ByteArrayOutputStream.write32(value: Int) {
            write16(value ushr 16)
            write16(value)
        }
    }

    private companion object {
        const val SSRC = 0x22222222
    }
}
