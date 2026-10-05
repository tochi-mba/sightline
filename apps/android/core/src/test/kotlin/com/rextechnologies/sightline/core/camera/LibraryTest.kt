package com.rextechnologies.sightline.core.camera

import com.rextechnologies.sightline.core.library.MediaKind
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import com.rextechnologies.sightline.protocol.gpsock.GpSockCommand
import com.rextechnologies.sightline.protocol.gpsock.NakCode
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runTest
import java.time.LocalDateTime
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** The camera's card: listing it, copying from it and deleting from it. */
class LibraryTest {
    private val taken = LocalDateTime.of(2026, 10, 4, 18, 35, 56)
    private val photo = FakeRtspCamera.jpeg(3000)
    private val video = ByteArray(5000).also {
        "RIFF".toByteArray(Charsets.US_ASCII).copyInto(it)
        "AVI ".toByteArray(Charsets.US_ASCII).copyInto(it, 8)
    }

    /** A camera with a photo and a clip on its card, connected. */
    private suspend fun TestScope.cameraWithCard(): ControllerHarness {
        val camera = ControllerHarness(this)
        camera.control.addFile('J', taken, photo)
        camera.control.addFile('A', taken.plusMinutes(1), video)
        camera.control.downloadChunk = 1000
        camera.connected()
        return camera
    }

    private suspend fun ControllerHarness.listed(): List<CameraFile> {
        controller.refreshLibrary()!!.join()
        return state.library.files!!
    }

    @Test
    fun `the card is listed and each thumbnail fetched, with the camera put back after`() = runTest {
        val camera = cameraWithCard()
        camera.control.isStreaming = false

        val files = camera.listed()

        assertEquals(listOf('J', 'A'), files.map { it.code })
        assertEquals(files.toSet(), camera.state.library.thumbnails.keys)
        assertFalse(camera.state.library.reading)
        assertTrue(camera.seen.any { it.library.reading && it.task == Task.ReadingCard })
        assertEquals(CameraMode.Record, camera.control.mode)
        assertEquals(CameraMode.Record, camera.state.status?.mode)
    }

    @Test
    fun `reading the card pauses the live view and starts it again after`() = runTest {
        val camera = cameraWithCard()
        camera.controller.holdLive("screen")
        camera.until { it.live is LiveView.Playing }

        camera.listed()

        assertTrue(camera.seen.any { it.live == LiveView.Paused })
        assertEquals("TEARDOWN", camera.streams.first().verbs.last())
        assertTrue(camera.until { it.live is LiveView.Playing }.live is LiveView.Playing)
        assertEquals(2, camera.streams.size)
    }

    @Test
    fun `files whose thumbnails the camera will not give are listed anyway`() = runTest {
        // The fake refuses thumbnails while the camera's media flow is running, as the firmware source does.
        val camera = cameraWithCard()
        camera.control.isStreaming = true

        val files = camera.listed()

        assertEquals(2, files.size)
        assertTrue(camera.state.library.thumbnails.isEmpty())
    }

    @Test
    fun `thumbnails already fetched are kept and those of files gone are dropped`() = runTest {
        val camera = cameraWithCard()
        camera.control.isStreaming = false
        val first = camera.listed()

        camera.controller.delete(listOf(first[0]))!!.join()
        camera.control.addFile('J', taken.plusMinutes(2), photo)
        val second = camera.listed()

        assertEquals(listOf(2, 3), second.map { it.index })
        assertEquals(second.toSet(), camera.state.library.thumbnails.keys)
    }

    @Test
    fun `an empty card lists no files`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()

