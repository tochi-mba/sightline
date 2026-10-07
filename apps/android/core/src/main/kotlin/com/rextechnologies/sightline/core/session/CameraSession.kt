package com.rextechnologies.sightline.core.session

import com.rextechnologies.sightline.protocol.CameraDatagrams
import com.rextechnologies.sightline.protocol.CameraSockets
import com.rextechnologies.sightline.protocol.gpsock.GpSockConnection
import com.rextechnologies.sightline.protocol.rtp.CameraFrame
import com.rextechnologies.sightline.protocol.rtp.RtspClient
import com.rextechnologies.sightline.protocol.rtp.RtspException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.firstOrNull
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.withTimeoutOrNull
import java.io.Closeable
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TimeSource

/**
 * One conversation with one camera: its control channel, and its stream while anybody watches it.
 *
 * The control connection is opened once and held for the life of this object, because the camera's
 * firmware treats that socket as the sign that a client is still there. Closing it stops a recording,
 * so this type closes it only when [close] is called.
 *
 * The connections and the stream's socket come from a [CameraSockets], so tests can stand a fake camera
 * behind them all, and so the phone can hand out sockets that travel over the camera's Wi-Fi. This is the
 * port of the .NET `CameraSession`, held to the same fakes and the same timings.
 */
class CameraSession private constructor(
    /** The control channel. */
    val control: GpSockConnection,
    private val sockets: CameraSockets,
    private val host: String,
    private val timing: CameraSessionTiming,
    private val scope: CoroutineScope,
    private val timeSource: TimeSource,
) : Closeable {
    private val lock = Any()
    private var feed: LiveFeed? = null

    @Volatile
    private var closed = false

    /**
     * Receives one line per step of starting and running the stream, when set.
     *
     * The camera gives no error when a stream fails to start; it simply sends nothing. Knowing which step
     * went quiet is the difference between a fix and a guess.
     */
    var trace: ((String) -> Unit)? = null

    /**
     * Takes one picture from the stream, without touching the camera's card.
     *
     * The picture comes from the stream everybody else is watching, which this starts if nobody is, and
     * which stops again afterwards if nobody else wants it.
     *
     * @throws CameraTimeoutException No whole picture arrived within [timeout].
     * @throws RtspException The camera refused the stream, or ended it before a picture arrived.
     */
    suspend fun grabFrame(timeout: Duration): CameraFrame {
        val frame = try {
            withTimeoutOrNull(timeout) { frames().firstOrNull() ?: throw endedBeforeAPicture() }
        } catch (_: CameraTimeoutException) {
            null
        }

        return frame
            ?: throw CameraTimeoutException("No whole picture arrived within ${timeout.inWholeSeconds} seconds.")
    }

    /**
     * The camera's pictures, one after another, until the camera ends its stream or the collector stops,
     * which both end the flow quietly.
     *
     * Everybody collecting shares one stream, started on the control channel first: RTSP alone negotiates
     * happily and then delivers nothing. The stream stops when its last collector stops, and the camera
     * ends it on its own when its card is browsed; collecting again starts another. Pictures that are not
     * whole JPEGs are skipped, so a collector can decode everything it receives.
     *
     * @throws CameraTimeoutException The stream did not start in time, or the camera went quiet.
     * @throws RtspException The stream could not start, or its connection or socket failed.
     */
    fun frames(): Flow<CameraFrame> = flow {
        val watcher = watch()
        try {
            for (frame in watcher.pictures) {
                emit(frame)
            }
        } finally {
            watcher.leave()
        }
    }

    /**
     * Ends the session: the stream, then the control channel.
     *
     * The camera stops recording when the control channel goes. Anybody still collecting sees the pictures
     * end.
     */
    override fun close() {
        val current = synchronized(lock) {
            closed = true
            feed.also { feed = null }
        }

        current?.stop()
        scope.cancel()
        control.close()
    }

    /** Why a stream that ended quietly gave no picture: the session closing, or the camera ending it. */
    private fun endedBeforeAPicture(): Exception = if (closed) {
        IllegalStateException("The session closed before a whole picture arrived.")
    } else {
        RtspException("The camera ended its stream before a whole picture arrived.")
    }

    /** Joins the stream that is running, or starts one if none is. */
    private fun watch(): LiveFeed.Watcher = synchronized(lock) {
        check(!closed) { "The session has ended." }
        // None is running, or the one there ended a moment ago and is on its way out.
        feed?.watch() ?: LiveFeed(scope, ::startStream, timing.stall, { trace }, timeSource, ::retire)
            .also { feed = it }
            .first
    }

    /**
     * Starts the media flow and negotiates the stream, all within [CameraSessionTiming.start].
     *
     * @return The stream's RTSP connection, played, and the socket its pictures arrive at.
     * @throws CameraTimeoutException It did not start in time.
     */
    private suspend fun startStream(): Pair<RtspClient, CameraDatagrams> {
        val rtsp = RtspClient(sockets.transport(RtspClient.PORT), host)
        val datagrams = try {
            sockets.datagrams()
        } catch (failure: Throwable) {
            rtsp.close()
            throw failure
        }

        try {
            withTimeoutOrNull(timing.start) { negotiate(rtsp, datagrams) }
                ?: throw CameraTimeoutException(
                    "The camera did not start its stream within ${timing.start.inWholeSeconds} seconds.",
                )
            return rtsp to datagrams
        } catch (failure: Throwable) {
            rtsp.close()
            datagrams.close()
            throw failure
        }
    }

    private suspend fun negotiate(rtsp: RtspClient, datagrams: CameraDatagrams) {
        trace?.invoke("control: RestartStreaming ...")
        control.startStreaming()
        trace?.invoke("control: RestartStreaming acknowledged")
        trace?.invoke("rtsp: connecting to $host:${RtspClient.PORT} ...")
        rtsp.connect()
        val describe = rtsp.describe()
        trace?.invoke("rtsp: DESCRIBE ${describe.statusCode}, ${describe.body.length} bytes of SDP")

        val setup = rtsp.setupVideo(datagrams.port)
        val from = rtsp.serverPort?.toString() ?: "a port it did not say"
        trace?.invoke(
            "rtsp: SETUP ${setup.statusCode}, session ${rtsp.session ?: "(none)"}, from $from to ${datagrams.port}",
        )
        if (!setup.isSuccess) {
            throw RtspException("The camera refused the video track (${setup.statusCode}).")
        }

        val play = rtsp.play()
        trace?.invoke("rtsp: PLAY ${play.statusCode}")
        if (!play.isSuccess) {
            throw RtspException("The camera would not start the stream (${play.statusCode}).")
        }

        // A firewall that drops what it did not ask for lets the stream in as the reply to this.
        rtsp.serverPort?.let { datagrams.send(OPENER, it) }
    }

    /** Lets go of a stream that is over, or that nobody is watching any more. */
    private fun retire(ended: LiveFeed) {
        synchronized(lock) {
            if (feed === ended) {
                feed = null
            }
        }

        ended.stop()
    }

    companion object {
        /** The address every camera of this family answers on. */
        const val CAMERA_HOST = "192.168.100.1"

        /** What is sent from the stream's port to the camera's, so a firewall lets the stream in as the reply. */
        private val OPENER = byteArrayOf(0)

        /**
         * Opens a session over [sockets].
         *
         * @param sockets Connections to the camera, and sockets for its stream.
         * @param scope Where the stream runs while anybody watches it; the session ends its own part of it.
         * @param host The camera's address, as RTSP URLs must name it.
         * @param timing How long each step may take; the real values when omitted.
         * @param timeSource How the age of a picture is measured.
         * @throws CameraTimeoutException The control port did not answer within [CameraSessionTiming.open].
         */
        suspend fun open(
            sockets: CameraSockets,
            scope: CoroutineScope,
            host: String = CAMERA_HOST,
            timing: CameraSessionTiming = CameraSessionTiming.Default,
            timeSource: TimeSource = TimeSource.Monotonic,
        ): CameraSession {
            require(host.isNotBlank()) { "The camera's host must not be blank." }
            val control = GpSockConnection(sockets.transport(GpSockConnection.PORT))
            try {
                withTimeoutOrNull(timing.open) { control.open() }
                    ?: throw CameraTimeoutException(
                        "The camera did not answer on its control port within ${timing.open.inWholeSeconds} seconds.",
                    )
            } catch (failure: Throwable) {
                control.close()
                throw failure
            }

            val own = CoroutineScope(scope.coroutineContext + SupervisorJob(scope.coroutineContext[Job]))
            return CameraSession(control, sockets, host, timing, own, timeSource)
        }
    }
}

/**
 * How long each step is given.
 *
 * @property open To connect the control channel.
 * @property start To start: RestartStreaming, then RTSP's DESCRIBE, SETUP and PLAY.
 * @property stall To go silent before the stream is called lost. At about 12 pictures a second this is many
 *   dozens of missing frames.
 */
data class CameraSessionTiming(val open: Duration, val start: Duration, val stall: Duration) {
    companion object {
        /** The real timings, the same as the Windows app's. */
        val Default = CameraSessionTiming(open = 10.seconds, start = 10.seconds, stall = 8.seconds)
    }
}

/**
 * The camera did not do something in the time it was given.
 *
 * Not a [kotlinx.coroutines.CancellationException], which is what a coroutine timeout throws, because
 * a camera that went quiet is a failure to report, and a cancellation is silently swallowed by whatever
 * is collecting.
 */
class CameraTimeoutException(override val message: String) : Exception(message)
