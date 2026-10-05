package com.rextechnologies.sightline.library

import android.content.ContentResolver
import android.content.ContentValues
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import com.rextechnologies.sightline.core.library.MediaKind
import com.rextechnologies.sightline.core.library.MediaSink
import com.rextechnologies.sightline.core.library.PendingMedia
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import java.io.BufferedOutputStream
import java.io.IOException
import java.io.OutputStream

/**
 * Saves copies from the camera's card into the phone's shared gallery, in a Sightline folder.
 *
 * Each file is written as pending, which hides it from other apps, and published only once it is whole:
 * a copy cut short by a lost camera never appears in somebody's photos looking like a real one. Photos go
 * to Pictures, videos to Movies, and anything the app does not recognise to Downloads, where nobody
 * mistakes it for a picture.
 *
 * No storage permission is needed: from Android 10 an app may add to the shared collections freely.
 *
 * The file is not dated from the camera's clock. The reference camera's clock was found two years out, so
 * a gallery sorted by it would bury today's copies in the past; the date the copy was made is honest.
 */
class GallerySink(private val resolver: ContentResolver, private val folder: String = FOLDER) : MediaSink {
    override fun create(file: CameraFile, kind: MediaKind): PendingMedia = start(file.displayName, kind)

    /**
     * Saves [bytes], already whole, as [stem] with [kind]'s extension: a Sentry snapshot, say.
     *
     * @return Where it went, in words a person recognises.
     * @throws java.io.IOException The gallery would not take it; nothing is left behind.
     */
    fun saveWhole(stem: String, kind: MediaKind, bytes: ByteArray): String {
        val pending = start(stem, kind)
        try {
            pending.output.write(bytes)
            pending.output.close()
        } catch (failure: IOException) {
            pending.discard()
            throw failure
        }

        return pending.publish()
    }

    private fun start(stem: String, kind: MediaKind): PendingMedia {
        val (collection, directory) = placeFor(kind)
        val name = stem + kind.extension
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, name)
            put(MediaStore.MediaColumns.MIME_TYPE, kind.mimeType)
            put(MediaStore.MediaColumns.RELATIVE_PATH, "$directory/$folder")
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }
        val uri = resolver.insert(collection, values) ?: throw IOException("The gallery would not take $name.")
        val output = try {
            resolver.openOutputStream(uri) ?: throw IOException("The gallery gave nowhere to write $name.")
        } catch (failure: IOException) {
            resolver.delete(uri, null, null)
            throw failure
        }

        return Pending(uri, BufferedOutputStream(output, BUFFER_BYTES), "$directory/$folder/$name")
    }

    private inner class Pending(
        private val uri: Uri,
        override val output: OutputStream,
        private val where: String,
    ) : PendingMedia {
        override fun publish(): String {
            resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
            return where
        }

        override fun discard() {
            try {
                output.close()
            } finally {
                resolver.delete(uri, null, null)
            }
        }
    }

    companion object {
        /** The folder copies go in, inside Pictures, Movies or Downloads. */
        const val FOLDER = "Sightline"

        /** How much is gathered before each write to storage; a download frame is up to 60 KB. */
        private const val BUFFER_BYTES = 64 * 1024

        /** Which collection and which top-level folder a file of [kind] belongs in. */
        fun placeFor(kind: MediaKind): Pair<Uri, String> = when (kind) {
            MediaKind.Jpeg -> MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY) to
                Environment.DIRECTORY_PICTURES
            MediaKind.Avi, MediaKind.Mp4 -> MediaStore.Video.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY) to
                Environment.DIRECTORY_MOVIES
            MediaKind.Unknown -> MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY) to
                Environment.DIRECTORY_DOWNLOADS
        }
    }
}
