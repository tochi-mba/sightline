package com.rextechnologies.sightline.core.session

import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.FakeCamera
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import com.rextechnologies.sightline.protocol.gpsock.GpSockConnection
import com.rextechnologies.sightline.protocol.rtp.RtspException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import java.io.IOException
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/**
 * One conversation with one camera, against fakes of both its control channel and its stream.
 *
 * The same cases the .NET session is held to, on virtual time: the real eight-second stall and ten-second
 * start are what is asserted, and the suite still runs in milliseconds. The session may open one stream
 * connection, as the reference camera answers only one per power-on; each test checks it opened no more.
 */
class CameraSessionTest {
    private val control = FakeCamera()
    private val stream = FakeRtspCamera().apply { streamStarted = { control.isStreaming } }
    private val trace = mutableListOf<String>()
    private var streamsOpened = 0

    /** What the stream port is; the fake camera's stream unless a test says otherwise. */
    private var streamPort: CameraTransport = stream

    private fun transport(port: Int): CameraTransport = if (port == GpSockConnection.PORT) {
        control
    } else {
        streamsOpened++
        streamPort
    }

    private suspend fun TestScope.open(timing: CameraSessionTiming = CameraSessionTiming.Default): CameraSession {
        val session = CameraSession.open(
            ::transport,
            backgroundScope,
            timing = timing,
            timeSource = testScheduler.timeSource,
        )
        session.trace = trace::add
        return session
    }

    private fun frames(count: Int) = List(count) { FakeRtspCamera.jpeg(100 + it) }

    @Test
    fun `a picture is grabbed after starting the stream on the control channel`() = runTest {
        val jpeg = FakeRtspCamera.jpeg(900)
        stream.frames += jpeg
        val session = open()

        val frame = session.grabFrame(5.seconds)

        assertContentEquals(jpeg, frame.jpeg)
        assertEquals(640, frame.width)
        assertTrue(control.isStreaming)
        assertEquals(listOf("DESCRIBE", "SETUP", "PLAY"), stream.verbs)
        assertTrue("control: RestartStreaming acknowledged" in trace)
        assertTrue(trace.any { it.startsWith("rtsp: first stream bytes arrived: 80") })
    }

    @Test
    fun `the stream is opened once and kept however many times pictures are wanted`() = runTest {
        stream.pace = 30.milliseconds
        stream.frames += frames(20)
        val session = open()
        assertFalse(session.holdsLivePicture)

        session.grabFrame(5.seconds)
        session.frames().first()
        session.grabFrame(5.seconds)

        assertEquals(1, streamsOpened)
        assertEquals(listOf("DESCRIBE", "SETUP", "PLAY"), stream.verbs)
        assertTrue(stream.isConnected)
        assertTrue(session.holdsLivePicture)
    }

    @Test
    fun `two watchers at once share the one stream`() = runTest {
        stream.pace = 30.milliseconds
        stream.frames += frames(30)
        val session = open()

        val watched = List(2) { async { session.frames().take(3).toList().size } }.awaitAll()

        assertEquals(listOf(3, 3), watched)
        assertEquals(1, streamsOpened)
    }

    @Test
    fun `someone who comes back to the picture sees the latest one at once`() = runTest {
        // Every picture arrives before anybody is watching again, and the camera then goes quiet.
        val last = FakeRtspCamera.jpeg(333)
        stream.frames += listOf(FakeRtspCamera.jpeg(111), FakeRtspCamera.jpeg(222), last)
        val session = open()
        session.grabFrame(5.seconds)
        runCurrent()

        assertContentEquals(last, session.grabFrame(5.seconds).jpeg)
    }

    @Test
    fun `a picture more than a second old is not shown to someone who comes back`() = runTest {
        stream.frames += FakeRtspCamera.jpeg(111)
        val session = open()
        session.grabFrame(5.seconds)
        delay(2.seconds)

        val waited = assertFailsWith<CameraTimeoutException> { session.grabFrame(5.seconds) }

        assertEquals("No whole picture arrived within 5 seconds.", waited.message)
    }

    @Test
    fun `cancelling a watcher stops its pictures and leaves the stream open`() = runTest {
        stream.pace = 30.milliseconds
        stream.frames += frames(10)
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
        assertTrue(stream.isConnected)
        assertTrue(session.holdsLivePicture)
    }

    @Test
    fun `a frame that is not a whole jpeg is skipped and said so`() = runTest {
        val good = FakeRtspCamera.jpeg(400)
        stream.frames += listOf(byteArrayOf(1, 2, 3, 4, 5, 6), good)
        val session = open()

        assertContentEquals(good, session.grabFrame(5.seconds).jpeg)
        assertTrue(trace.any { "was not a whole JPEG and was skipped" in it })
    }

