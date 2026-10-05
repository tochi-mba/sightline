package com.rextechnologies.sightline.core.session

import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.gpsock.GpSockConnection
import com.rextechnologies.sightline.protocol.rtp.CameraFrame
import com.rextechnologies.sightline.protocol.rtp.RtpJpegReassembler
import com.rextechnologies.sightline.protocol.rtp.RtspClient
import com.rextechnologies.sightline.protocol.rtp.RtspException
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.firstOrNull
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import java.io.Closeable
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * One conversation with one camera: its control channel, and its picture when asked for.
 *
 * The control connection is opened once and held for the life of this object, because the camera's
 * firmware treats that socket as the sign that a client is still there. Closing it stops a recording
 * and tears down the stream, so this type closes it only when [close] is called.
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
) : Closeable {
    /**
     * Receives one line per step of starting and running the stream, when set.
     *
     * The camera gives no error when a stream fails to start; it simply sends nothing. Knowing which step
     * went quiet is the difference between a fix and a guess.
     */
    var trace: ((String) -> Unit)? = null

    /**
     * Takes one picture from the live stream, without touching the camera's card.
     *
     * @throws CameraTimeoutException No whole picture arrived within [timeout].
     * @throws RtspException The camera stopped sending, or refused the stream.
     */
    suspend fun grabFrame(timeout: Duration): CameraFrame {
        val frame = withTimeoutOrNull(timeout) {
            // Taking the first frame ends the flow, which is what sends its TEARDOWN.
            frames().firstOrNull() ?: throw RtspException("The camera stopped sending before a whole picture arrived.")
        }
        return frame
            ?: throw CameraTimeoutException("No whole picture arrived within ${timeout.inWholeSeconds} seconds.")
    }

    /**
     * The live picture, frame after frame, until the collector stops or the camera does.
     *
     * The stream is started on the control channel first: RTSP alone negotiates happily and then
     * delivers nothing. Frames that are not whole JPEGs are skipped rather than passed on, so a collector
     * can decode everything it receives. The flow completes when the camera closes the stream, and fails
     * with [CameraTimeoutException] when it will not start or goes silent.
     */
    fun frames(): Flow<CameraFrame> = flow {
        val rtsp = RtspClient(transports(RtspClient.PORT), host)
        try {
            start(rtsp)

            val reassembler = RtpJpegReassembler()
            var received = 0L
            while (true) {
                val bytes = readOrStall(rtsp)
                if (bytes.isEmpty()) {
                    trace?.invoke("rtsp: the camera closed the stream after $received bytes")
                    return@flow
                }

                if (received == 0L) {
                    trace?.invoke(
                        "rtsp: first stream bytes arrived: ${bytes.take(
                            16,
                        ).toByteArray().toHexString(HexFormat.UpperCase)}",
                    )
                }

                received += bytes.size
                for (frame in reassembler.push(bytes)) {
                    if (RtpJpegReassembler.looksLikeJpeg(frame.jpeg)) {
                        emit(frame)
                    } else {
                        trace?.invoke("rtsp: a ${frame.jpeg.size}-byte frame was not a whole JPEG and was skipped")
                    }
                }

                if (reassembler.packetsRead == 0 && received > NOISE_REPORTED_AFTER) {
                    trace?.invoke("rtsp: $received bytes arrived but none parsed as an RTP packet")
                }
            }
        } finally {
            // On every way out: cancelled, failed, or the collector simply stopped. The camera's RTSP
            // server is single-threaded and does not reap an abandoned session: one left without a
            // TEARDOWN stops it answering anybody until the camera is restarted.
            withContext(NonCancellable) { tryTeardown(rtsp) }
            rtsp.close()
        }
    }

    /** Starts the media flow and negotiates the stream, all within [CameraSessionTiming.start]. */
    private suspend fun start(rtsp: RtspClient) {
        val started = withTimeoutOrNull(timing.start) {
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

        if (started == null) {
            throw CameraTimeoutException(
                "The camera did not start its stream within ${timing.start.inWholeSeconds} seconds.",
            )
        }
    }

    private suspend fun readOrStall(rtsp: RtspClient): ByteArray = withTimeoutOrNull(timing.stall) { rtsp.readStream() }
        ?: throw CameraTimeoutException("The camera sent nothing for ${timing.stall.inWholeSeconds} seconds.")

    private suspend fun tryTeardown(rtsp: RtspClient) {
        if (rtsp.session == null) {
            // SETUP never succeeded, so there is no session on the camera to end.
            return
        }

        try {
            withTimeout(timing.teardown) { rtsp.teardown() }
        } catch (_: Exception) {
            // Best effort, from a finally: a failure here, a reset connection or the deadline, would
            // replace whatever brought the stream down with a less useful one about saying goodbye.
        }
    }

    /**
     * Ends the session.
     *
     * The camera stops recording and streaming when this happens.
     */
    override fun close() {
        control.close()
    }

    companion object {
        /** The address every camera of this family answers on. */
        const val CAMERA_HOST = "192.168.100.1"

        /** How much of something that never parses as RTP arrives before the trace says so. */
        private const val NOISE_REPORTED_AFTER = 64 * 1024

        /**
         * Opens a session over transports from [transports].
         *
         * @param transports Makes a transport to a given port on the camera.
         * @param host The camera's address, as RTSP URLs must name it.
         * @param timing How long each step may take; the real values when omitted.
         * @throws CameraTimeoutException The control port did not answer within [CameraSessionTiming.open].
         */
        suspend fun open(
            transports: (port: Int) -> CameraTransport,
            host: String = CAMERA_HOST,
            timing: CameraSessionTiming = CameraSessionTiming.Default,
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

            return CameraSession(control, transports, host, timing)
        }
    }
}

/**
 * How long each step is given.
 *
 * @property open To connect the control channel.
 * @property start To start: RestartStreaming, then RTSP's DESCRIBE, SETUP and PLAY.
 * @property stall To go silent before the stream is called stopped. At about 12 pictures a second this
 *   is many dozens of missing frames.
 * @property teardown To end the RTSP session properly on the way out.
 */
data class CameraSessionTiming(val open: Duration, val start: Duration, val stall: Duration, val teardown: Duration) {
    companion object {
        /** The real timings, the same as the Windows app's. */
        val Default =
            CameraSessionTiming(open = 10.seconds, start = 10.seconds, stall = 8.seconds, teardown = 2.seconds)
    }
}

/**
 * The camera did not do something in the time it was given.
 *
 * Not a [kotlinx.coroutines.CancellationException], which is what a coroutine timeout throws, because
 * a camera that went quiet is a failure to report, and a cancellation is silently swallowed by whatever
 * is collecting.
 */
class CameraTimeoutException(message: String) : Exception(message)
