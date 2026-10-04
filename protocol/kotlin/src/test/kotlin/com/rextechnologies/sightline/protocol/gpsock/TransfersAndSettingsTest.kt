package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.FakeCamera
import com.rextechnologies.sightline.protocol.WIFI_FIELD_LENGTH
import com.rextechnologies.sightline.protocol.paddedField
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.OutputStream
import java.time.LocalDateTime
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/**
 * Downloads that can be cancelled, and settings read back and written as text, against a camera
 * that behaves as the reference one did. Mirrors the .NET GpSockConnectionTests for the same paths.
 */
class TransfersAndSettingsTest {
    private val taken = LocalDateTime.of(2026, 10, 2, 14, 30)

    private suspend fun open(configure: FakeCamera.() -> Unit = {}): Pair<GpSockConnection, FakeCamera> {
        val camera = FakeCamera()
        camera.configure()
        val connection = GpSockConnection(camera)
        connection.open()
        return connection to camera
    }

    @Test
    fun `a download copies the whole file and reports its progress`(): Unit = runBlocking {
        val content = ByteArray(3500) { it.toByte() }
        val (connection, _) = open {
            addFile('A', taken, content)
            downloadChunk = 1000
            // Each read waits as a real socket's does, so the download suspends and resumes.
            answersSlowly = true
        }
        connection.setMode(CameraMode.Browse)
        val destination = ByteArrayOutputStream()
        val progress = mutableListOf<Long>()

        val written = connection.download(1, destination) { progress += it }

        assertEquals(3500L, written)
        assertContentEquals(content, destination.toByteArray())
        assertEquals(listOf(1000L, 2000L, 3000L, 3500L), progress)
    }

    @Test
    fun `a refused download says why and leaves the channel usable`(): Unit = runBlocking {
        val (connection, _) = open {
            addFile('A', taken, ByteArray(10))
            refuseDownloadWith = NakCode.WriteFail
        }
        connection.setMode(CameraMode.Browse)

        val refused = assertFailsWith<GpSockRefusedException> { connection.download(1, ByteArrayOutputStream()) }

        assertEquals(GpSockCommand.PlaybackGetRawData, refused.command)
        assertEquals(NakCode.WriteFail, refused.reason)
        assertEquals(CameraMode.Browse, connection.getStatus().mode)
    }

    @Test
    fun `a cancelled download leaves the channel ready without waiting for an answer that never comes`(): Unit =
        runBlocking {
            // The request that stops a transfer is consumed by the firmware and never answered;
            // waiting for one would stall every cancel for the whole wind-down timeout.
            val (connection, camera) = open {
                addFile('A', taken, ByteArray(10_000))
                downloadChunk = 1000
            }
            connection.setMode(CameraMode.Browse)
            val destination = ByteArrayOutputStream()
            val started = System.nanoTime()

            val job = async {
                connection.download(1, destination) { throw CancellationException("the person gave up") }
            }
            assertFailsWith<CancellationException> { job.await() }

            assertTrue(System.nanoTime() - started < GpSockConnection.WIND_DOWN_TIMEOUT_MILLIS * 500_000L)
            assertEquals(1000, destination.size())
            assertEquals(1, connection.getFileCount())
            assertEquals(0, connection.staleFramesSkipped)
            assertTrue(camera.isConnected)
        }

    @Test
    fun `a download that finished before the cancel arrived still has its answer read`(): Unit = runBlocking {
        // The last frame was on its way, so the camera answers the stopping request normally; that
        // answer must be read now or every later status would be one behind.
        val (connection, _) = open {
            addFile('A', taken, ByteArray(2000))
            downloadChunk = 1000
        }
        connection.setMode(CameraMode.Browse)
        var seen = 0L

        assertFailsWith<CancellationException> {
            connection.download(1, ByteArrayOutputStream()) {
                seen = it
                if (it == 2000L) throw CancellationException("cancelled on the last byte")
            }
        }

        assertEquals(2000L, seen)
        assertEquals(CameraMode.Browse, connection.getStatus().mode)
        assertEquals(0, connection.staleFramesSkipped)
    }

    @Test
    fun `a destination that fails mid download still leaves the channel in step`(): Unit = runBlocking {
        val (connection, _) = open {
            addFile('A', taken, ByteArray(5000))
            downloadChunk = 1000
        }
        connection.setMode(CameraMode.Browse)

        assertFailsWith<IOException> { connection.download(1, FullDisk(acceptWrites = 2)) }

        assertEquals(1, connection.getFileCount())
    }

