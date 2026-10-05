package com.rextechnologies.sightline.core.session

import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.FakeCamera
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import com.rextechnologies.sightline.protocol.gpsock.GpSockConnection
import com.rextechnologies.sightline.protocol.rtp.RtspException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import java.io.IOException
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds

/**
 * One conversation with one camera, against fakes of both its control channel and its stream.
 *
 * The same cases the .NET session is held to, on virtual time: the real eight-second stall and
 * ten-second start are what is asserted, and the suite still runs in milliseconds.
 */
class CameraSessionTest {
    private val control = FakeCamera()
    private val streams = mutableListOf<FakeRtspCamera>()
    private val trace = mutableListOf<String>()

    /** The next RTSP connection the session opens; each stream gets a fresh one, as on the wire. */
    private fun nextStream(configure: FakeRtspCamera.() -> Unit = {}): FakeRtspCamera =
        FakeRtspCamera().apply {
            streamStarted = { control.isStreaming }
            configure()
            streams += this
        }

    private suspend fun open(): CameraSession {
        var next = 0
        val session = CameraSession.open({ port -> if (port == GpSockConnection.PORT) control else streams[next++] })
        session.trace = trace::add
        return session
    }

    @Test
    fun `a picture is grabbed after starting the stream on the control channel`() = runTest {
        val jpeg = FakeRtspCamera.jpeg(900)
        val stream = nextStream { frames += jpeg }
        val session = open()

        val frame = session.grabFrame(5.seconds)

        assertContentEquals(jpeg, frame.jpeg)
        assertEquals(640, frame.width)
        assertTrue(control.isStreaming)
        assertEquals(listOf("DESCRIBE", "SETUP", "PLAY", "TEARDOWN"), stream.verbs)
        assertTrue(stream.wasClosed)
        assertTrue("control: RestartStreaming acknowledged" in trace)
        assertTrue(trace.any { it.startsWith("rtsp: first stream bytes arrived: 80") })
    }

    @Test
    fun `frames keep coming until the collector stops and the stream is then torn down`() = runTest {
        val stream = nextStream {
            frames +=
                listOf(FakeRtspCamera.jpeg(100), FakeRtspCamera.jpeg(200), FakeRtspCamera.jpeg(300))
        }
        val session = open()

        val sizes = session.frames().take(2).toList().map { it.jpeg.size }

        assertEquals(listOf(100, 200), sizes)
        assertEquals("TEARDOWN", stream.verbs.last())
    }

    @Test
    fun `cancelling the collector between frames ends the stream quietly and still tears it down`() = runTest {
        val stream = nextStream { frames += listOf(FakeRtspCamera.jpeg(100), FakeRtspCamera.jpeg(200)) }
        val session = open()
        var count = 0

        val watching = launch {
            session.frames().collect {
                count++
                cancel()
            }
        }
        watching.join()

        assertTrue(watching.isCancelled)
        assertEquals(1, count)
        assertEquals("TEARDOWN", stream.verbs.last())
    }

    @Test
    fun `a frame that is not a whole jpeg is skipped and said so`() = runTest {
        val good = FakeRtspCamera.jpeg(400)
        nextStream { frames += listOf(byteArrayOf(1, 2, 3, 4, 5, 6), good) }
        val session = open()

        assertContentEquals(good, session.grabFrame(5.seconds).jpeg)
        assertTrue(trace.any { "was not a whole JPEG and was skipped" in it })
    }

    @Test
    fun `a camera that closes the stream before any picture is reported`() = runTest {
        nextStream { closesAfterFrames = true }
        val session = open()

        val failed = assertFailsWith<RtspException> { session.grabFrame(5.seconds) }

        assertEquals("The camera stopped sending before a whole picture arrived.", failed.message)
        assertTrue(trace.any { it.startsWith("rtsp: the camera closed the stream after") })
    }

    @Test
    fun `a stream that goes quiet is called stopped rather than waited on`() = runTest {
        // Negotiated, PLAY answered, and nothing ever arrives: the commonest broken-camera state.
        val stream = nextStream { streamStarted = { false } }
        val session = open()

        val stalled = assertFailsWith<CameraTimeoutException> { session.frames().collect() }

        assertEquals("The camera sent nothing for 8 seconds.", stalled.message)
        assertEquals("TEARDOWN", stream.verbs.last())
    }

    @Test
    fun `grabbing from a quiet stream times out with a sentence`() = runTest {
        nextStream { streamStarted = { false } }
        val session = open()

        val timedOut = assertFailsWith<CameraTimeoutException> { session.grabFrame(5.seconds) }

        assertEquals("No whole picture arrived within 5 seconds.", timedOut.message)
    }

