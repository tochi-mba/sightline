package com.rextechnologies.sightline.core.session

import com.rextechnologies.sightline.protocol.CameraDatagrams
import com.rextechnologies.sightline.protocol.CameraSockets
import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.FakeCamera
import com.rextechnologies.sightline.protocol.FakeCameraSockets
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import com.rextechnologies.sightline.protocol.gpsock.GpSockConnection
import com.rextechnologies.sightline.protocol.rtp.RtspException
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import java.io.IOException
import java.net.SocketException
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
 * One conversation with one camera, against fakes of its control channel, its RTSP server and the datagrams
 * its pictures arrive in.
 *
 * The same cases the .NET session is held to, on virtual time: the real eight-second stall and ten-second
 * start are what is asserted, and the suite still runs in milliseconds. Every stream the session starts is a
 * new RTSP connection, handed out in the order the test prepared them.
 */
class CameraSessionTest {
    private val control = FakeCamera()
    private val prepared = ArrayDeque<FakeRtspCamera>()
    private val streams = mutableListOf<FakeRtspCamera>()
    private val trace = mutableListOf<String>()
    private val sockets = FakeCameraSockets(::transport)

    /** The next stream the camera will answer, for the test to set up before it is opened. */
    private fun next(): FakeRtspCamera = FakeRtspCamera().apply { streamStarted = { control.isStreaming } }
        .also(prepared::addLast)

    private fun transport(port: Int): CameraTransport = if (port == GpSockConnection.PORT) {
        control
    } else {
        (prepared.removeFirstOrNull() ?: FakeRtspCamera().apply { streamStarted = { control.isStreaming } })
            .also(streams::add)
    }

    private suspend fun TestScope.open(
        timing: CameraSessionTiming = CameraSessionTiming.Default,
        through: CameraSockets = sockets,
    ): CameraSession {
        val session = CameraSession.open(
            through,
            backgroundScope,
            timing = timing,
            timeSource = testScheduler.timeSource,
        )
        session.trace = trace::add
        return session
    }

    private fun pictures(count: Int) = List(count) { FakeRtspCamera.jpeg(100 + it) }

    private fun causes(failure: Throwable): Sequence<Throwable> = generateSequence(failure) { it.cause }

    @Test
    fun `a picture is grabbed after starting the stream on the control channel`() = runTest {
        val jpeg = FakeRtspCamera.jpeg(900)
        val stream = next().apply { frames += jpeg }
        val session = open()

        val frame = session.grabFrame(5.seconds)

        assertContentEquals(jpeg, frame.jpeg)
        assertEquals(640, frame.width)
        assertTrue(control.isStreaming)
        assertEquals(listOf("DESCRIBE", "SETUP", "PLAY"), stream.verbs)
        val socket = sockets.openedSockets.single()
        assertTrue("client_port=${socket.port}-${socket.port + 1}" in stream.requests[1])
        assertTrue("control: RestartStreaming acknowledged" in trace)
        assertTrue(trace.any { it.startsWith("rtp: first datagram arrived: 80") })
    }

    @Test
    fun `the stream's socket writes to the camera's port first so a firewall lets the pictures in`() = runTest {
        next().frames += FakeRtspCamera.jpeg(300)
        val session = open()

        session.grabFrame(5.seconds)

        val (port, datagram) = sockets.openedSockets.single().sent.single()
        assertEquals(FakeRtspCamera.REFERENCE_SERVER_PORT, port)
        assertContentEquals(byteArrayOf(0), datagram)
        assertTrue(trace.any { "from ${FakeRtspCamera.REFERENCE_SERVER_PORT} to " in it })
    }

    @Test
    fun `a camera that does not say where it streams from is written nothing and still streams`() = runTest {
        next().apply {
            setupTransport = "RTP/AVP;unicast"
            frames += FakeRtspCamera.jpeg(300)
        }
        val session = open()

        assertEquals(300, session.grabFrame(5.seconds).jpeg.size)

        assertTrue(sockets.openedSockets.single().sent.isEmpty())
        assertTrue(trace.any { "from a port it did not say" in it })
    }

