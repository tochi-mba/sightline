package com.rextechnologies.sightline.core.camera

import com.rextechnologies.sightline.core.playback.CardClip
import com.rextechnologies.sightline.core.playback.ClipArrival
import com.rextechnologies.sightline.core.playback.ClipCache
import com.rextechnologies.sightline.protocol.FakeClip
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Rule
import org.junit.rules.TemporaryFolder
import java.io.File
import java.time.LocalDateTime
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/** Playing a video straight off the card: fetched, read as it arrives, and kept for next time. */
class PlaybackTest {
    @get:Rule
    val temp = TemporaryFolder()

    private val taken = LocalDateTime.of(2026, 10, 7, 9, 30, 0)
    private val clip = FakeClip.reference(List(4) { FakeRtspCamera.jpeg(1500) }, List(2) { ByteArray(16_000) })
        .copy(picturesPerSound = 2)
        .build()

    private fun TestScope.cache(folder: File = File(temp.root, "clips")) =
        ClipCache(folder, io = StandardTestDispatcher(testScheduler))

    /** A camera with [video] on its card, connected and its card read. */
    private suspend fun TestScope.cameraWith(video: ByteArray): ControllerHarness {
        val camera = ControllerHarness(this)
        camera.control.addFile('A', taken, video)
        camera.control.downloadChunk = 1000
        camera.connected()
        camera.controller.refreshLibrary()!!.join()
        return camera
    }

    private val ControllerHarness.video: CameraFile get() = state.library.files!!.single()

    private suspend fun ControllerHarness.free() = until { it.task == null }

    @Test
    fun `a clip is read as it arrives kept whole and the camera put back`() = runTest {
        val camera = cameraWith(clip)
        camera.controller.holdLive("screen")
        camera.until { it.live is LiveView.Playing }
        val cache = cache()

        val playing = camera.controller.play(camera.video, cache)!!

        assertEquals(ClipArrival.Kept(cache.pathFor(camera.video)), playing.arrival.await())
        assertTrue(playing.reader.isComplete)
        assertEquals(4, playing.reader.clip!!.totalFrames)
        playing.openRead().use { assertEquals(clip.size.toLong(), it.length()) }
        assertContentEquals(clip, cache.pathFor(camera.video).readBytes())
        camera.free()
        assertTrue(camera.seen.any { it.task == Task.Playing })
        assertTrue(camera.seen.any { it.live is LiveView.Paused })
        assertEquals(CameraMode.Record, camera.control.mode)
        assertEquals(listOf(cache.pathFor(camera.video)), cache.folder.listFiles()!!.toList())
    }

    @Test
    fun `a clip kept from before plays without the camera`() = runTest {
        val camera = cameraWith(clip)
        val cache = cache()
        val video = camera.video
        camera.controller.play(video, cache)!!.arrival.await()
        camera.free()
        camera.controller.disconnect().join()

        val again = camera.controller.play(video, cache)!!

        assertEquals(ClipArrival.Kept(cache.pathFor(video)), again.arrival.await())
        assertTrue(again.reader.isComplete)
    }

    @Test
    fun `without the camera nothing new plays and while it is busy nothing else starts`() = runTest {
        val away = ControllerHarness(this)
        assertNull(away.controller.play(CameraFile('A', 1, taken, 1), cache()))
        assertEquals("Connect to the camera first.", away.state.notice?.text)

        val camera = cameraWith(clip)
        camera.control.downloadStallsAfterBytes = 2000
        val first = camera.controller.play(camera.video, cache())!!
        assertEquals(Task.Playing, camera.state.task)

        assertNull(camera.controller.play(camera.video, cache()))
        first.close()
        assertEquals(ClipArrival.Stopped, first.arrival.await())
    }

