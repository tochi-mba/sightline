package com.rextechnologies.sightline.core.camera

import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/** The live picture: started when somebody wants it, kept going through failures, stopped when not. */
class LiveViewTest {
    @Test
    fun `the live view runs while somebody holds it and stops when the last lets go`() = runTest {
        val camera = ControllerHarness(this)
        val picture = FakeRtspCamera.jpeg(900)
        camera.stream = { frames += picture }
        camera.connected()

        camera.controller.holdLive("screen")
        camera.controller.holdLive("sentry")
        camera.until { it.live is LiveView.Playing }
        val frame = assertNotNull(camera.controller.frames.value)
        camera.controller.releaseLive("screen")
        runCurrent()

        assertContentEquals(picture, frame.jpeg)
        assertEquals(640, frame.width)
        assertEquals(360, frame.height)
        assertTrue(camera.state.live is LiveView.Playing)
        camera.controller.releaseLive("sentry")
        runCurrent()
        assertEquals(LiveView.Off, camera.state.live)
        // Closing the stream's connection is what stops the camera sending.
        assertTrue(camera.streams.single().wasClosed)
    }

    @Test
    fun `holding the live view before connecting starts it once connected`() = runTest {
        val camera = ControllerHarness(this)
        camera.controller.holdLive("screen")
        assertEquals(LiveView.Off, camera.state.live)

        camera.connected()

        assertTrue(camera.until { it.live is LiveView.Playing }.live is LiveView.Playing)
    }

    @Test
    fun `the frame rate is measured over each second`() = runTest {
        val camera = ControllerHarness(this)
        camera.stream = {
            frames += List(40) { FakeRtspCamera.jpeg(300 + it) }
            pace = 100.milliseconds
        }
        camera.connected()

        camera.controller.holdLive("screen")
        val first = camera.until { it.live is LiveView.Playing }.live as LiveView.Playing
        val measured = camera.until {
            (it.live as? LiveView.Playing)?.framesPerSecond?.let { rate -> rate > 0 } == true
        }

        assertEquals(0.0, first.framesPerSecond)
        assertEquals(10.0, (measured.live as LiveView.Playing).framesPerSecond, 0.01)
    }

    @Test
    fun `frames count up across streams so the same picture twice is still two frames`() = runTest {
        val camera = ControllerHarness(this)
        camera.stream = {
            frames += FakeRtspCamera.jpeg(500)
            closesAfterFrames = true
        }
        camera.connected()

        camera.controller.holdLive("screen")
        camera.until { it.live is LiveView.Interrupted }
        val firstNumber = assertNotNull(camera.controller.frames.value).number
        advanceTimeBy(2.1.seconds)

        assertEquals(firstNumber + 1, assertNotNull(camera.controller.frames.value).number)
    }

    @Test
    fun `a stream that goes quiet is called interrupted and started again`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()

        camera.controller.holdLive("screen")
        val interrupted = camera.until { it.live is LiveView.Interrupted }.live as LiveView.Interrupted
        camera.until { it.live is LiveView.Playing }

        assertEquals("The camera sent nothing for 8 seconds.", interrupted.reason)
        assertEquals(2, camera.streams.size)
    }

    @Test
    fun `a camera that ends the stream gets it started again`() = runTest {
        val camera = ControllerHarness(this)
        camera.stream = { closesAfterFrames = true }
        camera.connected()

        camera.controller.holdLive("screen")
        val interrupted = camera.until { it.live is LiveView.Interrupted }.live as LiveView.Interrupted

        assertEquals("The camera ended the live view.", interrupted.reason)
    }

    @Test
    fun `a stream that keeps failing is retried less often, up to every ten seconds`() = runTest {
        val camera = ControllerHarness(this)
        camera.stream = { setupStatus = 454 }
        camera.connected()

        camera.controller.holdLive("screen")
        advanceTimeBy(60.seconds)

        // At 0, 2, 6, 12, 20, 30, 40 and 50 seconds: each wait two seconds longer, until it is ten.
        assertEquals(8, camera.streams.size)
        assertEquals(LiveView.Interrupted("The camera refused the video track (454)."), camera.state.live)
    }

    @Test
    fun `a stream that works again resets the wait before the next retry`() = runTest {
        val camera = ControllerHarness(this)
        var attempt = 0
        camera.stream = {
            attempt++
            // Fails twice, then streams once and closes, then fails again.
            if (attempt == 3) frames += FakeRtspCamera.jpeg(400) else setupStatus = 454
            closesAfterFrames = true
        }
        camera.connected()

        camera.controller.holdLive("screen")
        advanceTimeBy(2.seconds + 4.seconds + 2.seconds + 1.milliseconds)

        assertEquals(4, camera.streams.size)
    }

    @Test
    fun `the live view stops when the camera is lost and comes back with it`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        camera.controller.holdLive("screen")
        camera.until { it.live is LiveView.Playing }

        camera.link.lease.lose()
        camera.until { it.connection is Connection.Reconnecting }
        assertEquals(LiveView.Off, camera.state.live)
        assertTrue(camera.streams.first().wasClosed)

        camera.until { it.isConnected }
        assertTrue(camera.until { it.live is LiveView.Playing }.live is LiveView.Playing)
    }

    @Test
    fun `letting go of a live view nobody held changes nothing`() = runTest {
        val camera = ControllerHarness(this)
        camera.controller.connect(CameraNetwork())

        camera.controller.releaseLive("screen")

        assertEquals(LiveView.Off, camera.state.live)
    }
}