    @Test
    fun `grabbing stops when the caller gives up rather than calling it a timeout`() = runTest {
        val stream = nextStream { streamStarted = { false } }
        val session = open()

        val grabbing = async { session.grabFrame(30.seconds) }
        delay(1.seconds)
        grabbing.cancel()
        grabbing.join()

        assertTrue(grabbing.isCancelled)
        assertEquals("TEARDOWN", stream.verbs.last())
    }

    @Test
    fun `a server that never answers the start is called stuck and left without a teardown`() = runTest {
        // SETUP never came back, so there is no session on the camera to end.
        val stream = nextStream { neverAnswers = "DESCRIBE" }
        val session = open()

        val stuck = assertFailsWith<CameraTimeoutException> { session.grabFrame(30.seconds) }

        assertEquals("The camera did not start its stream within 10 seconds.", stuck.message)
        assertEquals(listOf("DESCRIBE"), stream.verbs)
    }

    @Test
    fun `a refused setup says so and leaves nothing to end`() = runTest {
        val stream = nextStream { setupStatus = 454 }
        val session = open()

        val refused = assertFailsWith<RtspException> { session.grabFrame(5.seconds) }

        assertEquals("The camera refused the video track (454).", refused.message)
        assertFalse("TEARDOWN" in stream.verbs)
    }

    @Test
    fun `a refused play says so and still ends the session that setup made`() = runTest {
        val stream = nextStream { playStatus = 454 }
        val session = open()

        val refused = assertFailsWith<RtspException> { session.grabFrame(5.seconds) }

        assertEquals("The camera would not start the stream (454).", refused.message)
        assertEquals("TEARDOWN", stream.verbs.last())
    }

    @Test
    fun `a teardown that fails does not hide the picture that did arrive`() = runTest {
        nextStream {
            frames += FakeRtspCamera.jpeg(500)
            teardownFails = true
        }
        val session = open()

        assertEquals(500, session.grabFrame(5.seconds).jpeg.size)
    }

    @Test
    fun `noise that is not rtp at all is reported once it passes 64 kilobytes`() = runTest {
        nextStream { noiseAfterPlay = ByteArray(70_000) }
        val session = open()

        assertFailsWith<CameraTimeoutException> { session.grabFrame(30.seconds) }

        assertTrue("rtsp: 70000 bytes arrived but none parsed as an RTP packet" in trace)
    }

    @Test
    fun `the stream works with nobody tracing it`() = runTest {
        nextStream {
            noiseAfterPlay = ByteArray(70_000)
            frames += listOf(byteArrayOf(1, 2, 3, 4, 5, 6), FakeRtspCamera.jpeg(64))
        }
        val session = open()
        session.trace = null

        assertEquals(64, session.grabFrame(5.seconds).jpeg.size)
    }

    @Test
    fun `a camera that closes the stream is handled with nobody tracing it`() = runTest {
        nextStream { closesAfterFrames = true }
        val session = open()
        session.trace = null

        assertFailsWith<RtspException> { session.grabFrame(5.seconds) }
    }

    @Test
    fun `ending the session closes the control channel`() = runTest {
        val session = open()

        session.close()

        assertTrue(control.wasDisposed)
    }

    @Test
    fun `a session opened without timings gets the real ones`() = runTest {
        val session = CameraSession.open({ control })

        assertEquals(16, session.control.getStatus().length)
        assertEquals(CameraSessionTiming(10.seconds, 10.seconds, 8.seconds, 2.seconds), CameraSessionTiming.Default)
    }

    @Test
    fun `a control channel that cannot open is closed again and reported`() = runTest {
        val refusing = StubbornTransport { throw IOException("Connection refused") }

        assertFailsWith<IOException> { CameraSession.open({ refusing }) }

        assertTrue(refusing.closed)
    }

    @Test
    fun `a control port that never answers is given up on and closed`() = runTest {
        // What a camera whose access point is half asleep does: the connect neither succeeds nor fails.
        val silent = StubbornTransport { awaitCancellation() }

        val gaveUp = assertFailsWith<CameraTimeoutException> { CameraSession.open({ silent }) }

        assertEquals("The camera did not answer on its control port within 10 seconds.", gaveUp.message)
        assertTrue(silent.closed)
    }

    @Test
    fun `a blank host is refused`() = runTest {
        assertFailsWith<IllegalArgumentException> { CameraSession.open({ control }, host = " ") }
        assertFalse(control.isConnected)
    }

    /** A transport whose connect does whatever [connecting] does, and which records being closed. */
    private class StubbornTransport(private val connecting: suspend () -> Unit) : CameraTransport {
        var closed = false

        override val isConnected = false

        override suspend fun connect() = connecting()

        override suspend fun send(bytes: ByteArray) = error("Never connected.")

        override suspend fun receive(into: ByteArray): Int = error("Never connected.")

        override fun close() {
            closed = true
        }
    }
}
