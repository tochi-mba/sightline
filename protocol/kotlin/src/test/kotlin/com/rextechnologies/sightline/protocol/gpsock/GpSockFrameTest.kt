package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.bytes
import com.rextechnologies.sightline.protocol.gpSockRefusal
import com.rextechnologies.sightline.protocol.gpSockResponse
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The GPSOCKET codec, against bytes the real camera produced.
 *
 * The expected request bytes here are not invented: the firmware hardcodes the acknowledgement
 * for the file-list command as the twelve bytes `47 50 53 4F 43 4B 45 54 02 00 03 03`, which
 * is what pins down the little-endian type and the split of the command into two bytes.
 */
class GpSockFrameTest {
    @Test
    fun `a request begins with the tag that stops the camera ignoring it`() {
        val frame = GpSockFrame.encode(GpSockCommand.GetDeviceStatus)

        assertContentEquals("GPSOCKET".toByteArray(Charsets.US_ASCII), frame.copyOfRange(0, 8))
    }

    @Test
    fun `a request carries the type little endian and the command as two bytes`() {
        val frame = GpSockFrame.encode(GpSockCommand.GetDeviceStatus)

        assertContentEquals(bytes(0x47, 0x50, 0x53, 0x4F, 0x43, 0x4B, 0x45, 0x54, 0x01, 0x00, 0x00, 0x01), frame)
    }

    @Test
    fun `the command splits into a group byte and a command byte`() {
        val cases = listOf(
            Triple(GpSockCommand.CapturePicture, 0x02, 0x00),
            Triple(GpSockCommand.RecordToggle, 0x01, 0x00),
            Triple(GpSockCommand.PlaybackGetFileList, 0x03, 0x03),
            Triple(GpSockCommand.MenuSetParameter, 0x04, 0x01),
        )

        for ((command, group, within) in cases) {
            val frame = GpSockFrame.encode(command)

            assertEquals(group.toByte(), frame[10], "the group byte of $command")
            assertEquals(within.toByte(), frame[11], "the command byte of $command")
        }
    }

    @Test
    fun `a payload follows the header with no length in front of it`() {
        val frame = GpSockFrame.encode(GpSockCommand.SetMode, bytes(CameraMode.Browse.code))

        assertEquals(13, frame.size)
        assertEquals(2.toByte(), frame[12])
    }

    @Test
    fun `an acknowledgement decodes to its command and payload`() {
        val wire = gpSockResponse(GpSockType.Ack.code, GpSockCommand.PlaybackGetFileCount.code, bytes(0x02, 0x00))

        val decoded = assertNotNull(GpSockFrame.tryDecode(wire))

        assertEquals(wire.size, decoded.consumed)
        assertTrue(decoded.response.isAck)
        assertEquals(GpSockCommand.PlaybackGetFileCount, decoded.response.command)
        assertContentEquals(bytes(0x02, 0x00), decoded.response.payload)
    }

    @Test
    fun `a refusal carries its reason where an acknowledgement carries its size`() {
        // Exactly what the firmware sends for "busy": gp_resp_set(NAK | cmd, -1, NULL, 0) is the
        // fourteen-byte header with 0xFFFF in the size slot and no payload at all. Read as a size,
        // that is 65,535 bytes that never arrive, and the app hangs on the commonest refusal.
        val wire = bytes(0x47, 0x50, 0x53, 0x4F, 0x43, 0x4B, 0x45, 0x54, 0x03, 0x00, 0x03, 0x03, 0xFF, 0xFF)

        val decoded = assertNotNull(GpSockFrame.tryDecode(wire))

        assertEquals(14, decoded.consumed)
        assertFalse(decoded.response.isAck)
        assertEquals(GpSockCommand.PlaybackGetFileList, decoded.response.command)
        assertEquals(NakCode.ServerBusy, decoded.response.nak)
    }

    @Test
    fun `a refusal takes no bytes from the frame after it`() {
        val refusal = bytes(0x47, 0x50, 0x53, 0x4F, 0x43, 0x4B, 0x45, 0x54, 0x03, 0x00, 0x03, 0x02, 0xFB, 0xFF)
        val next = gpSockResponse(GpSockType.Ack.code, GpSockCommand.GetDeviceStatus.code, bytes(7))
        val both = refusal + next

        val first = assertNotNull(GpSockFrame.tryDecode(both))
        assertEquals(NakCode.NoStorage, first.response.nak)

        val second = assertNotNull(GpSockFrame.tryDecode(both, offset = first.consumed))
        assertEquals(GpSockCommand.GetDeviceStatus, second.response.command)
        assertContentEquals(bytes(7), second.response.payload)
    }

    @Test
    fun `a frame split across two reads is not an error it is just not ready`() {
        val wire = gpSockResponse(GpSockType.Ack.code, GpSockCommand.GetParameterFile.code, bytes(1, 2, 3, 4))

        // Every prefix short of the whole frame must report "not yet" rather than throwing or
        // inventing a frame, because this is exactly what a TCP read gives you.
        for (length in 0 until wire.size) {
            assertNull(GpSockFrame.tryDecode(wire, length = length), "a prefix of $length bytes")
        }

        assertNotNull(GpSockFrame.tryDecode(wire))
    }