    @Test
    fun `the stream stops once nobody is watching and the next watcher starts another`() = runTest {
        // Closing the stream's connection is what stops the real camera sending.
        next().frames += FakeRtspCamera.jpeg(300)
        next().frames += FakeRtspCamera.jpeg(400)
        val session = open()

        assertEquals(300, session.grabFrame(5.seconds).jpeg.size)
        runCurrent()
        assertTrue(streams[0].wasClosed)
        assertTrue(sockets.openedSockets[0].isClosed)
        assertEquals(400, session.grabFrame(5.seconds).jpeg.size)

        assertEquals(2, streams.size)
    }

    @Test
    fun `two watchers at once share one stream`() = runTest {
        next().apply {
            pace = 30.milliseconds
            frames += pictures(30)
        }
        val session = open()

        val watched = List(2) { async { session.frames().take(3).toList().size } }.awaitAll()

        assertEquals(listOf(3, 3), watched)
        assertEquals(1, streams.size)
    }

    @Test
    fun `a watcher who joins a running stream sees its latest picture at once`() = runTest {
        // The camera sends three pictures and goes quiet; somebody still watching keeps the stream running.
        val last = FakeRtspCamera.jpeg(333)
        next().frames += listOf(FakeRtspCamera.jpeg(111), FakeRtspCamera.jpeg(222), last)
        val session = open()
        val watching = backgroundScope.launch { session.frames().collect() }
        runCurrent()

        assertContentEquals(last, session.grabFrame(5.seconds).jpeg)

        assertEquals(1, streams.size)
        watching.cancel()
    }

    @Test
    fun `cancelling the only watcher ends its pictures quietly and stops the stream`() = runTest {
        val stream = next().apply {
            pace = 30.milliseconds
            frames += pictures(10)
        }
        val session = open()

        assertEquals(1, session.frames().take(1).toList().size)
        runCurrent()

        assertTrue(stream.wasClosed)
    }

    @Test
    fun `a picture that is not a whole jpeg is skipped and said so`() = runTest {
        val good = FakeRtspCamera.jpeg(400)
        next().frames += listOf(byteArrayOf(1, 2, 3, 4, 5, 6), good)
        val session = open()

        assertContentEquals(good, session.grabFrame(5.seconds).jpeg)
        assertTrue(trace.any { "was not a whole JPEG and was skipped" in it })
    }

    @Test
    fun `the camera ending its stream ends the pictures quietly and the next watcher starts another`() = runTest {
        // Browse mode, on the real camera: it closes the stream's connection, and answers a new one afterwards.
        val first = next().apply {
            pace = 50.milliseconds
            frames += pictures(20)
        }
        next().frames += FakeRtspCamera.jpeg(777)
        val session = open()

        session.frames().collect { first.hangUp() }

        assertTrue("rtsp: the camera closed the stream" in trace)
        assertEquals(777, session.grabFrame(5.seconds).jpeg.size)
        assertEquals(2, streams.size)
    }

    @Test
    fun `a grab waiting when the camera hangs up is told at once`() = runTest {
        val stream = next().apply { streamStarted = { false } }
        val session = open()
        val waiting = async { runCatching { session.grabFrame(30.seconds) } }
        runCurrent()

        stream.hangUp()

        val ended = assertIs<RtspException>(waiting.await().exceptionOrNull())
        assertEquals("The camera ended its stream before a whole picture arrived.", ended.message)
    }

    @Test
    fun `a stream whose connection fails ends with why`() = runTest {
        next().breaksWith = IOException("An existing connection was forcibly closed.")
        val session = open()

        val lost = assertFailsWith<RtspException> { session.grabFrame(5.seconds) }

        assertEquals("The stream was lost: An existing connection was forcibly closed.", lost.message)
        assertTrue(causes(lost).any { it is IOException })
    }

