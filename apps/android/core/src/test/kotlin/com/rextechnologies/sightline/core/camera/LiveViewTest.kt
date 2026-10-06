package com.rextechnologies.sightline.core.camera

import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.session.LivePictureUnavailableException
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/**
 * The live picture: started when somebody wants it, its one stream kept for the whole connection, and
 * said to be gone, not retried, once the camera will not give it again.
 */
class LiveViewTest {
    /** Holds the picture for the screen and asks for it, as pressing "Show the live picture" does. */
    private fun ControllerHarness.watch() {
        controller.holdLive("screen")
        controller.showLivePicture()
    }

    /** A dozen pictures a second for minutes, as the real camera streams. */
    private fun ControllerHarness.streamsForMinutes() {
        stream = {
            frames += List(1500) { FakeRtspCamera.jpeg(600 + it % 7) }
            pace = 80.milliseconds
        }
    }

    @Test
    fun `the live view shows while somebody holds it and the stream stays open when nobody does`() = runTest {
        val camera = ControllerHarness(this)
        camera.streamsForMinutes()
        camera.connected()

        camera.watch()
        camera.controller.holdLive("sentry")
        assertTrue(camera.until { it.live is LiveView.Playing }.holdsLivePicture)
        val frame = assertNotNull(camera.controller.frames.value)
        camera.controller.releaseLive("screen")
        runCurrent()
        assertTrue(camera.state.live is LiveView.Playing)
        camera.controller.releaseLive("sentry")
        runCurrent()

        assertEquals(LiveView.Off, camera.state.live)
        assertEquals(640, frame.width)
        assertEquals(360, frame.height)
        // The camera answers one stream per power-on, so letting go of the picture keeps the stream.
        assertTrue(camera.streams.single().isConnected)
        assertTrue(camera.state.holdsLivePicture)
        camera.watch()
        camera.until { it.live is LiveView.Playing }
        assertEquals(1, camera.streams.size)
    }

    @Test
    fun `the picture is offered rather than started until the person asks`() = runTest {
        val camera = ControllerHarness(this)
        camera.controller.holdLive("screen")
        assertEquals(LiveView.Off, camera.state.live)

        camera.connected()
        camera.until { it.live == LiveView.Offered }
        advanceTimeBy(30.seconds)
        assertTrue(camera.streams.isEmpty())
        assertFalse(camera.state.holdsLivePicture)

        camera.controller.showLivePicture()

        assertTrue(camera.until { it.live is LiveView.Playing }.holdsLivePicture)
    }

    @Test
    fun `a holder that asks starts the picture while it holds it`() = runTest {
        val camera = ControllerHarness(this)
        camera.controller.holdLive("sentry", asking = true)

        camera.connected()
        camera.until { it.live is LiveView.Playing }
        camera.controller.releaseLive("sentry")
        camera.controller.holdLive("screen")

        assertEquals(LiveView.Offered, camera.until { it.live == LiveView.Offered }.live)
    }

    @Test
    fun `leaving a camera whose picture ran says its buttons are stuck and the offer starts over`() = runTest {
        val camera = ControllerHarness(this)
        camera.streamsForMinutes()
        camera.connected()
        camera.watch()
        camera.until { it.live is LiveView.Playing }

        camera.controller.disconnect().join()

        assertEquals(BUTTONS_STUCK, camera.state.notice?.text)
        camera.powerCycle()
        camera.connected()
        assertEquals(LiveView.Offered, camera.until { it.live == LiveView.Offered }.live)
    }

    @Test
    fun `leaving a camera whose picture never ran says nothing`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        camera.controller.holdLive("screen")
        camera.until { it.live == LiveView.Offered }

        camera.controller.disconnect().join()