    @Test
    fun `a camera that ends its stream has given its picture for this power on`() = runTest {
        stream.pace = 100.milliseconds
        stream.frames += FakeRtspCamera.jpeg(500)
        val session = open()
        session.frames().first()

        // Browse mode, on the real camera.
        stream.hangUp()
        val gone = assertFailsWith<LivePictureUnavailableException> { session.frames().collect() }

        assertEquals("The camera ended its live picture. ${LivePictureUnavailableException.ADVICE}", gone.message)
        assertTrue(trace.any { it.startsWith("rtsp: the camera closed the stream after") })
        assertFalse(session.holdsLivePicture)
        val again = assertFailsWith<LivePictureUnavailableException> { session.grabFrame(5.seconds) }
        assertEquals(gone.message, again.message)
        assertEquals(1, streamsOpened)
        assertTrue(stream.wasClosed)
    }

    @Test
    fun `a watcher waiting when the camera hangs up is told at once`() = runTest {
        stream.streamStarted = { false }
        val session = open()
        val waiting = async { runCatching { session.grabFrame(30.seconds) } }
        runCurrent()

        stream.hangUp()

        assertIs<LivePictureUnavailableException>(waiting.await().exceptionOrNull())
    }

    @Test
    fun `a stream that drops is reported with why`() = runTest {
        stream.breaksWith = IOException("An existing connection was forcibly closed.")
        val session = open()

        val lost = assertFailsWith<LivePictureUnavailableException> { session.grabFrame(5.seconds) }

        assertEquals(
            "The live picture was lost: An existing connection was forcibly closed. ${LivePictureUnavailableException.ADVICE}",
            lost.message,
        )
    }

    @Test
    fun `a quiet spell is said and the same stream is watched again`() = runTest {
        // Negotiated, PLAY answered, and nothing arrives: the stream stays open, and is waited on again.
        stream.streamStarted = { false }
        val session = open()

        val quiet = assertFailsWith<CameraTimeoutException> { session.frames().collect() }
        assertFailsWith<CameraTimeoutException> { session.frames().collect() }

        assertEquals("The camera sent nothing for 8 seconds.", quiet.message)
        assertTrue(session.holdsLivePicture)
        assertEquals(1, streamsOpened)
        assertEquals(listOf("DESCRIBE", "SETUP", "PLAY"), stream.verbs)
    }

    @Test
    fun `grabbing from a quiet stream times out with a sentence`() = runTest {
        stream.streamStarted = { false }
        val session = open()

        val timedOut = assertFailsWith<CameraTimeoutException> { session.grabFrame(5.seconds) }

        assertEquals("No whole picture arrived within 5 seconds.", timedOut.message)
    }

    @Test
    fun `a quiet spell longer than the grab is still called the grab running out`() = runTest {
        stream.streamStarted = { false }
        val session = open()

        val timedOut = assertFailsWith<CameraTimeoutException> { session.grabFrame(30.seconds) }

        assertEquals("No whole picture arrived within 30 seconds.", timedOut.message)
    }

    @Test
    fun `a caller who gives up while the stream starts is cancelled rather than told it failed`() = runTest {
        stream.neverAnswers = "DESCRIBE"
        val session = open()

        val grabbing = async { session.grabFrame(30.seconds) }
        delay(1.seconds)
        grabbing.cancel()
        grabbing.join()

        assertTrue(grabbing.isCancelled)
    }

    @Test
    fun `a camera that never answers the start has given its picture elsewhere and is not asked again`() = runTest {
        // What the reference camera does with any connection after its first since power-on.
        stream.neverAnswers = "DESCRIBE"
        val session = open()

        val stuck = assertFailsWith<LivePictureUnavailableException> { session.grabFrame(30.seconds) }

        assertEquals(
            "The camera did not start its live picture within 10 seconds, which is what it does once it has " +
                "given its live picture to an earlier connection. ${LivePictureUnavailableException.ADVICE}",
            stuck.message,
        )
        assertFailsWith<LivePictureUnavailableException> { session.grabFrame(30.seconds) }
        assertEquals(listOf("DESCRIBE"), stream.verbs)
        assertTrue(stream.wasClosed)
    }

    @Test
    fun `a refused setup says which step`() = runTest {
        stream.setupStatus = 454
        val session = open()

        val refused = assertFailsWith<LivePictureUnavailableException> { session.grabFrame(5.seconds) }

        assertEquals(
            "The camera refused the video track (454). ${LivePictureUnavailableException.ADVICE}",
            refused.message,
        )
        // Crossing coroutines can wrap it in a copy of itself, so the refusal is looked for down the chain.
        assertTrue(generateSequence<Throwable>(refused) { it.cause }.any { it is RtspException })
    }

