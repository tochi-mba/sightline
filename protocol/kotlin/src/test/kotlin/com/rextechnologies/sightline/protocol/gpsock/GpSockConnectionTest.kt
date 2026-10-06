package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.FakeCamera
import com.rextechnologies.sightline.protocol.bytes
import com.rextechnologies.sightline.protocol.gpSockResponse
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.yield
import java.time.LocalDateTime
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** The control channel, against a camera that behaves the way the real one did. */
class GpSockConnectionTest {
    private suspend fun open(configure: FakeCamera.() -> Unit = {}): Pair<GpSockConnection, FakeCamera> {
        val camera = FakeCamera()
        camera.configure()
        val connection = GpSockConnection(camera)
        connection.open()
        return connection to camera
    }

    /** Puts [count] files on the card, photographs and videos in turn, a minute apart. */
    private fun FakeCamera.addFiles(count: Int, size: Int = 100) {
        for (i in 0 until count) {
            addFile(if (i % 2 == 0) 'J' else 'A', TAKEN.plusMinutes(i.toLong()), ByteArray(size) { i.toByte() })
        }
    }

    @Test
    fun `it reads the camera state`(): Unit = runBlocking {
        val (connection, _) = open()

        val status = connection.getStatus()

        assertEquals(CameraMode.Record, status.mode)
        assertEquals(16, status.length)
    }

    @Test
    fun `it gathers a chunked answer into one document`(): Unit = runBlocking {
        // The menu is far longer than the camera's 242-byte chunk, so this is the path that
        // silently truncated everything if a reader assumed one frame per answer.
        val longMenu = " ".repeat(2000)
        val (connection, _) = open {
            menuXml = "<Menu><Categories><Category><Name>Record</Name><!--$longMenu--><Settings>" +
                "<Setting><Name>Resolution</Name><ID>0x0</ID><Type>0x00</Type><Default>0x0</Default>" +
                "<Values><Value><ID>0x00</ID><Name>4K</Name></Value></Values></Setting>" +
                "</Settings></Category></Categories></Menu>"
        }

        val menu = connection.getMenu()

        assertEquals(1, menu.settings.size)
        assertEquals("4K", assertNotNull(menu.find(0)).labelFor(0))
    }

    @Test
    fun `a frame arriving a few bytes at a time is still read correctly`(): Unit = runBlocking {
        // TCP does not promise frame boundaries, and this camera sends no length on requests, so
        // a reader that assumed one read per frame would work on a desk and fail on a bad link.
        val (connection, _) = open { dribbleBytes = 3 }

        val status = connection.getStatus()

        assertEquals(CameraMode.Record, status.mode)
    }

    @Test
    fun `taking a photograph tells the camera to`(): Unit = runBlocking {
        val (connection, camera) = open()

        connection.capturePicture()

        assertEquals(1, camera.picturesTaken)
    }

    @Test
    fun `recording toggles rather than taking an argument`(): Unit = runBlocking {
        val (connection, camera) = open()

        connection.toggleRecording()
        assertTrue(camera.isRecording)

        connection.toggleRecording()
        assertFalse(camera.isRecording)
    }

    @Test
    fun `streaming has to be started explicitly`(): Unit = runBlocking {
        val (connection, camera) = open()
        assertFalse(camera.isStreaming)

        connection.startStreaming()

        assertTrue(camera.isStreaming)
    }

    @Test
    fun `turning the camera off is a command it acknowledges`(): Unit = runBlocking {
        val (connection, camera) = open()

        connection.powerOff()

        assertTrue(camera.isPoweredOff)
    }

    @Test
    fun `browsing the card before switching mode is refused with a reason`(): Unit = runBlocking {
        val (connection, _) = open { addFiles(2) }

        val refused = assertFailsWith<GpSockRefusedException> { connection.getFileCount() }

        assertEquals(NakCode.ServerBusy, refused.reason)
        assertContains(assertNotNull(refused.message), "busy")
    }

    @Test
    fun `browsing works once the camera is in browse mode`(): Unit = runBlocking {
        val (connection, _) = open { addFiles(7) }

        connection.setMode(CameraMode.Browse)
        val count = connection.getFileCount()

        assertEquals(7, count)
    }

    @Test
    fun `an empty card counts as no files rather than an error`(): Unit = runBlocking {
        // The firmware refuses the count on an empty card with "no storage", exactly as it would
        // with no card at all; to a person both mean there is nothing to show.
        val (connection, _) = open()
        connection.setMode(CameraMode.Browse)

        assertEquals(0, connection.getFileCount())
        assertTrue(connection.getFileList().isEmpty())
    }

    @Test
    fun `the file list is read page by page until every file is in`(): Unit = runBlocking {
        val (connection, _) = open {
            addFiles(10)
            pageSize = 4
        }
        connection.setMode(CameraMode.Browse)

        val files = connection.getFileList()

        assertEquals((1..10).toList(), files.map { it.index })
        assertEquals(CameraFileKind.Photo, files[0].kind)
        assertEquals(CameraFileKind.Video, files[1].kind)
        assertEquals(TAKEN.plusMinutes(3), files[3].taken)
    }