    @Test
    fun `letting a clip go part-way stops the fetch and leaves the camera ready`() = runTest {
        val camera = cameraWith(clip)
        camera.control.downloadStallsAfterBytes = 2000
        val cache = cache()
        val playing = camera.controller.play(camera.video, cache)!!
        runCurrent()
        assertEquals(2000L, playing.reader.bytesRead)
        assertTrue(playing.hasBytes)

        playing.close()

        assertEquals(ClipArrival.Stopped, playing.arrival.await())
        camera.free()
        assertTrue(camera.state.isConnected)
        assertEquals(CameraMode.Record, camera.control.mode)
        assertTrue(cache.folder.listFiles()!!.isEmpty())

        // The camera was told to stop sending, so the channel is still in step for what comes next.
        camera.controller.refreshLibrary()!!.join()
        assertEquals(1, camera.state.library.files!!.size)
        assertTrue(camera.state.isConnected)
    }

    @Test
    fun `a file that is no clip is not played and the camera is fine`() = runTest {
        val camera = cameraWith(FakeRtspCamera.jpeg(3000))
        val cache = cache()

        val playing = camera.controller.play(camera.video, cache)!!

        assertEquals(
            ClipArrival.Failed("This file cannot be played. This is not an AVI file."),
            playing.arrival.await(),
        )
        camera.free()
        assertTrue(camera.state.isConnected)
        assertTrue(cache.folder.listFiles()!!.isEmpty())
    }

    @Test
    fun `a refusal ends the clip with the cameras reason for its player to show`() = runTest {
        val camera = cameraWith(clip)

        val playing = camera.controller.play(CameraFile('A', 99, taken, 1), cache())!!

        assertEquals(ClipArrival.Failed("The camera said no: it does not know that command."), playing.arrival.await())
        camera.free()
        assertTrue(camera.state.isConnected)
        assertNull(camera.state.notice)
    }

    @Test
    fun `nothing plays from the card while the camera records`() = runTest {
        val camera = cameraWith(clip)
        camera.controller.toggleRecording()!!.join()
        val cache = cache()

        val playing = camera.controller.play(camera.video, cache)!!

        assertEquals(ClipArrival.Failed("Stop recording to play a clip from the card."), playing.arrival.await())
        assertTrue(camera.control.isRecording)
        assertFalse(cache.pathFor(camera.video).exists())
    }

    @Test
    fun `a clip that stops coming ends and the camera is treated as lost`() = runTest {
        val camera = cameraWith(clip)
        camera.control.downloadStallsAfterBytes = 2000
        val cache = cache()

        val playing = camera.controller.play(camera.video, cache)!!

        assertEquals(ClipArrival.Failed("The camera stopped sending for 15 seconds."), playing.arrival.await())
        camera.until { it.connection is Connection.Reconnecting }
        assertTrue(cache.folder.listFiles()!!.isEmpty())
    }

    @Test
    fun `a phone that cannot keep the clip is no reason to think the camera lost`() = runTest {
        val camera = cameraWith(clip)
        val blocked = File(temp.root, "not-a-folder").also { it.writeBytes(byteArrayOf(1)) }

        val playing = camera.controller.play(camera.video, cache(blocked))!!

        val failed = assertIs<ClipArrival.Failed>(playing.arrival.await())
        assertTrue(failed.reason.startsWith("The phone could not keep the clip: "), failed.reason)
        camera.free()
        assertTrue(camera.state.isConnected)
    }

    @Test
    fun `a camera that closes its channel part-way ends the clip and is treated as lost`() = runTest {
        val camera = cameraWith(clip)
        camera.control.answerDelay = 1.seconds
        val cache = cache()
        val playing = camera.controller.play(camera.video, cache)!!
        while (playing.reader.bytesRead < 2000) {
            advanceTimeBy(100.milliseconds)
        }

        camera.control.close()

        assertEquals(ClipArrival.Failed("The camera was lost before the whole clip arrived."), playing.arrival.await())
        assertTrue(cache.folder.listFiles()!!.isEmpty())
    }

    @Test
    fun `a camera lost part-way ends the clip`() = runTest {
        val camera = cameraWith(clip)
        camera.control.downloadStallsAfterBytes = 2000
        val cache = cache()
        val playing: CardClip = camera.controller.play(camera.video, cache)!!
        runCurrent()
        assertNotNull(playing.reader.clip)

        camera.link.lease.lose()

        assertEquals(ClipArrival.Failed("The camera was lost before the whole clip arrived."), playing.arrival.await())
        assertTrue(cache.folder.listFiles()!!.isEmpty())
    }
}
