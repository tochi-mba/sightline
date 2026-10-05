package com.rextechnologies.sightline.core.library

import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import java.io.IOException
import java.io.OutputStream

/**
 * What kind of file a download turned out to be, from its first bytes.
 *
 * The camera's file list says photo or video but names no container, so the file is recognised as it
 * arrives rather than guessed. The same three containers the Windows app recognises, and nothing is
 * claimed for anything else.
 *
 * @property extension What the saved file is named with.
 * @property mimeType What the phone's gallery is told it is.
 */
enum class MediaKind(val extension: String, val mimeType: String) {
    Jpeg(".jpg", "image/jpeg"),
    Avi(".avi", "video/x-msvideo"),
    Mp4(".mp4", "video/mp4"),
    Unknown(".bin", "application/octet-stream"),
    ;

    companion object {
        /** How many bytes [sniff] needs to tell every kind apart. */
        const val HEADER_LENGTH = 12

        /** The kind whose signature [header] starts with; [Unknown] when it is none of them or too short to tell. */
        fun sniff(header: ByteArray): MediaKind = when {
            header.startsWith(0, JPEG) -> Jpeg
            header.startsWith(0, RIFF) && header.startsWith(8, AVI) -> Avi
            header.startsWith(4, FTYP) -> Mp4
            else -> Unknown
        }

        private val JPEG = byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 0xFF.toByte())
        private val RIFF = "RIFF".toByteArray(Charsets.US_ASCII)
        private val AVI = "AVI ".toByteArray(Charsets.US_ASCII)
        private val FTYP = "ftyp".toByteArray(Charsets.US_ASCII)

        private fun ByteArray.startsWith(offset: Int, signature: ByteArray): Boolean =
            size >= offset + signature.size && signature.indices.all { this[offset + it] == signature[it] }
    }
}

/**
 * Where downloads from the camera's card are saved.
 *
 * On a phone, the shared gallery: the file is written as pending, invisible to other apps, and published
 * only once it is whole, so a cut-off download never shows up looking like a real photo.
 */
fun interface MediaSink {
    /** Starts saving [file], which [kind] says the bytes are. */
    fun create(file: CameraFile, kind: MediaKind): PendingMedia
}

/** A file being saved. Exactly one of [publish] and [discard] ends it. */
interface PendingMedia {
    /** Where the bytes are written. */
    val output: OutputStream

    /** Makes the whole file visible, and says where it went in words a person recognises. */
    fun publish(): String

    /** Throws away what was written. */
    fun discard()
}

/**
 * An output stream that holds back the first bytes of a download until it knows what they are, then
 * opens the real destination for that kind and passes everything through.
 *
 * Nothing is created for a download that fails before its first bytes, so a refused download leaves
 * nothing behind.
 */
internal class SniffingOutput(private val open: (MediaKind) -> OutputStream) : OutputStream() {
    private val header = ByteArray(MediaKind.HEADER_LENGTH)
    private var held = 0
    private var target: OutputStream? = null

    /** The kind the bytes turned out to be, once known. */
    var kind: MediaKind? = null
        private set

    override fun write(byte: Int) = write(byteArrayOf(byte.toByte()), 0, 1)

    override fun write(bytes: ByteArray, offset: Int, length: Int) {
        val opened = target
        if (opened != null) {
            saving { opened.write(bytes, offset, length) }
            return
        }

        val taken = minOf(length, header.size - held)
        bytes.copyInto(header, held, offset, offset + taken)
        held += taken
        if (held == header.size) {
            val opened = start()
            if (length > taken) {
                saving { opened.write(bytes, offset + taken, length - taken) }
            }
        }
    }

    /**
     * Opens the destination with whatever has been held back, even if that is short of a full header:
     * a file smaller than twelve bytes is still a file.
     */
    fun finish() {
        if (target == null && held > 0) {
            start()
        }
    }

    private fun start(): OutputStream {
        val recognised = MediaKind.sniff(header.copyOf(held))
        kind = recognised
        return saving {
            open(recognised).also { opened ->
                target = opened
                opened.write(header, 0, held)
            }
        }
    }
}

/**
 * The phone could not save a file: its storage refused it or is full.
 *
 * Kept apart from the camera's own failures, which arrive as [IOException]s too, because a full phone
 * is no reason to think the camera was lost and reconnect to it.
 */
class SaveFailure(cause: IOException) : Exception(cause.message ?: "its storage refused the file.", cause)

/** Runs [block], which writes to the phone's storage, turning its failures into [SaveFailure]. */
internal inline fun <T> saving(block: () -> T): T = try {
    block()
} catch (failure: IOException) {
    throw SaveFailure(failure)
}