    @Test
    fun `a camera that repeats its first page does not keep the app paging forever`(): Unit = runBlocking {
        val (connection, _) = open {
            addFiles(10)
            pageSize = 4
            repeatsFirstPage = true
        }
        connection.setMode(CameraMode.Browse)

        val files = connection.getFileList()

        assertEquals(listOf(1, 2, 3, 4), files.map { it.index })
    }

    @Test
    fun `listing files outside browse mode is refused`(): Unit = runBlocking {
        val (connection, _) = open { addFiles(3) }

        val refused = assertFailsWith<GpSockRefusedException> { connection.getFileList() }

        assertEquals(NakCode.ServerBusy, refused.reason)
    }

    @Test
    fun `a page with more files than the count stops at the count`(): Unit = runBlocking {
        // The count and the pages are separate answers, and a file can be written between them.
        // The count is what was asked about, so the list is held to it.
        val (connection, camera) = open {
            addFiles(4)
            pageSize = 4
        }
        connection.setMode(CameraMode.Browse)
        camera.forcedAnswers[GpSockCommand.PlaybackGetFileCount] = bytes(3, 0)

        val files = connection.getFileList()

        assertEquals(listOf(1, 2, 3), files.map { it.index })
    }

    @Test
    fun `a thumbnail comes back as the bytes of a picture`(): Unit = runBlocking {
        val (connection, _) = open { addFiles(2) }
        connection.setMode(CameraMode.Browse)

        val thumbnail = connection.getThumbnail(2)

        assertContentEquals(bytes(0xFF, 0xD8, 2, 0xFF, 0xD9), thumbnail)
    }

    @Test
    fun `a thumbnail is refused while the camera is streaming`(): Unit = runBlocking {
        // Browse mode stops the stream; starting it again there is what leaves the camera busy.
        val (connection, _) = open { addFiles(2) }
        connection.setMode(CameraMode.Browse)
        connection.startStreaming()

        val refused = assertFailsWith<GpSockRefusedException> { connection.getThumbnail(1) }

        assertEquals(NakCode.ServerBusy, refused.reason)
    }

    @Test
    fun `a file index outside sixteen bits is refused before it is sent`(): Unit = runBlocking {
        val (connection, camera) = open { addFiles(1) }

        assertFailsWith<IllegalArgumentException> { connection.getThumbnail(-1) }
        assertFailsWith<IllegalArgumentException> { connection.deleteFile(65_536) }

        assertEquals(1, camera.files.size)
    }

    @Test
    fun `deleting a file removes it from the card`(): Unit = runBlocking {
        val (connection, camera) = open { addFiles(3) }
        connection.setMode(CameraMode.Browse)

        connection.deleteFile(2)

        assertEquals(listOf(1, 3), camera.files.map { it.index })
    }

    @Test
    fun `a camera built without delete refuses it`(): Unit = runBlocking {
        val (connection, camera) = open {
            addFiles(1)
            supportsDelete = false
        }
        connection.setMode(CameraMode.Browse)

        assertFailsWith<GpSockRefusedException> { connection.deleteFile(1) }

        assertEquals(1, camera.files.size)
    }

    @Test
    fun `writing a setting sends the id and the value`(): Unit = runBlocking {
        val (connection, camera) = open()

        connection.setSetting(0x0100, 3)

        assertEquals(listOf(0x0100 to 3), camera.settingsWritten)
    }

    @Test
    fun `a command the camera does not know is reported as such`(): Unit = runBlocking {
        val (connection, _) = open()

        val refused = assertFailsWith<GpSockRefusedException> { connection.demand(GpSockCommand.AuthDevice) }

        assertEquals(NakCode.InvalidCommand, refused.reason)
    }

    @Test
    fun `a leftover answer to an abandoned request is skipped, not taken as the reply`(): Unit = runBlocking {
        val (connection, camera) = open()
        camera.strayFramesBeforeNextAnswer +=
            gpSockResponse(GpSockType.Ack.code, GpSockCommand.PlaybackGetRawData.code, bytes(1, 2, 3))

        val status = connection.getStatus()

        assertEquals(16, status.length)
        assertEquals(1, connection.staleFramesSkipped)
    }

    @Test
    fun `the answer to a request given up on is skipped by the request after it`(): Unit = runBlocking {
        // A caller that stops waiting, cancelled or timed out, leaves its answer on the way. The next
        // request must not take that answer for its own.
        val (connection, _) = open { answersSlowly = true }
        val abandoned = launch { connection.ask(GpSockCommand.GetDeviceStatus) }
        // Sent, and waiting for the answer.
        yield()
        abandoned.cancelAndJoin()

        val answer = connection.ask(GpSockCommand.CapturePicture)

        assertEquals(GpSockCommand.CapturePicture, answer.command)
        assertEquals(1, connection.staleFramesSkipped)
    }

    @Test
    fun `a camera that hangs up is reported rather than waited on forever`(): Unit = runBlocking {
        val camera = FakeCamera().apply { hangsUp = true }
        val connection = GpSockConnection(camera)
        connection.open()

        // The camera takes the command and never answers, which is what its access point going to
        // sleep mid-session looks like from here.
        assertFailsWith<GpSockProtocolException> { connection.ask(GpSockCommand.GetDeviceStatus) }
    }