    @Test
    fun `a socket that fails ends the stream with why`() = runTest {
        val failing = FailingDatagrams()
        val session = open(through = Sockets(sockets::transport) { failing })

        val lost = assertFailsWith<RtspException> { session.grabFrame(5.seconds) }

        assertTrue(lost.message.orEmpty().startsWith("The stream was lost: "))
        assertTrue(causes(lost).any { it is SocketException })
        runCurrent()
        assertTrue(failing.closed)
    }

    @Test
    fun `a quiet camera ends the stream and watching again starts another`() = runTest {
        val quiet = next().apply { streamStarted = { false } }
        next().frames += FakeRtspCamera.jpeg(500)
        val session = open()

        val silence = assertFailsWith<CameraTimeoutException> { session.frames().collect() }

        assertEquals("The camera sent nothing for 8 seconds.", silence.message)
        assertTrue("rtp: The camera sent nothing for 8 seconds." in trace)
        runCurrent()
        assertTrue(quiet.wasClosed)
        assertEquals(500, session.grabFrame(5.seconds).jpeg.size)
        assertEquals(2, streams.size)
    }

    @Test
    fun `grabbing from a quiet stream times out with a sentence`() = runTest {
        next().streamStarted = { false }
        val session = open()

        val timedOut = assertFailsWith<CameraTimeoutException> { session.grabFrame(100.milliseconds) }

        assertEquals("No whole picture arrived within 0 seconds.", timedOut.message)
    }

    @Test
    fun `a grab that outlasts the camera going quiet says how long it waited`() = runTest {
        next().streamStarted = { false }
        val session = open()

        val timedOut = assertFailsWith<CameraTimeoutException> { session.grabFrame(30.seconds) }

        assertEquals("No whole picture arrived within 30 seconds.", timedOut.message)
    }

    @Test
    fun `a watcher who joins after the camera has gone quiet is not shown a stale picture`() = runTest {
        next().frames += FakeRtspCamera.jpeg(111)
        val session = open()
        val watching = backgroundScope.launch { session.frames().collect() }
        runCurrent()
        advanceTimeBy(2.seconds)

        assertFailsWith<CameraTimeoutException> { session.grabFrame(1.seconds) }

        watching.cancel()
    }

    @Test
    fun `grabbing stops when the caller cancels rather than calling it a timeout`() = runTest {
        next().streamStarted = { false }
        val session = open()
        val grabbing = launch { session.grabFrame(30.seconds) }
        runCurrent()

        grabbing.cancel()
        grabbing.join()

        assertTrue(grabbing.isCancelled)
    }

    @Test
    fun `a start the camera never answers times out, closes what it opened, and is tried afresh next time`() =
        runTest {
            val stuck = next().apply { neverAnswers = "DESCRIBE" }
            next().frames += FakeRtspCamera.jpeg(500)
            val session = open()

            val timedOut = assertFailsWith<CameraTimeoutException> { session.frames().collect() }

            assertEquals("The camera did not start its stream within 10 seconds.", timedOut.message)
            assertTrue(stuck.wasClosed)
            assertTrue(sockets.openedSockets[0].isClosed)
            assertEquals(500, session.grabFrame(5.seconds).jpeg.size)
        }

    @Test
    fun `a refused setup says so and closes what was opened`() = runTest {
        val stream = next().apply { setupStatus = 454 }
        val session = open()

        val refused = assertFailsWith<RtspException> { session.grabFrame(5.seconds) }

        assertEquals("The camera refused the video track (454).", refused.message)
        assertTrue(stream.wasClosed)
        assertTrue(sockets.openedSockets.single().isClosed)
        assertTrue(trace.any { "session (none)" in it })
    }

    @Test
    fun `a refused play says so and closes what was opened`() = runTest {
        val stream = next().apply { playStatus = 454 }
        val session = open()

        val refused = assertFailsWith<RtspException> { session.grabFrame(5.seconds) }

        assertEquals("The camera would not start the stream (454).", refused.message)
        assertTrue(stream.wasClosed)
    }

