package com.rextechnologies.sightline.core.session

import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.gpsock.GpSockConnection
import com.rextechnologies.sightline.protocol.rtp.CameraFrame
import com.rextechnologies.sightline.protocol.rtp.RtspClient
import com.rextechnologies.sightline.protocol.rtp.RtspException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emitAll
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withTimeoutOrNull
import java.io.Closeable
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TimeSource

/**
 * One conversation with one camera: its control channel, and its one live stream once asked for.
 *
 * The control connection is opened once and held for the life of this object, because the camera's
 * firmware treats that socket as the sign that a client is still there. Closing it stops a recording,
 * so this type closes it only when [close] is called.
 *
 * The camera answers one stream connection each time it is switched on, and cannot be asked to stop one:
 * TEARDOWN is not implemented and PAUSE is ignored. So the stream is opened once, the first time a
 * picture is wanted, and kept until the session ends; see PROTOCOL.md, "One stream per power-on".
 *
 * The transports come from a factory so tests can stand a fake camera behind both, and so the phone can
 * hand out sockets that travel over the camera's Wi-Fi. This is the port of the .NET `CameraSession`,
 * held to the same fakes and the same timings.
 */
class CameraSession private constructor(
    /** The control channel. */
    val control: GpSockConnection,
    private val transports: (port: Int) -> CameraTransport,
    private val host: String,
    private val timing: CameraSessionTiming,
    private val scope: CoroutineScope,
    private val timeSource: TimeSource,
) : Closeable {
    private val lock = Any()
    private var feed: Deferred<LiveFeed>? = null
    private var running: LiveFeed? = null
    private var spent: String? = null

    /**
     * Receives one line per step of starting and running the stream, when set.
     *
     * The camera gives no error when a stream fails to start; it simply sends nothing. Knowing which step
     * went quiet is the difference between a fix and a guess.
     */
    var trace: ((String) -> Unit)? = null

    /**
     * Whether this session holds the camera's live picture: its one stream is open and still running, so
     * anything that ends it, such as browsing the card, costs the picture until the camera's battery is taken
     * out and put back.
     */
    val holdsLivePicture: Boolean
        get() = synchronized(lock) { feed != null && spent == null }

    /**
     * Takes one picture from the live stream, without touching the camera's card. The stream is started
     * if nothing has started it yet, and left running.
     *
     * @throws CameraTimeoutException No whole picture arrived within [timeout].
     * @throws LivePictureUnavailableException The camera will not give this session a live picture.
     */
    suspend fun grabFrame(timeout: Duration): CameraFrame {
        val frame = try {
            withTimeoutOrNull(timeout) { frames().first() }
        } catch (_: CameraTimeoutException) {
            null
        }

        return frame
            ?: throw CameraTimeoutException("No whole picture arrived within ${timeout.inWholeSeconds} seconds.")
    }

    /**
     * The live picture, frame after frame, until the collector stops. Stopping leaves the stream running
     * for whoever collects next.
     *
     * The stream is started on the control channel first: RTSP alone negotiates happily and then delivers
     * nothing. Frames that are not whole JPEGs are skipped, so a collector can decode everything it
     * receives.
     *
     * @throws CameraTimeoutException Nothing arrived for [CameraSessionTiming.stall]. The stream stays
     *   open; collecting again waits on it again.
     * @throws LivePictureUnavailableException The camera will not give this session a live picture: it
     *   ended the one it gave, or never started it.
     */
    fun frames(): Flow<CameraFrame> = flow { emitAll(feed().pictures(timing.stall)) }

    /**
     * Ends the session: the stream, then the control channel.
     *
     * The camera stops recording when the control channel goes.
     */
    override fun close() {
        scope.cancel()
        synchronized(lock) { running }?.close()
        control.close()
    }

    /** The session's one stream, started by whoever asks first. */
    private suspend fun feed(): LiveFeed {
        val starting = synchronized(lock) {
            spent?.let { throw LivePictureUnavailableException(it) }
            feed ?: scope.async { start() }.also { feed = it }
        }

        return try {
            starting.await()
        } catch (cancelled: CancellationException) {
            if (!currentCoroutineContext().isActive) {
                throw cancelled
            }

            // The start was given up because the session ended, not because this caller did.
            throw LivePictureUnavailableException(spend("The session ended before the live picture started."))
        }
    }

    /**
     * Starts the media flow and negotiates the stream, all within [CameraSessionTiming.start].
     *
     * Not tied to whoever asked first: giving up waiting must not abandon a start others may be waiting
     * on. Any failure is final for this session, because a second connection would not be answered.
     */
    private suspend fun start(): LiveFeed {
        val rtsp = RtspClient(transports(RtspClient.PORT), host)
        val started = try {
            withTimeoutOrNull(timing.start) { negotiate(rtsp) }
        } catch (cancelled: CancellationException) {
            rtsp.close()
            throw cancelled
        } catch (failure: Exception) {
            rtsp.close()
            throw LivePictureUnavailableException(
                spend(failure.message ?: "The live picture could not be started."),
                failure,
            )
        }

        if (started == null) {
            rtsp.close()
            throw LivePictureUnavailableException(
                spend(
                    "The camera did not start its live picture within ${timing.start.inWholeSeconds} seconds, " +
                        "which is what it does once it has given its live picture to an earlier connection.",
                ),
            )
        }

        return LiveFeed(rtsp, scope, { trace }, timeSource, ::spend).also { synchronized(lock) { running = it } }
    }

    private suspend fun negotiate(rtsp: RtspClient) {
        trace?.invoke("control: RestartStreaming ...")
        control.startStreaming()
        trace?.invoke("control: RestartStreaming acknowledged")
        trace?.invoke("rtsp: connecting to $host:${RtspClient.PORT} ...")
        rtsp.connect()
        val describe = rtsp.describe()
        trace?.invoke("rtsp: DESCRIBE ${describe.statusCode}, ${describe.body.length} bytes of SDP")

        val setup = rtsp.setupVideo()
        trace?.invoke("rtsp: SETUP ${setup.statusCode}, session ${rtsp.session ?: "(none)"}")
        if (!setup.isSuccess) {
            throw RtspException("The camera refused the video track (${setup.statusCode}).")
        }

        val play = rtsp.play()
        trace?.invoke("rtsp: PLAY ${play.statusCode}")
        if (!play.isSuccess) {
            throw RtspException("The camera would not start the stream (${play.statusCode}).")
        }
    }

    /** Records that this session's live picture is gone, and returns what to tell people. */
    private fun spend(reason: String): String {
        val told = "$reason ${LivePictureUnavailableException.ADVICE}"
        synchronized(lock) {
            if (spent == null) {
                spent = told
            }
        }

        return told
    }

    companion object {
        /** The address every camera of this family answers on. */
        const val CAMERA_HOST = "192.168.100.1"

        /**
         * Opens a session over transports from [transports].
         *
         * @param transports Makes a transport to a given port on the camera.
         * @param scope Where the stream is read, once there is one; the session ends its own part of it.
         * @param host The camera's address, as RTSP URLs must name it.
         * @param timing How long each step may take; the real values when omitted.
         * @param timeSource How the age of a picture is measured.
         * @throws CameraTimeoutException The control port did not answer within [CameraSessionTiming.open].
         */
        suspend fun open(
            transports: (port: Int) -> CameraTransport,
            scope: CoroutineScope,
            host: String = CAMERA_HOST,
            timing: CameraSessionTiming = CameraSessionTiming.Default,
            timeSource: TimeSource = TimeSource.Monotonic,
        ): CameraSession {
            require(host.isNotBlank()) { "The camera's host must not be blank." }
            val control = GpSockConnection(transports(GpSockConnection.PORT))
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
            return CameraSession(control, transports, host, timing, own, timeSource)
        }
    }
}

/**
 * How long each step is given.
 *
 * @property open To connect the control channel.
 * @property start To start: RestartStreaming, then RTSP's DESCRIBE, SETUP and PLAY.
 * @property stall To go silent before a watcher is told so. At about 12 pictures a second this is many
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

/**
 * The camera will not give this session a live picture, and asking again will not change that until its
 * battery is taken out and put back.
 *
 * The reference camera answers one stream connection per power-on, ends it when its card is browsed, and
 * leaves its own buttons stuck once it has ended, so it cannot even be switched off; see PROTOCOL.md,
 * "One stream per power-on".
 */
class LivePictureUnavailableException(override val message: String, cause: Throwable? = null) :
    Exception(message, cause) {
    companion object {
        /** What to do about it, said after every reason. */
        const val ADVICE =
            "This camera gives its live picture once each time it starts, and its own buttons stay stuck once it " +
                "ends: take its battery out and put it back to see it again."
    }
}