    @Test
    fun `commands sent at the same moment each get their own answer`(): Unit = runBlocking {
        // The live view starts the stream while a person presses the shutter, both down one socket.
        // Each must read the answer to its own command, not the other's. A slow link that brings a
        // few bytes at a time is what gives the second request its chance to cut in.
        val (connection, camera) = open {
            addFiles(4)
            answersSlowly = true
            dribbleBytes = 3
        }
        connection.setMode(CameraMode.Browse)

        val expected = List(20) { index ->
            if (index % 2 == 0) GpSockCommand.GetDeviceStatus else GpSockCommand.PlaybackGetFileCount
        }
        val answers = expected.map { command -> async { connection.ask(command) } }.awaitAll()

        assertEquals(expected, answers.map { it.command })
        assertTrue(camera.isConnected)
    }

    @Test
    fun `every refusal code turns into a sentence rather than a number`() {
        for (code in NakCode.entries) {
            val explained = GpSockRefusedException.explain(code)

            assertTrue(explained.isNotBlank(), "$code has no explanation")
            assertFalse(explained.contains("reason "), "$code is explained by number")
            assertEquals(explained, GpSockRefusedException.explain(code.code))
        }
    }

    @Test
    fun `a refusal the camera gives no name for is still reported, by number`() {
        val refused = GpSockRefusedException(GpSockCommand.CapturePicture, -99)

        assertNull(refused.reason)
        assertEquals("reason -99", GpSockRefusedException.explain(-99))
        assertEquals("The camera refused CapturePicture: reason -99.", refused.message)
    }

    @Test
    fun `a refusal names the command and the reason in words`(): Unit = runBlocking {
        val (connection, _) = open()

        val refused = assertFailsWith<GpSockRefusedException> { connection.getFileCount() }

        assertEquals(GpSockCommand.PlaybackGetFileCount, refused.command)
        assertEquals(NakCode.ServerBusy.code, refused.code)
        assertEquals(
            "The camera refused PlaybackGetFileCount: it is busy, which usually means it is in the wrong mode " +
                "or still streaming.",
            refused.message,
        )
    }

    @Test
    fun `a chunked answer reports its running total for a progress bar`(): Unit = runBlocking {
        val (connection, camera) = open { menuXml = "x".repeat(500) }
        val totals = mutableListOf<Int>()

        val gathered = connection.askForChunks(GpSockCommand.GetParameterFile, onProgress = { totals += it })

        assertEquals(camera.menuXml, gathered.toString(Charsets.UTF_8))
        assertEquals(listOf(242, 484, 500), totals)
    }

    @Test
    fun `a chunked answer the camera refuses is reported rather than gathered`(): Unit = runBlocking {
        val (connection, _) = open()
        connection.setMode(CameraMode.Browse)

        // There is no file 9 on an empty card.
        val refused = assertFailsWith<GpSockRefusedException> {
            connection.askForChunks(GpSockCommand.PlaybackGetThumbnail, byteArrayOf(9, 0))
        }

        assertEquals(GpSockCommand.PlaybackGetThumbnail, refused.command)
        assertEquals(NakCode.InvalidCommand, refused.reason)
    }

    @Test
    fun `a file count the camera leaves out reads as none`(): Unit = runBlocking {
        val (connection, _) = open { forcedAnswers[GpSockCommand.PlaybackGetFileCount] = ByteArray(1) }

        assertEquals(0, connection.getFileCount())
    }

    @Test
    fun `a file count past 255 is read as the little-endian number it is`(): Unit = runBlocking {
        val (connection, _) = open { addFiles(0x0123, size = 1) }
        connection.setMode(CameraMode.Browse)

        assertEquals(0x0123, connection.getFileCount())
    }

    @Test
    fun `asking does not throw on a refusal so the caller can read it`(): Unit = runBlocking {
        val (connection, _) = open()

        val response = connection.ask(GpSockCommand.PlaybackGetFileCount)

        assertFalse(response.isAck)
        assertEquals(NakCode.ServerBusy, response.nak)
    }

    @Test
    fun `closing the connection is what tells the camera the session is over`(): Unit = runBlocking {
        val (connection, camera) = open()
        connection.toggleRecording()
        connection.startStreaming()

        connection.close()

        assertTrue(camera.wasDisposed)
        assertFalse(camera.isConnected)
        assertFalse(camera.isRecording)
        assertFalse(camera.isStreaming)
    }

    @Test
    fun `opening the control channel connects its transport`(): Unit = runBlocking {
        val camera = FakeCamera()

        GpSockConnection(camera).open()

        assertTrue(camera.isConnected)
    }

    @Test
    fun `the control channel listens where the camera does`() {
        assertEquals(8081, GpSockConnection.PORT)
        assertEquals(242, GpSockConnection.MAX_CHUNK_PAYLOAD)
    }

    private companion object {
        val TAKEN: LocalDateTime = LocalDateTime.of(2026, 10, 2, 14, 30, 0)
    }
}
