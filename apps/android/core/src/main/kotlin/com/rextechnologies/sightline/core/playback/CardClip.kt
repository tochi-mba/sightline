package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Job
import java.io.Closeable
import java.io.File
import java.io.RandomAccessFile

/** How a clip from the card stopped arriving. */
sealed interface ClipArrival {
    /** All of it arrived, and it is kept at [where]. */
    data class Kept(val where: File) : ClipArrival

    /** It stopped arriving part-way, because of [reason]. */
    data class Failed(val reason: String) : ClipArrival

    /** The player let it go before all of it had arrived. */
    data object Stopped : ClipArrival
}

/**
 * A video from the card, arriving for a player: read and timed as it comes, and kept on the phone once whole.
 *
 * The bytes go to a file rather than memory, a long clip being far bigger than a picture: [openRead] opens it for the
 * player, which reads each picture from where [reader] says it lies, while the rest is still being written. The
 * player closes the clip when it is done with it, which stops whatever still fetches or reads it.
 */
class CardClip internal constructor(
    /** The video on the card. */
    val file: CameraFile,
) : Closeable {
    private val lock = Any()
    private val ending = CompletableDeferred<ClipArrival>()
    private var stopped = false
    private var work: Job? = null

    @Volatile
    private var path: File? = null

    /** The clip as it arrives: its headers, and when each picture and run of sound plays. */
    val reader = ClipReader()

    /** Completes once the clip stops arriving: all of it kept, or why not. */
    val arrival: Deferred<ClipArrival>
        get() = ending

    /** Whether the clip's bytes have started arriving, so [openRead] has something to open. */
    val hasBytes: Boolean
        get() = path != null

    /** Whether the clip has been let go. */
    internal val isStopped: Boolean
        get() = synchronized(lock) { stopped }

    /**
     * Opens the clip's bytes for reading, as far as they have arrived and on as they come.
     *
     * @throws IllegalStateException No bytes have arrived yet.
     */
    fun openRead(): RandomAccessFile = RandomAccessFile(
        checkNotNull(path) {
            "The clip has not started arriving."
        },
        "r",
    )

    /** Lets the clip go: if it is still arriving that stops, and what had arrived is thrown away. A kept clip stays kept. */
    override fun close() {
        val running = synchronized(lock) {
            stopped = true
            work
        }
        running?.cancel()
    }

    /** Says [job] is what fetches or reads the clip, so letting the clip go stops it, even before it starts. */
    internal fun runs(job: Job) {
        val stop = synchronized(lock) {
            work = job
            stopped
        }
        if (stop) {
            job.cancel()
        }
    }

    internal fun arriving(at: File) {
        path = at
    }

    internal fun kept(at: File) {
        path = at
        ending.complete(ClipArrival.Kept(at))
    }

    internal fun failed(reason: String) {
        ending.complete(ClipArrival.Failed(reason))
    }

    internal fun stopped() {
        ending.complete(ClipArrival.Stopped)
    }
}
