package com.rextechnologies.sightline.core.session

import com.rextechnologies.sightline.protocol.rtp.CameraFrame
import com.rextechnologies.sightline.protocol.rtp.RtpJpegReassembler
import com.rextechnologies.sightline.protocol.rtp.RtspClient
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.io.Closeable
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TimeMark
import kotlin.time.TimeSource

/** How fresh the latest picture must be for a new watcher to be shown it at once. */
private val FRESH = 1.seconds

/** How much of something that never parses as RTP arrives before the trace says so. */
private const val NOISE_REPORTED_AFTER = 64 * 1024

/**
 * The camera's one live stream, read without pause and shared by everybody who wants its pictures.
 *
 * The camera answers one stream connection each time it is switched on, so this is opened once per
 * session and never closed while the session lasts: watching stops and starts by collecting, not by
 * touching the connection. It is read continuously even with nobody watching, so the camera is never
 * left blocked on a connection nobody drains.
 *
 * Each watcher gets the newest picture: one not yet taken when the next arrives is replaced by it, so a
 * slow watcher sees fewer pictures, never older ones. A new watcher starts with the latest picture when
 * it is under a second old, so coming back to the picture shows one at once. This is the port of the
 * .NET `LiveFeed`.
 *
 * @param rtsp The stream connection, whose PLAY has been answered.
 * @param scope Where the stream is read; cancelling it ends the stream.
 * @param trace Where trace lines go, read each time so it can be changed meanwhile.
 * @param timeSource How the age of a picture is measured.
 * @param ended Told, once, why the stream ended; returns what watchers are told.
 */
internal class LiveFeed(
    private val rtsp: RtspClient,
    scope: CoroutineScope,
    private val trace: () -> ((String) -> Unit)?,
    private val timeSource: TimeSource,
    ended: (String) -> String,
) : Closeable {
    private sealed interface Item {
        class Picture(val frame: CameraFrame, val at: TimeMark) : Item

        class Ended(val message: String) : Item
    }

    private val latest = MutableStateFlow<Item?>(null)

    private val pump: Job = scope.launch {
        var reason = "The live picture was closed with the camera's session."
        try {
            reason = read()
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            reason = "The live picture was lost: ${failure.message}"
        } finally {
            latest.value = Item.Ended(ended(reason))
            rtsp.close()
        }
    }

    /**
     * The pictures, from the latest when it is fresh, until the collector stops.
     *
     * One that starts after the stream has ended is told at once. That happens: the stream is read on the
     * same thread as its watchers, so a camera that hangs up straight away can be heard before the first
     * watcher has started looking.
     *
     * @throws CameraTimeoutException Nothing arrived for [stall]; the stream is still open.
     * @throws LivePictureUnavailableException The stream has ended for good.
     */
    fun pictures(stall: Duration): Flow<CameraFrame> = flow {
        var seen = latest.value
        val first = seen
        if (first is Item.Ended) {
            throw LivePictureUnavailableException(first.message)
        }

        if (first is Item.Picture && first.at.elapsedNow() < FRESH) {
            emit(first.frame)
        }

        while (true) {
            val before = seen
            val next = withTimeoutOrNull(stall) { latest.filterNotNull().first { it !== before } }
                ?: throw CameraTimeoutException("The camera sent nothing for ${stall.inWholeSeconds} seconds.")
            seen = next
            when (next) {
                is Item.Picture -> emit(next.frame)
                is Item.Ended -> throw LivePictureUnavailableException(next.message)
            }
        }
    }

    /** Stops reading and closes the connection: only for the end of the session. */
    override fun close() {
        pump.cancel()
        rtsp.close()
    }

    /** Reads the stream until the camera ends it, and says that it did. */
    private suspend fun read(): String {
        val reassembler = RtpJpegReassembler()
        var received = 0L
        while (true) {
            val bytes = rtsp.readStream()
            if (bytes.isEmpty()) {
                trace()?.invoke("rtsp: the camera closed the stream after $received bytes")
                return "The camera ended its live picture."
            }

            if (received == 0L) {
                val first = bytes.take(16).toByteArray().toHexString(HexFormat.UpperCase)
                trace()?.invoke("rtsp: first stream bytes arrived: $first")
            }

            received += bytes.size
            for (frame in reassembler.push(bytes)) {
                if (RtpJpegReassembler.looksLikeJpeg(frame.jpeg)) {
                    latest.value = Item.Picture(frame, timeSource.markNow())
                } else {
                    trace()?.invoke("rtsp: a ${frame.jpeg.size}-byte frame was not a whole JPEG and was skipped")
                }
            }

            if (reassembler.packetsRead == 0 && received > NOISE_REPORTED_AFTER) {
                trace()?.invoke("rtsp: $received bytes arrived but none parsed as an RTP packet")
            }
        }
    }
}
