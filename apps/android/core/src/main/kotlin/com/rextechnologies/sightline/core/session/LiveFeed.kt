package com.rextechnologies.sightline.core.session

import com.rextechnologies.sightline.protocol.CameraDatagrams
import com.rextechnologies.sightline.protocol.rtp.CameraFrame
import com.rextechnologies.sightline.protocol.rtp.RtpJpegReassembler
import com.rextechnologies.sightline.protocol.rtp.RtspClient
import com.rextechnologies.sightline.protocol.rtp.RtspException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.ReceiveChannel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TimeMark
import kotlin.time.TimeSource

/** How fresh the latest picture must be for a new watcher to be shown it at once. */
private val FRESH = 1.seconds

/** How many datagrams that are not RTP/JPEG arrive, with none that is, before the trace says so. */
private const val NOISE_REPORTED_AFTER = 64L

/** Bigger than any datagram can be, so none is ever cut short. */
private const val DATAGRAM_BUFFER = 64 * 1024

/** What watchers are told when a stream ends, made afresh for each of them; null when it ended without a fault. */
private typealias Ending = (() -> Exception)?

/**
 * One run of the camera's stream, from starting it to its end, shared by everybody who wants its pictures.
 *
 * It runs while anybody watches. When the last watcher leaves, even before the stream has started, it
 * ends, and closing its RTSP connection is what stops the camera sending. It also ends when the camera
 * closes that connection, as browsing its card does, or sends nothing for the stall time, or the start
 * fails; whoever asks next starts another.
 *
 * Each watcher gets the newest picture: one not yet taken when the next arrives is replaced by it, so a
 * slow watcher sees fewer pictures, never older ones. A new watcher starts with the latest picture when it
 * is under a second old, so coming back to the picture shows one at once. This is the port of the .NET
 * `LiveFeed`.
 *
 * @param scope Where the stream runs.
 * @param start Starts the stream: its RTSP connection, played, and the socket its pictures arrive at.
 * @param stall How long the camera may send nothing before the stream is called lost.
 * @param trace Where trace lines go, read each time so it can be changed meanwhile.
 * @param timeSource How the age of a picture is measured.
 * @param retire Told, once, that this feed is over or that nobody is watching it.
 */