    @Test
    fun `a failed start reaches everyone waiting on it`() = runTest {
        next().setupStatus = 454
        val session = open()

        val failures = List(2) { async { runCatching { session.frames().collect() }.exceptionOrNull() } }.awaitAll()

        failures.forEach {
            assertEquals("The camera refused the video track (454).", assertIs<RtspException>(it).message)
        }
        assertEquals(1, streams.size)
    }

    @Test
    fun `a watcher who leaves while the stream is starting stops the start`() = runTest {
        val stuck = next().apply { neverAnswers = "DESCRIBE" }
        val session = open(CameraSessionTiming(open = 10.seconds, start = 30.seconds, stall = 8.seconds))

        assertFailsWith<CameraTimeoutException> { session.grabFrame(200.milliseconds) }
        runCurrent()

        // Long before the thirty seconds the start was given.
        assertTrue(stuck.wasClosed)
        assertTrue(sockets.openedSockets.single().isClosed)
    }

    @Test
    fun `a socket that cannot be opened fails the start and closes the connection`() = runTest {
        val stream = next()
        val session = open(through = Sockets(sockets::transport) { throw SocketException("Address not available") })

        val failed = assertFailsWith<RtspException> { session.grabFrame(5.seconds) }

        assertTrue(causes(failed).any { it is SocketException })
        assertTrue(stream.wasClosed)
    }

    @Test
    fun `a connection that cannot be made fails the start with nothing left open`() = runTest {
        var opened = 0
        val refusing = Sockets(
            transports = { port ->
                if (port ==
                    GpSockConnection.PORT
                ) {
                    control
                } else {
                    throw SocketException("Connection refused")
                }
            },
            datagrams = {
                opened++
                FailingDatagrams()
            },
        )
        val session = open(through = refusing)

        val failed = assertFailsWith<RtspException> { session.grabFrame(5.seconds) }

        assertTrue(causes(failed).any { it is SocketException })
        assertEquals(0, opened)
    }

    @Test
    fun `noise that is not rtp at all is reported once 64 datagrams of it have arrived`() = runTest {
        next().noiseAfterPlay += List(64) { byteArrayOf(1, 2, 3) }
        val session = open()

        assertFailsWith<CameraTimeoutException> { session.grabFrame(1.seconds) }

        assertTrue("rtp: 64 datagrams arrived and none was an RTP/JPEG packet" in trace)
    }

    @Test
    fun `noise after the stream has shown itself to be rtp is not called a stream of noise`() = runTest {
        next().frames += FakeRtspCamera.jpeg(200)
        val session = open()
        val seen = mutableListOf<Int>()
        val watching = backgroundScope.launch { session.frames().collect { seen += it.jpeg.size } }
        runCurrent()
        val socket = sockets.openedSockets.single()

        repeat(63) { socket.deliver(byteArrayOf(1, 2, 3)) }
        socket.deliver(FakeRtspCamera.packet(FakeRtspCamera.jpeg(201), 1))
        runCurrent()

        assertEquals(listOf(200, 201), seen)
        assertFalse(trace.any { "none was an RTP/JPEG packet" in it })
        watching.cancel()
    }

    @Test
    fun `the stream works with nobody tracing it`() = runTest {
        // Noise, a broken picture and a good one; then a stream that goes quiet; then one the camera ends.
        val noisy = next().apply {
            noiseAfterPlay += List(64) { byteArrayOf(1, 2, 3) }
            frames += listOf(byteArrayOf(1, 2, 3, 4, 5, 6), FakeRtspCamera.jpeg(64))
        }
        next().streamStarted = { false }
        next().apply {
            frames += FakeRtspCamera.jpeg(65)
            closesAfterFrames = true
        }
        val session = open()
        session.trace = null

        assertEquals(64, session.grabFrame(5.seconds).jpeg.size)
        runCurrent()
        assertTrue(noisy.wasClosed)
        assertFailsWith<CameraTimeoutException> { session.frames().collect() }
        assertEquals(1, session.frames().toList().size)

        assertTrue(trace.isEmpty())
    }