    @Test
    fun `a refused play says which step`() = runTest {
        stream.playStatus = 454
        val session = open()

        val refused = assertFailsWith<LivePictureUnavailableException> { session.grabFrame(5.seconds) }

        assertEquals(
            "The camera would not start the stream (454). ${LivePictureUnavailableException.ADVICE}",
            refused.message,
        )
    }

    @Test
    fun `a stream port that fails with nothing to say is still explained`() = runTest {
        streamPort = StubbornTransport { throw IOException() }
        val session = open()

        val failed = assertFailsWith<LivePictureUnavailableException> { session.grabFrame(5.seconds) }

        assertEquals("The live picture could not be started. ${LivePictureUnavailableException.ADVICE}", failed.message)
    }

    @Test
    fun `everybody waiting when the session ends is told the same`() = runTest {
        stream.neverAnswers = "DESCRIBE"
        val session = open(CameraSessionTiming.Default.copy(start = 60.seconds))
        val waiting = List(2) { async { runCatching { session.grabFrame(60.seconds) }.exceptionOrNull()?.message } }
        runCurrent()

        session.close()

        val told = waiting.awaitAll()
        assertEquals(told[0], told[1])
        assertEquals(
            "The session ended before the live picture started. ${LivePictureUnavailableException.ADVICE}",
            told[0],
        )
    }

    @Test
    fun `noise that is not rtp at all is reported once it passes 64 kilobytes`() = runTest {
        stream.noiseAfterPlay = ByteArray(70_000)
        val session = open()

        assertFailsWith<CameraTimeoutException> { session.grabFrame(5.seconds) }

        assertTrue("rtsp: 70000 bytes arrived but none parsed as an RTP packet" in trace)
    }

    @Test
    fun `the stream works with nobody tracing it`() = runTest {
        stream.noiseAfterPlay = ByteArray(70_000)
        stream.frames += listOf(byteArrayOf(1, 2, 3, 4, 5, 6), FakeRtspCamera.jpeg(64))
        stream.closesAfterFrames = true
        stream.pace = 50.milliseconds
        val session = open()
        session.trace = null

        assertEquals(64, session.grabFrame(5.seconds).jpeg.size)
        assertFailsWith<LivePictureUnavailableException> { session.frames().collect() }
    }

    @Test
    fun `ending the session closes the stream and then the control channel`() = runTest {
        stream.frames += FakeRtspCamera.jpeg(500)
        val session = open()
        session.grabFrame(5.seconds)

        session.close()

        assertTrue(stream.wasClosed)
        assertTrue(control.wasDisposed)
    }

    @Test
    fun `ending the session while the stream is starting ends the start too`() = runTest {
        stream.neverAnswers = "DESCRIBE"
        val session = open(CameraSessionTiming.Default.copy(start = 60.seconds))
        val grabbing = async { runCatching { session.grabFrame(60.seconds) } }
        runCurrent()

        session.close()

        val ended = grabbing.await().exceptionOrNull()
        assertIs<LivePictureUnavailableException>(ended)
        assertEquals(
            "The session ended before the live picture started. ${LivePictureUnavailableException.ADVICE}",
            ended.message,
        )
        assertTrue(stream.wasClosed)
        assertTrue(control.wasDisposed)
    }

    @Test
    fun `ending a session that never streamed closes only the control channel`() = runTest {
        val session = open()

        session.close()

        assertTrue(control.wasDisposed)
        assertEquals(0, streamsOpened)
    }

    @Test
    fun `a session opened without timings gets the real ones`() = runTest {
        val session = CameraSession.open({ control }, backgroundScope)

        assertEquals(16, session.control.getStatus().length)
        assertEquals(CameraSessionTiming(10.seconds, 10.seconds, 8.seconds), CameraSessionTiming.Default)
    }

    @Test
    fun `a control channel that cannot open is closed again and reported`() = runTest {
        val refusing = StubbornTransport { throw IOException("Connection refused") }

        assertFailsWith<IOException> { CameraSession.open({ refusing }, backgroundScope) }

        assertTrue(refusing.closed)
    }

    @Test
    fun `a control port that never answers is given up on and closed`() = runTest {
        // What a camera whose access point is half asleep does: the connect neither succeeds nor fails.
        val silent = StubbornTransport { awaitCancellation() }

        val gaveUp = assertFailsWith<CameraTimeoutException> { CameraSession.open({ silent }, backgroundScope) }

        assertEquals("The camera did not answer on its control port within 10 seconds.", gaveUp.message)
        assertTrue(silent.closed)
    }

    @Test
    fun `a blank host is refused`() = runTest {
        assertFailsWith<IllegalArgumentException> { CameraSession.open({ control }, backgroundScope, host = " ") }
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