        assertEquals(emptyList(), camera.listed())
    }

    @Test
    fun `a refused list is explained and the camera still put back`() = runTest {
        val camera = cameraWithCard()
        camera.control.forcedRefusals[GpSockCommand.PlaybackGetFileList] = NakCode.GetFileListFail

        camera.controller.refreshLibrary()!!.join()

        assertEquals("The camera said no: it could not read the file list.", camera.state.notice?.text)
        assertEquals(CameraMode.Record, camera.control.mode)
        assertFalse(camera.state.library.reading)
    }

    @Test
    fun `the card is not read while the camera records`() = runTest {
        val camera = cameraWithCard()
        camera.control.isRecording = true
        camera.until { it.isRecording }

        camera.controller.refreshLibrary()!!.join()

        assertEquals("Stop recording to look at the card.", camera.state.notice?.text)
        assertEquals(null, camera.state.library.files)
    }

    @Test
    fun `files are copied whole, recognised from their first bytes, and published`() = runTest {
        val camera = cameraWithCard()
        val files = camera.listed()
        val sink = FakeSink()

        camera.controller.download(files, sink)!!.join()

        assertEquals(listOf(MediaKind.Jpeg, MediaKind.Avi), sink.created.map { it.second })
        assertContentEquals(photo, sink.published.getValue("Gallery/PICT0001.jpg"))
        assertContentEquals(video, sink.published.getValue("Gallery/MOVI0002.avi"))
        assertEquals(Transfer.Saved("Gallery/MOVI0002.avi"), camera.state.library.transfers[files[1]])
        assertTrue(camera.seen.any { it.task == Task.Copying })
        assertEquals(CameraMode.Record, camera.control.mode)
    }

    @Test
    fun `progress is reported as a file arrives`() = runTest {
        val camera = cameraWithCard()
        val files = camera.listed()

        camera.controller.download(files.take(1), FakeSink())!!.join()

        val progress = camera.seen.mapNotNull {
            it.library.transfers[files[0]] as? Transfer.Copying
        }.map { it.copied }.distinct()
        assertEquals(listOf(0L, 1000L, 2000L, 3000L), progress)
    }

    @Test
    fun `a file the camera refuses is marked and the next is still copied`() = runTest {
        val camera = cameraWithCard()
        val files = camera.listed()
        val missing = CameraFile('J', 99, taken, 1)
        val sink = FakeSink()

        camera.controller.download(listOf(missing, files[0]), sink)!!.join()

        assertEquals(
            Transfer.Failed("The camera said no: it does not know that command."),
            camera.state.library.transfers[missing],
        )
        assertEquals(Transfer.Saved("Gallery/PICT0001.jpg"), camera.state.library.transfers[files[0]])
    }

    @Test
    fun `an empty file is reported and nothing is saved for it`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.addFile('J', taken, ByteArray(0))
        camera.connected()
        val files = camera.listed()
        val sink = FakeSink()

        camera.controller.download(files, sink)!!.join()

        assertEquals(
            Transfer.Failed("The camera sent nothing for this file."),
            camera.state.library.transfers[files[0]],
        )
        assertTrue(sink.created.isEmpty())
    }

    @Test
    fun `a full phone fails the file without being taken for a lost camera`() = runTest {
        val camera = cameraWithCard()
        val files = camera.listed()
        val sink = FakeSink().apply { full = true }

        camera.controller.download(files.take(1), sink)!!.join()

        assertEquals(
            Transfer.Failed("The phone could not save it: No space left on the device."),
            camera.state.library.transfers[files[0]],
        )
        assertEquals(Connection.Connected, camera.state.connection)
    }

    @Test
    fun `a phone that fills up part-way throws the partial file away`() = runTest {
        val camera = cameraWithCard()
        val files = camera.listed()
        val sink = FakeSink().apply {
            failsAfterBytes = 1500
            // Throwing it away fails as well, which must not hide why the copy failed.
            discardFails = true
        }

        camera.controller.download(files.take(1), sink)!!.join()

        assertEquals(listOf(files[0]), sink.discarded)
        assertEquals(
            Transfer.Failed("The phone could not save it: No space left on the device."),
            camera.state.library.transfers[files[0]],
        )
        assertTrue(sink.published.isEmpty())
        // The camera wound the transfer down, so the channel is still in step.
        camera.controller.takePhoto()!!.join()
        assertEquals(1, camera.control.picturesTaken)
    }

    @Test
    fun `cancelling a copy throws the partial file away and marks what was left`() = runTest {
        val camera = cameraWithCard()
        val files = camera.listed()
        camera.control.downloadStallsAfterBytes = 2000
        val sink = FakeSink()

        val copying = camera.controller.download(files, sink)!!
        camera.until { (it.library.transfers[files[0]] as? Transfer.Copying)?.copied == 2000L }
        copying.cancel()
        copying.join()

        assertEquals(listOf(files[0]), sink.discarded)
        val stopped = Transfer.Failed("Not copied: the copy was stopped.")
        assertEquals(stopped, camera.state.library.transfers[files[0]])
        assertEquals(stopped, camera.state.library.transfers[files[1]])
        assertEquals(Connection.Connected, camera.state.connection)
        assertEquals(null, camera.state.task)
    }

    @Test
    fun `a copy that stalls is given up and the camera treated as lost`() = runTest {
        val camera = cameraWithCard()
        val files = camera.listed()
        camera.control.downloadStallsAfterBytes = 2000
        val sink = FakeSink()

        camera.controller.download(files.take(1), sink)!!.join()

        val reconnecting = camera.until {
            it.connection is Connection.Reconnecting
        }.connection as Connection.Reconnecting
        assertEquals("The camera stopped sending for 15 seconds.", reconnecting.problem.detail)
        assertEquals(listOf(files[0]), sink.discarded)
    }

    @Test
    fun `copying is refused while the camera records`() = runTest {
        val camera = cameraWithCard()
        val files = camera.listed()
        camera.control.isRecording = true
        camera.until { it.isRecording }

        camera.controller.download(files, FakeSink())!!.join()

        assertEquals("Stop recording to copy from the card.", camera.state.notice?.text)
        assertTrue(camera.state.library.transfers.isEmpty())
    }

    @Test
    fun `deleting removes the files and reads the card again`() = runTest {
        val camera = cameraWithCard()
        camera.control.addFile('J', taken.plusMinutes(2), photo)
        val files = camera.listed()

        camera.controller.delete(listOf(files[0], files[2]))!!.join()

        assertEquals(listOf(2), camera.state.library.files!!.map { it.index })
        assertEquals(listOf(2), camera.control.files.map { it.index })
        assertEquals("2 files deleted from the card.", camera.state.notice?.text)
        assertTrue(camera.seen.any { it.task == Task.Deleting })
    }

    @Test
    fun `deleting one file says so in the singular`() = runTest {
        val camera = cameraWithCard()
        val files = camera.listed()

        camera.controller.delete(files.take(1))!!.join()

        assertEquals("Deleted from the card.", camera.state.notice?.text)
    }

    @Test
    fun `a camera that cannot delete says why`() = runTest {
        val camera = cameraWithCard()
        camera.control.supportsDelete = false
        val files = camera.listed()

        camera.controller.delete(files.take(1))!!.join()

        assertTrue(camera.state.notice!!.text.startsWith("The camera said no: it is busy"))
        assertEquals(2, camera.control.files.size)
    }

    @Test
    fun `deleting is refused while the camera records`() = runTest {
        val camera = cameraWithCard()
        val files = camera.listed()
        camera.control.isRecording = true
        camera.until { it.isRecording }

        camera.controller.delete(files)!!.join()

        assertEquals("Stop recording to delete from the card.", camera.state.notice?.text)
        assertEquals(2, camera.control.files.size)
    }

    @Test
    fun `files safely copied can be deleted from the card, and only those`() = runTest {
        val camera = cameraWithCard()
        val files = camera.listed()
        val missing = CameraFile('J', 99, taken, 1)

        camera.controller.download(listOf(files[0], missing), FakeSink(), deleteAfter = true)!!.join()

        assertEquals(listOf(2), camera.control.files.map { it.index })
        assertEquals(listOf(2), camera.state.library.files!!.map { it.index })
    }

    @Test
    fun `when nothing copied, nothing is deleted`() = runTest {
        val camera = cameraWithCard()
        camera.listed()
        camera.control.refuseDownloadWith = NakCode.FullStorage

        camera.controller.download(camera.state.library.files!!, FakeSink(), deleteAfter = true)!!.join()

        assertEquals(2, camera.control.files.size)
    }
}