    @Test
    fun `a camera that never winds down a cancelled download marks the channel out of step`(): Unit = runBlocking {
        val (connection, camera) = open {
            addFile('A', taken, ByteArray(4000))
            downloadChunk = 1000
        }
        connection.setMode(CameraMode.Browse)

        assertFailsWith<CancellationException> {
            connection.download(1, ByteArrayOutputStream()) {
                // From here the camera ignores everything, so the stopping request is never answered.
                camera.hangsUp = true
                throw CancellationException("cancelled")
            }
        }

        val stuck = assertFailsWith<GpSockProtocolException> { connection.getStatus() }
        assertContains(stuck.message!!, "out of step")
        assertFailsWith<GpSockProtocolException> { connection.getThumbnail(1) }
    }

    @Test
    fun `a file index outside sixteen bits is refused before it is sent`(): Unit = runBlocking {
        val (connection, _) = open()

        assertFailsWith<IllegalArgumentException> { connection.download(-1, ByteArrayOutputStream()) }
        assertFailsWith<IllegalArgumentException> { connection.download(65_536, ByteArrayOutputStream()) }
    }

    @Test
    fun `a choice setting reads back as its value id`(): Unit = runBlocking {
        val (connection, _) = open { values[MenuIds.CAPTURE_RESOLUTION] = byteArrayOf(3) }

        assertEquals(3, connection.getChoice(MenuIds.CAPTURE_RESOLUTION))
        assertEquals(0, connection.getChoice(0x7777))
    }

    @Test
    fun `a value byte above 127 reads back as itself, not as a negative number`(): Unit = runBlocking {
        val (connection, _) = open { values[MenuIds.LANGUAGE] = byteArrayOf(0xC8.toByte()) }

        assertEquals(200, connection.getChoice(MenuIds.LANGUAGE))
    }

    @Test
    fun `a camera that sends nothing for a choice is reported`(): Unit = runBlocking {
        val (connection, _) = open { values[MenuIds.BEEP_SOUND] = ByteArray(0) }

        val missing = assertFailsWith<GpSockProtocolException> { connection.getChoice(MenuIds.BEEP_SOUND) }
        assertContains(missing.message!!, "0x0204")
    }

    @Test
    fun `a text setting reads back without its padding`(): Unit = runBlocking {
        val (connection, _) = open { values[MenuIds.WIFI_NAME] = "ActionCam_x".toByteArray(Charsets.US_ASCII) }

        assertEquals("12345678", connection.getText(MenuIds.WIFI_PASSWORD))
        assertEquals("ActionCam_x", connection.getText(MenuIds.WIFI_NAME))
    }

    @Test
    fun `text is written padded to the size of the cameras field`(): Unit = runBlocking {
        // The firmware copies the whole field whatever was sent; a short write would let it copy
        // whatever followed in its buffer into the camera's password.
        val (connection, camera) = open()

        connection.setText(MenuIds.WIFI_PASSWORD, "new-pass", WIFI_FIELD_LENGTH)

        val (id, value) = camera.rawSettingsWritten.single()
        assertEquals(MenuIds.WIFI_PASSWORD, id)
        assertContentEquals(paddedField("new-pass", WIFI_FIELD_LENGTH), value)
        assertEquals("new-pass", connection.getText(MenuIds.WIFI_PASSWORD))
    }

    @Test
    fun `text the camera cannot take is refused before it is sent`(): Unit = runBlocking {
        val (connection, camera) = open()

        for (bad in listOf("", "a password far too long for this field", "pässword", "tab\there")) {
            assertFailsWith<IllegalArgumentException>(bad) { connection.setText(MenuIds.WIFI_PASSWORD, bad, 32) }
        }
        assertFailsWith<IllegalArgumentException> { connection.setText(MenuIds.WIFI_NAME, "x", 0) }
        assertFailsWith<IllegalArgumentException> { connection.setText(MenuIds.WIFI_NAME, "x", 256) }
        assertTrue(camera.rawSettingsWritten.isEmpty())
    }

    @Test
    fun `a choice that does not fit in a byte is refused before it is sent`(): Unit = runBlocking {
        val (connection, camera) = open()

        assertFailsWith<IllegalArgumentException> { connection.setSetting(MenuIds.CAPTURE_QUALITY, -1) }
        assertFailsWith<IllegalArgumentException> { connection.setSetting(MenuIds.CAPTURE_QUALITY, 256) }
        connection.setSetting(MenuIds.CAPTURE_QUALITY, 255)

        assertEquals(listOf(MenuIds.CAPTURE_QUALITY to 255), camera.settingsWritten)
    }

    /** A destination that takes a few writes and then fails, as a full disk does. */
    private class FullDisk(private val acceptWrites: Int) : OutputStream() {
        private var writes = 0

        override fun write(b: Int) = write(byteArrayOf(b.toByte()))

        override fun write(b: ByteArray, off: Int, len: Int) {
            if (++writes > acceptWrites) throw IOException("There is not enough space on the disk.")
        }
    }
}
