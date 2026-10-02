package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.FakeCamera
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.runBlocking
import kotlin.test.Test
import kotlin.test.assertContains
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
    fun `browsing the card before switching mode is refused with a reason`(): Unit = runBlocking {
        val (connection, _) = open()

        val refused = assertFailsWith<GpSockRefusedException> { connection.getFileCount() }

        assertEquals(NakCode.ServerBusy, refused.reason)
        assertContains(assertNotNull(refused.message), "busy")
    }

    @Test
    fun `browsing works once the camera is in browse mode`(): Unit = runBlocking {
        val (connection, _) = open { fileCount = 7 }

        connection.setMode(CameraMode.Browse)
        val count = connection.getFileCount()

        assertEquals(7, count)
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

        val refused = assertFailsWith<GpSockRefusedException> { connection.demand(GpSockCommand.PowerOff) }

        assertEquals(NakCode.InvalidCommand, refused.reason)
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
            fileCount = 4
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

        val refused = assertFailsWith<GpSockRefusedException> {
            connection.askForChunks(GpSockCommand.PlaybackGetThumbnail, byteArrayOf(0, 0))
        }

        assertEquals(GpSockCommand.PlaybackGetThumbnail, refused.command)
        assertEquals(NakCode.InvalidCommand, refused.reason)
    }

    @Test
    fun `a file count the camera leaves out reads as none`(): Unit = runBlocking {
        val (connection, _) = open { answers[GpSockCommand.PlaybackGetFileCount] = ByteArray(1) }

        assertEquals(0, connection.getFileCount())
    }

    @Test
    fun `a file count past 255 is read as the little-endian number it is`(): Unit = runBlocking {
        val (connection, _) = open { fileCount = 0x0123 }
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
}
