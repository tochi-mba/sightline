package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import com.rextechnologies.sightline.protocol.media.AviFormatException
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import java.io.Closeable
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.io.OutputStream
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.time.format.DateTimeFormatter
import java.util.Locale
import java.util.UUID

/**
 * Clips played from the card, kept on the phone so playing one again needs no download.
 *
 * A clip being fetched is written to a `.part` file a player can read while it grows, and gets its real name only
 * once whole, so a clip cut off half-way is never taken for a whole one. A clip is known by its card name, its time
 * and its size, so a file the camera reuses an index for is not mistaken for the old one.
 *
 * The folder is kept under [limit] bytes by letting go of the clips played longest ago first; a clip bigger than the
 * limit on its own is still kept, alone. The files are this app's, in its own cache folder, which Android may also
 * empty when the phone runs short of space; nothing the person saved is ever touched.
 *
 * @param folder Where the clips are kept, made when the first clip arrives.
 * @param limit How many bytes of clips are kept at most.
 * @param io Where a kept clip is read back, away from the caller's thread.
 */
class ClipCache(
    val folder: File,
    val limit: Long = DEFAULT_LIMIT,
    private val io: CoroutineDispatcher = Dispatchers.IO,
) {
    init {
        require(limit > 0) { "A cache must have room for something." }
    }

    /** The clip kept for [file], read back in [scope]; null when none is kept. */
    fun open(file: CameraFile, scope: CoroutineScope): CardClip? {
        val path = pathFor(file)
        // Played again: it is the last to go. A clip that is not there cannot be touched.
        if (!path.setLastModified(System.currentTimeMillis())) {
            return null
        }

        val clip = CardClip(file)
        clip.arriving(path)
        val job = scope.launch(io) { readBack(clip, path) }
        clip.runs(job)
        job.invokeOnCompletion {
            if (clip.isStopped) {
                clip.stopped()
            }
        }
        return clip
    }

    /**
     * Starts keeping [file]: making room for it first, then a `.part` file to write it into, which a player may read
     * while it is written.
     *
     * @throws IOException The phone could not make the folder or the file.
     */
    fun start(file: CameraFile): PendingClip {
        val final = pathFor(file)
        if (!folder.isDirectory && !folder.mkdirs()) {
            throw IOException("Could not make the folder ${folder.path}.")
        }

        makeRoom(file.approximateBytes)
        val part = File(folder, ".${final.name}-${UUID.randomUUID()}.part")
        // Unbuffered, so every byte written is there for a reader at once.
        return PendingClip(part, final, FileOutputStream(part))
    }

    /** Where the whole clip for [file] is kept. */
    fun pathFor(file: CameraFile): File {
        val taken = file.taken?.format(TAKEN) ?: "undated"
        return File(folder, "${file.displayName}-$taken-${file.sizeKilobytes}k.avi")
    }

    /**
     * Lets go of the pieces of fetches that never finished, then of the clips played longest ago until [needed] more
     * bytes fit under the limit. A file that will not go is left for another time.
     */
    private fun makeRoom(needed: Long) {
        // The folder was made just now, so it lists.
        folder.listFiles { f -> f.name.endsWith(".part") }.forEach { it.delete() }
        val kept = folder.listFiles { f -> f.name.endsWith(".avi") }.sortedBy { it.lastModified() }
        var total = kept.sumOf { it.length() }
        for (oldest in kept) {
            if (total + needed <= limit) {
                break
            }

            // Read before deleting: a deleted file has no length to ask for.
            val length = oldest.length()
            if (oldest.delete()) {
                total -= length
            }
        }
    }

    internal companion object {
        /** How much the cache keeps unless told otherwise: two gigabytes, a few minutes of 1080p. */
        const val DEFAULT_LIMIT = 2L * 1024 * 1024 * 1024

        private const val READ_PIECE = 64 * 1024
        private val TAKEN = DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss", Locale.ROOT)

        /** Reads a kept clip into [clip], closing the file before saying how it ended. */
        suspend fun readBack(clip: CardClip, path: File) {
            try {
                clip.openRead().use { input ->
                    val buffer = ByteArray(READ_PIECE)
                    var read = input.read(buffer)
                    while (read >= 0) {
                        clip.reader.push(buffer, 0, read)
                        currentCoroutineContext().ensureActive()
                        read = input.read(buffer)
                    }
                }

                clip.reader.finish()
                clip.kept(path)
            } catch (damaged: AviFormatException) {
                // It was read whole before it was kept, so the storage has damaged it since. Letting it go means the
                // next play fetches it from the card again rather than failing here every time.
                path.delete()
                clip.failed(
                    "The copy kept on this phone was damaged, so it was thrown away. Play the clip again to fetch it from the card.",
                )
            } catch (unreadable: IOException) {
                clip.failed("The copy kept on this phone could not be read: ${unreadable.message}")
            }
        }
    }
}

/** A clip being written into the cache; exactly one of [keep] and [discard] ends it. */
class PendingClip internal constructor(
    /** Where the bytes are while it is written, which a player reads. */
    val path: File,
    private val final: File,
    private val stream: FileOutputStream,
) : Closeable {
    /** Where the bytes are written. */
    val output: OutputStream
        get() = stream

    /** Gives the whole clip its real name, and says where that is. */
    fun keep(): File {
        stream.close()
        Files.move(path.toPath(), final.toPath(), StandardCopyOption.REPLACE_EXISTING)
        return final
    }

    /** Throws away what was written; a piece that will not go now goes when the cache next makes room. */
    fun discard() {
        stream.close()
        path.delete()
    }

    override fun close() {
        stream.close()
    }
}