        assertEquals(null, camera.state.notice)
    }

    @Test
    fun `the frame rate is measured over each second`() = runTest {
        val camera = ControllerHarness(this)
        camera.stream = {
            frames += List(40) { FakeRtspCamera.jpeg(300 + it) }
            pace = 100.milliseconds
        }
        camera.connected()

        camera.watch()
        val first = camera.until { it.live is LiveView.Playing }.live as LiveView.Playing
        val measured = camera.until {
            (it.live as? LiveView.Playing)?.framesPerSecond?.let { rate -> rate > 0 } == true
        }

        assertEquals(0.0, first.framesPerSecond)
        assertEquals(10.0, (measured.live as LiveView.Playing).framesPerSecond, 0.01)
    }

    @Test
    fun `frames count up so the same picture twice is still two frames`() = runTest {
        val camera = ControllerHarness(this)
        val picture = FakeRtspCamera.jpeg(500)
        camera.stream = {
            frames += listOf(picture, picture)
            pace = 100.milliseconds
        }
        camera.connected()

        camera.watch()
        camera.until { it.live is LiveView.Playing }
        val first = assertNotNull(camera.controller.frames.value)
        advanceTimeBy(150.milliseconds)
        val second = assertNotNull(camera.controller.frames.value)

        assertContentEquals(picture, second.jpeg)
        assertEquals(first.number + 1, second.number)
    }

    @Test
    fun `a stream that goes quiet is said and waited on without another connection`() = runTest {
        val camera = ControllerHarness(this)
        camera.stream = { streamStarted = { false } }
        camera.connected()

        camera.watch()
        val interrupted = camera.until { it.live is LiveView.Interrupted }.live as LiveView.Interrupted
        advanceTimeBy(30.seconds)

        assertEquals("The camera sent nothing for 8 seconds.", interrupted.reason)
        assertEquals(1, camera.streams.size)
        assertEquals(listOf("DESCRIBE", "SETUP", "PLAY"), camera.streams.single().verbs)
    }

    @Test
    fun `a camera that ends its stream has the picture called gone and not asked for again`() = runTest {
        val camera = ControllerHarness(this)
        camera.stream = {
            frames += FakeRtspCamera.jpeg(500)
            closesAfterFrames = true
        }
        camera.connected()

        camera.watch()
        val gone = camera.until { it.live is LiveView.Unavailable }
        camera.controller.releaseLive("screen")
        camera.watch()
        advanceTimeBy(60.seconds)

        assertEquals(
            LiveView.Unavailable("The camera ended its live picture. ${LivePictureUnavailableException.ADVICE}"),
            gone.live,
        )
        assertFalse(gone.holdsLivePicture)
        assertTrue(camera.state.live is LiveView.Unavailable)
        assertEquals(1, camera.streams.size)
    }

    @Test
    fun `a stream the camera refuses says why and is not asked for again`() = runTest {
        val camera = ControllerHarness(this)
        camera.stream = { setupStatus = 454 }
        camera.connected()

        camera.watch()
        advanceTimeBy(60.seconds)

        assertEquals(
            LiveView.Unavailable("The camera refused the video track (454). ${LivePictureUnavailableException.ADVICE}"),
            camera.state.live,
        )
        assertEquals(1, camera.streams.size)
    }

    @Test
    fun `a dropped network takes the picture with it until the camera is switched off and on`() = runTest {
        val camera = ControllerHarness(this)
        camera.streamsForMinutes()
        camera.connected()
        camera.watch()
        camera.until { it.live is LiveView.Playing }

        camera.link.lease.lose()
        camera.until { it.connection is Connection.Reconnecting }
        assertEquals(LiveView.Off, camera.state.live)
        assertTrue(camera.streams.first().wasClosed)

        // The camera is still on, so it does not answer the reconnect's stream.
        camera.until { it.isConnected }
        assertTrue(camera.until { it.live is LiveView.Unavailable }.live is LiveView.Unavailable)
        assertEquals(2, camera.streams.size)
    }

    @Test
    fun `switching the camera off and on brings the picture back with the reconnect`() = runTest {
        val camera = ControllerHarness(this)
        camera.streamsForMinutes()
        camera.connected()
        camera.watch()
        camera.until { it.live is LiveView.Playing }
        camera.streams.single().hangUp()
        camera.until { it.live is LiveView.Unavailable }

        // Switched off: the network goes. Switched on: the reconnect finds a camera with its picture to give.
        camera.powerCycle()
        camera.link.lease.lose()

        assertTrue(camera.until { it.live is LiveView.Playing && it.holdsLivePicture }.holdsLivePicture)
        assertEquals(2, camera.streams.size)
    }

    @Test
    fun `letting go of a live view nobody held changes nothing`() = runTest {
        val camera = ControllerHarness(this)
        camera.controller.connect(CameraNetwork())

        camera.controller.releaseLive("screen")

        assertEquals(LiveView.Off, camera.state.live)
    }
}