    @Test
    fun `two frames in one read are taken one at a time`() {
        val first = gpSockResponse(GpSockType.Ack.code, GpSockCommand.GetDeviceStatus.code, bytes(9))
        val second = gpSockResponse(GpSockType.Ack.code, GpSockCommand.PlaybackGetFileCount.code, bytes(2, 0))
        val both = first + second

        val one = assertNotNull(GpSockFrame.tryDecode(both))
        assertEquals(GpSockCommand.GetDeviceStatus, one.response.command)

        val two = assertNotNull(GpSockFrame.tryDecode(both, offset = one.consumed))
        assertEquals(GpSockCommand.PlaybackGetFileCount, two.response.command)
    }

    @Test
    fun `bytes that are not a frame are reported rather than resynchronised past`() {
        // There is no framing to resynchronise to, so quietly skipping ahead would turn a
        // desynchronised stream into silently wrong answers.
        val rubbish = ByteArray(20)

        val refused = assertFailsWith<GpSockProtocolException> { GpSockFrame.tryDecode(rubbish) }

        assertEquals("Expected a GPSOCKET frame but the bytes began 0x0000000000000000.", refused.message)
    }

    @Test
    fun `an empty acknowledgement is how a chunked answer ends`() {
        val wire = gpSockResponse(GpSockType.Ack.code, GpSockCommand.GetParameterFile.code, ByteArray(0))

        val response = assertNotNull(GpSockFrame.tryDecode(wire)).response

        assertTrue(response.isEndOfChunks)
    }

    @Test
    fun `a refusal is never mistaken for the end of a chunked answer`() {
        val wire = bytes(0x47, 0x50, 0x53, 0x4F, 0x43, 0x4B, 0x45, 0x54, 0x03, 0x00, 0x00, 0x02, 0x00, 0x00)

        val response = assertNotNull(GpSockFrame.tryDecode(wire)).response

        assertFalse(response.isEndOfChunks)
    }

    @Test
    fun `an acknowledgement that carries an answer is not the end of a chunked answer`() {
        val wire = gpSockResponse(GpSockType.Ack.code, GpSockCommand.GetParameterFile.code, bytes(0x3C))

        val response = assertNotNull(GpSockFrame.tryDecode(wire)).response

        assertFalse(response.isEndOfChunks)
    }

    @Test
    fun `a refusal built without a reason reads as no refusal rather than a guess`() {
        // Every refusal off the wire has its two-byte reason, so only a response built by hand can
        // lack one; it still must not be read past its end.
        val response = GpSockResponse(GpSockType.Nak, GpSockCommand.SetMode, bytes(0xFF))

        assertEquals(NakCode.Ok, response.nak)
        assertEquals(0, response.nakCode)
    }

    @Test
    fun `an acknowledgement is never read as a refusal whatever its payload`() {
        val wire = gpSockResponse(GpSockType.Ack.code, GpSockCommand.PlaybackGetFileCount.code, bytes(0xFF, 0xFF))

        val response = assertNotNull(GpSockFrame.tryDecode(wire)).response

        assertEquals(NakCode.Ok, response.nak)
    }

    @Test
    fun `a reason the camera gives that has no name keeps its number`() {
        val wire = gpSockRefusal(GpSockCommand.CapturePicture.code, -99)

        val response = assertNotNull(GpSockFrame.tryDecode(wire)).response

        assertNull(response.nak)
        assertEquals(-99, response.nakCode)
    }

    @Test
    fun `a frame type GPSOCKET does not define is passed on as unknown rather than guessed at`() {
        val wire = gpSockResponse(0x0004, GpSockCommand.GetDeviceStatus.code, bytes(1))

        val response = assertNotNull(GpSockFrame.tryDecode(wire)).response

        assertNull(response.type)
        assertFalse(response.isAck)
        assertEquals(NakCode.Ok, response.nak)
        assertContentEquals(bytes(1), response.payload)
    }

    @Test
    fun `an answer to a command this library does not know is passed on as unknown`() {
        val wire = gpSockResponse(GpSockType.Ack.code, 0x0999, ByteArray(0))

        val response = assertNotNull(GpSockFrame.tryDecode(wire)).response

        assertNull(response.command)
        assertTrue(response.isAck)
    }

    @Test
    fun `a frame read from the middle of a buffer uses only the bytes it was given`() {
        val frame = gpSockResponse(GpSockType.Ack.code, GpSockCommand.GetDeviceStatus.code, bytes(7, 7))
        val buffer = bytes(0xAA) + frame + bytes(0xBB)

        assertNull(GpSockFrame.tryDecode(buffer, offset = 1, length = frame.size - 1))
        val decoded = assertNotNull(GpSockFrame.tryDecode(buffer, offset = 1, length = frame.size))

        assertEquals(frame.size, decoded.consumed)
        assertContentEquals(bytes(7, 7), decoded.response.payload)
    }

    @Test
    fun `the tag handed out is a copy that cannot change what is sent`() {
        val tag = GpSockFrame.tag
        tag.fill(0)

        val expected = "GPSOCKET".toByteArray(Charsets.US_ASCII)
        assertContentEquals(expected, GpSockFrame.tag)
        assertContentEquals(expected, GpSockFrame.encode(GpSockCommand.SetMode).copyOfRange(0, 8))
    }

    @Test
    fun `every type and command is found again from its number`() {
        for (type in GpSockType.entries) {
            assertEquals(type, GpSockType.fromCode(type.code))
        }
        for (command in GpSockCommand.entries) {
            assertEquals(command, GpSockCommand.fromCode(command.code))
        }
        for (mode in CameraMode.entries) {
            assertEquals(mode, CameraMode.fromCode(mode.code))
        }
        for (reason in NakCode.entries) {
            assertEquals(reason, NakCode.fromCode(reason.code))
        }
    }
}