internal class LiveFeed(
    scope: CoroutineScope,
    private val start: suspend () -> Pair<RtspClient, CameraDatagrams>,
    private val stall: Duration,
    private val trace: () -> ((String) -> Unit)?,
    private val timeSource: TimeSource,
    private val retire: (LiveFeed) -> Unit,
) {
    private val lock = Any()
    private val watchers = mutableListOf<Channel<CameraFrame>>()
    private var latest: Pair<CameraFrame, TimeMark>? = null
    private var over = false

    @Volatile
    private var running: Pair<RtspClient, CameraDatagrams>? = null

    /** The watcher the feed was started for, which sees the start's failure if it fails. */
    val first: Watcher = watch()!!

    private val job: Job = scope.launch { run() }

    /**
     * Starts sending pictures to a new watcher: the latest if it is fresh, then each one that arrives.
     *
     * @return The watcher, or null once this feed is over and another must be started.
     */
    fun watch(): Watcher? {
        val channel = Channel<CameraFrame>(Channel.CONFLATED)
        synchronized(lock) {
            if (over) {
                return null
            }

            latest?.let { (frame, at) ->
                if (at.elapsedNow() < FRESH) {
                    channel.trySend(frame)
                }
            }

            watchers += channel
        }

        return Watcher(channel)
    }

    /** Stops the stream, closing its connection and socket now. Harmless to repeat. */
    fun stop() {
        job.cancel()
        running?.let { (rtsp, datagrams) ->
            rtsp.close()
            datagrams.close()
        }
    }

    private fun leave(channel: Channel<CameraFrame>) {
        synchronized(lock) {
            if (!watchers.remove(channel) || watchers.isNotEmpty()) {
                return
            }

            // Nobody is watching: no one may join a feed on its way out.
            over = true
        }

        retire(this)
    }

    /**
     * Starts the stream, then reads its pictures and watches its connection until one of them ends it, or
     * it is stopped; then closes both.
     */
    private suspend fun run() {
        val (rtsp, datagrams) = try {
            start()
        } catch (cancelled: CancellationException) {
            end(null)
            throw cancelled
        } catch (failure: Exception) {
            end { copy(failure) }
            return
        }

        running = rtsp to datagrams
        try {
            val ending = coroutineScope {
                val first = CompletableDeferred<Ending>()
                val pumping = launch { first.complete(pump(datagrams)) }
                val watching = launch { first.complete(watch(rtsp)) }
                first.await().also {
                    pumping.cancel()
                    watching.cancel()
                }
            }
            end(ending)
        } catch (cancelled: CancellationException) {
            end(null)
            throw cancelled
        } finally {
            // Closing the connection is what stops the camera sending.
            rtsp.close()
            datagrams.close()
        }
    }

    /** The pictures, until the camera goes quiet or the socket fails; then what to tell watchers. */
    private suspend fun pump(datagrams: CameraDatagrams): Ending {
        val reassembler = RtpJpegReassembler()
        val buffer = ByteArray(DATAGRAM_BUFFER)
        var received = 0L
        try {
            while (true) {
                val length = withTimeoutOrNull(stall) { datagrams.receive(buffer) }
                    ?: throw CameraTimeoutException("The camera sent nothing for ${stall.inWholeSeconds} seconds.")
                if (received++ == 0L) {
                    val first = buffer.copyOf(minOf(16, length)).toHexString(HexFormat.UpperCase)
                    trace()?.invoke("rtp: first datagram arrived: $first")
                }

                val frame = reassembler.push(buffer, length)
                if (frame == null) {
                    if (received == NOISE_REPORTED_AFTER && reassembler.packetsRead == 0) {
                        trace()?.invoke("rtp: $NOISE_REPORTED_AFTER datagrams arrived and none was an RTP/JPEG packet")
                    }

                    continue
                }

                if (RtpJpegReassembler.looksLikeJpeg(frame.jpeg)) {
                    publish(frame)
                } else {
                    trace()?.invoke("rtp: a ${frame.jpeg.size}-byte picture was not a whole JPEG and was skipped")
                }
            }
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (quiet: CameraTimeoutException) {
            trace()?.invoke("rtp: ${quiet.message}")
            return { CameraTimeoutException(quiet.message) }
        } catch (failure: Exception) {
            return { RtspException("The stream was lost: ${failure.message}", failure) }
        }
    }

    /** The stream's connection, until the camera closes it; then what to tell watchers. */
    private suspend fun watch(rtsp: RtspClient): Ending {
        try {
            rtsp.waitForClose()
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            return { RtspException("The stream was lost: ${failure.message}", failure) }
        }

        trace()?.invoke("rtsp: the camera closed the stream")
        return null
    }

    private fun publish(frame: CameraFrame) {
        synchronized(lock) {
            latest = frame to timeSource.markNow()
            watchers.forEach { it.trySend(frame) }
        }
    }

    /** Tells every watcher the stream is over: quietly, or with what went wrong. */
    private fun end(failure: Ending) {
        val retiring: Boolean
        val ended: List<Channel<CameraFrame>>
        synchronized(lock) {
            retiring = !over
            over = true
            latest = null
            ended = watchers.toList()
            watchers.clear()
        }

        ended.forEach { it.close(failure?.invoke()) }
        if (retiring) {
            retire(this)
        }
    }

    /** One watcher's pictures. Leaving stops them, and ends the feed if nobody else is watching. */
    inner class Watcher(private val channel: Channel<CameraFrame>) {
        /**
         * The pictures, until the stream is over: they end quietly when it ended without a fault, and
         * receiving fails with [CameraTimeoutException] or [RtspException] when it did not.
         */
        val pictures: ReceiveChannel<CameraFrame>
            get() = channel

        /** Stops this watcher's pictures. */
        fun leave() = leave(channel)
    }

    private companion object {
        /** A copy of a start's failure for one watcher, of a kind that says what went wrong. */
        fun copy(failure: Exception): Exception = if (failure is CameraTimeoutException) {
            CameraTimeoutException(failure.message)
        } else {
            RtspException(failure.message ?: "The stream could not be started.", failure)
        }
    }
}