    @Test
    fun `ending the session ends every watcher's pictures and closes the stream and the control channel`() =
        runTest {
            val stream = next().apply {
                pace = 30.milliseconds
                frames += pictures(100)
            }
            val session = open()
            val watching = backgroundScope.async { session.frames().toList().size }
            session.frames().first()

            session.close()

            assertTrue(watching.await() < 100)
            assertTrue(stream.wasClosed)
            assertTrue(sockets.openedSockets.single().isClosed)
            assertTrue(control.wasDisposed)
        }

    @Test
    fun `ending the session while the stream is starting stops the start and tells a grab why`() = runTest {
        val stuck = next().apply { neverAnswers = "DESCRIBE" }
        val session = open(CameraSessionTiming(open = 10.seconds, start = 30.seconds, stall = 8.seconds))
        val grabbing = async { runCatching { session.grabFrame(60.seconds) } }
        runCurrent()

        session.close()

        val closed = assertIs<IllegalStateException>(grabbing.await().exceptionOrNull())
        assertEquals("The session closed before a whole picture arrived.", closed.message)
        assertTrue(stuck.wasClosed)
        assertTrue(control.wasDisposed)
    }

    @Test
    fun `a session that has ended starts no stream and closed only its control channel`() = runTest {
        val session = open()

        session.close()

        assertEquals(
            "The session has ended.",
            assertFailsWith<IllegalStateException> {
                session.grabFrame(1.seconds)
            }.message,
        )
        assertFailsWith<IllegalStateException> { session.frames().collect() }
        assertTrue(streams.isEmpty())
        assertTrue(control.wasDisposed)
    }

    @Test
    fun `a session opened without timings gets the real ones`() = runTest {
        val session = CameraSession.open(FakeCameraSockets { control }, backgroundScope)

        assertEquals(16, session.control.getStatus().length)
        assertEquals(CameraSessionTiming(10.seconds, 10.seconds, 8.seconds), CameraSessionTiming.Default)
    }

    @Test
    fun `a control channel that cannot open is closed again and reported`() = runTest {
        val refusing = StubbornTransport { throw IOException("Connection refused") }

        assertFailsWith<IOException> { CameraSession.open(FakeCameraSockets { refusing }, backgroundScope) }

        assertTrue(refusing.closed)
    }

    @Test
    fun `a control port that never answers is given up on and closed`() = runTest {
        // What a camera whose access point is half asleep does: the connect neither succeeds nor fails.
        val silent = StubbornTransport { awaitCancellation() }

        val gaveUp = assertFailsWith<CameraTimeoutException> {
            CameraSession.open(FakeCameraSockets { silent }, backgroundScope)
        }

        assertEquals("The camera did not answer on its control port within 10 seconds.", gaveUp.message)
        assertTrue(silent.closed)
    }

    @Test
    fun `a blank host is refused`() = runTest {
        assertFailsWith<IllegalArgumentException> {
            CameraSession.open(FakeCameraSockets { control }, backgroundScope, host = " ")
        }
        assertFalse(control.isConnected)
    }

    /** Connections and sockets that come from the test. */
    private class Sockets(
        private val transports: (port: Int) -> CameraTransport,
        private val datagrams: () -> CameraDatagrams,
    ) : CameraSockets {
        override fun transport(port: Int): CameraTransport = transports(port)

        override fun datagrams(): CameraDatagrams = datagrams.invoke()
    }

    /** A socket whose every receive fails, as one on a network that has gone does. */
    private class FailingDatagrams : CameraDatagrams {
        var closed = false

        override val port = 50999

        override suspend fun send(datagram: ByteArray, port: Int) = Unit

        override suspend fun receive(into: ByteArray): Int = throw SocketException("Network is unreachable")

        override fun close() {
            closed = true
        }
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
