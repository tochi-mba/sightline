package com.rextechnologies.sightline.protocol.media

import com.rextechnologies.sightline.protocol.ByteQueue
import com.rextechnologies.sightline.protocol.JpegPadding
import com.rextechnologies.sightline.protocol.readUInt16LittleEndian
import com.rextechnologies.sightline.protocol.readUInt32LittleEndian
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * What an AVI clip holds, from its headers.
 *
 * @property width Pixels across.
 * @property height Pixels down.
 * @property framesPerSecond How many pictures a second it plays at.
 * @property totalFrames How many pictures it holds.
 * @property sound Its sound, or null when it has none.
 */
data class AviClip(
    val width: Int,
    val height: Int,
    val framesPerSecond: Double,
    val totalFrames: Int,
    val sound: AviSound?,
) {
    /** How long it plays for. */
    val duration: Duration
        get() = timeOf(totalFrames)

    /** When picture [number], counted from 0, is shown. */
    fun timeOf(number: Int): Duration = if (framesPerSecond > 0) (number / framesPerSecond).seconds else Duration.ZERO
}

/**
 * An AVI clip's sound: plain PCM, as the reference camera records it.
 *
 * @property sampleRate Samples a second.
 * @property channels How many channels.
 * @property bitsPerSample Bits in each sample.
 */
data class AviSound(val sampleRate: Int, val channels: Int, val bitsPerSample: Int)

/**
 * One piece of an AVI clip's content, in the order it is stored, with where in the file it lies: a player that
 * keeps a long clip on disk rather than in memory reads it back from there.
 *
 * @property offset Where its bytes start in the file.
 */
sealed class AviChunk(val offset: Long) {
    /** A picture: a whole JPEG, numbered from 0 in the order it is stored. */
    class Picture(val number: Int, offset: Long, val jpeg: ByteArray) : AviChunk(offset)

    /** A run of sound, as PCM samples. */
    class Sound(offset: Long, val pcm: ByteArray) : AviChunk(offset)
}

/** A file that is not an AVI, or one with a chunk in it that cannot be right. */
class AviFormatException(message: String) : Exception(message)

/**
 * Reads an AVI clip as its bytes arrive, so a clip can be shown while it is still coming off the card.
 *
 * The reference camera records Motion JPEG in AVI: every picture is a whole JPEG in a `00dc` chunk, and its
 * sound is 16-bit PCM in `01wb` chunks, inside the `movi` list. So nothing needs decoding to play it but the
 * JPEGs, which the apps already show for the live picture. A picture comes out without the zeros the camera pads
 * it with after its end, as in its stream; see [JpegPadding].
 *
 * Bytes go in whatever their boundaries, and a chunk comes out once all of it has arrived. Chunks are read in
 * the order they are stored: the header list is read whole, `movi` and `rec` lists are stepped into, a picture or
 * sound of a stream the header described is kept, and everything else, `idx1` and `JUNK` included, is passed
 * over without being held. So a clip of any length is read in the memory of its largest picture.
 */
class AviReader {
    private val buffer = ByteQueue()
    private val streams = HashMap<String, Boolean>()
    private var skipping = 0L
    private var consumed = 0L
    private var started = false
    private var pictures = 0

    /** The clip's headers, once they have arrived. */
    var clip: AviClip? = null
        private set

    /**
     * Takes the next [length] bytes of the file from [bytes] at [offset], and returns every picture and run of
     * sound they completed.
     *
     * @throws AviFormatException The file is not an AVI, or a chunk in it cannot be right.
     */
    fun push(bytes: ByteArray, offset: Int, length: Int): List<AviChunk> {
        val chunks = ArrayList<AviChunk>()
        var at = offset
        var left = length
        while (left > 0) {
            if (skipping > 0) {
                val skip = minOf(skipping, left.toLong()).toInt()
                skipping -= skip
                at += skip
                left -= skip
                continue
            }

            buffer.append(bytes, at, left)
            left = 0
            var stepped = true
            while (stepped) {
                stepped = step(chunks)
            }
        }

        return chunks
    }

    /** Takes the next step the buffered bytes allow, returning false when it needs more of them. */
    private fun step(chunks: MutableList<AviChunk>): Boolean {
        if (!started) {
            if (buffer.size < LIST_HEADER) {
                return false
            }

            if (code(0) != "RIFF" || code(CHUNK_HEADER) != "AVI ") {
                throw AviFormatException("This is not an AVI file.")
            }

            discard(LIST_HEADER.toLong())
            started = true
            return true
        }

        // Bytes still to pass over are never buffered, so a chunk header here is a whole one or none.
        if (buffer.size < CHUNK_HEADER) {
            return false
        }

        val code = code(0)
        val length = padded(buffer.array.readUInt32LittleEndian(4))
        if (code == "LIST") {
            if (buffer.size < LIST_HEADER) {
                return false
            }

            return when (code(CHUNK_HEADER)) {
                // The clip itself: its chunks are read one by one rather than kept whole.
                "movi", "rec " -> {
                    discard(LIST_HEADER.toLong())
                    true
                }
                "hdrl" -> keep(length) { _, body -> readHeaders(body) }
                else -> {
                    discard(CHUNK_HEADER + length)
                    true
                }
            }
        }

        val isVideo = streams[code.substring(0, 2)]
        val kind = code.substring(2)
        val ours = if (isVideo == true) kind == "dc" || kind == "db" else isVideo == false && kind == "wb"
        if (!ours) {
            discard(CHUNK_HEADER + length)
            return true
        }

        val unpadded = buffer.array.readUInt32LittleEndian(4).toInt()
        return keep(length) { offset, body ->
            chunks += if (isVideo == true) {
                AviChunk.Picture(pictures++, offset, body.copyOf(JpegPadding.end(body, unpadded)))
            } else {
                AviChunk.Sound(offset, body.copyOf(unpadded))
            }
        }
    }

    /** Hands over a chunk's body of [length] bytes, and where it starts, once all of it is here. */
    private fun keep(length: Long, take: (Long, ByteArray) -> Unit): Boolean {
        if (length > LARGEST_KEPT) {
            throw AviFormatException("A chunk of $length bytes is more than a clip of this camera holds.")
        }

        if (buffer.size < CHUNK_HEADER + length) {
            return false
        }

        val body = buffer.array.copyOfRange(CHUNK_HEADER, CHUNK_HEADER + length.toInt())
        val offset = consumed + CHUNK_HEADER
        discard(CHUNK_HEADER + length)
        take(offset, body)
        return true
    }

    /** Reads the header list, its type first: the main header, then each stream's header and format. */
    private fun readHeaders(hdrl: ByteArray) {
        var width = 0
        var height = 0
        var total = 0
        var perSecond = 0.0
        var sound: AviSound? = null
        var stream = 0
        for ((code, body) in children(hdrl)) {
            if (code == "avih" && body.size >= MAIN_HEADER) {
                val micros = body.readUInt32LittleEndian(0)
                perSecond = if (micros > 0) MICROS_PER_SECOND / micros else 0.0
                total = body.readUInt32LittleEndian(16).toInt()
                width = body.readUInt32LittleEndian(32).toInt()
                height = body.readUInt32LittleEndian(36).toInt()
            } else if (code == "LIST" && body.size >= 4 && ascii(body, 0) == "strl") {
                val described = readStream(body)
                val number = stream++.toString().padStart(2, '0')
                if (described.video) {
                    streams[number] = true
                    perSecond = described.rate ?: perSecond
                } else if (described.sound != null) {
                    streams[number] = false
                    sound = described.sound
                }
            }
        }

        clip = AviClip(width, height, perSecond, total, sound)
    }

    /**
     * One stream's header and format, its type first: whether it is video, its own rate, which is more exact than
     * the main header's, and its sound when it is PCM audio.
     */
    private fun readStream(strl: ByteArray): Stream {
        var type = ""
        var rate: Double? = null
        var sound: AviSound? = null
        for ((code, body) in children(strl)) {
            if (code == "strh" && body.size >= STREAM_HEADER) {
                type = ascii(body, 0)
                val scale = body.readUInt32LittleEndian(20)
                val perScale = body.readUInt32LittleEndian(24)
                rate = if (scale > 0 && perScale > 0) perScale.toDouble() / scale else null
            } else if (code == "strf" && type == "auds" && body.size >= SOUND_FORMAT &&
                body.readUInt16LittleEndian(0) == PCM
            ) {
                sound =
                    AviSound(
                        body.readUInt32LittleEndian(4).toInt(),
                        body.readUInt16LittleEndian(2),
                        body.readUInt16LittleEndian(14),
                    )
            }
        }

        val video = type == "vids"
        return Stream(video, if (video) rate else null, sound)
    }

    private fun code(at: Int): String = ascii(buffer.array, at)

    /** Lets go of [count] bytes: those buffered, then as many more as arrive. */
    private fun discard(count: Long) {
        val held = minOf(count, buffer.size.toLong()).toInt()
        buffer.removeFirst(held)
        skipping = count - held
        consumed += count
    }

    private class Stream(val video: Boolean, val rate: Double?, val sound: AviSound?)

    private companion object {
        const val CHUNK_HEADER = 8
        const val LIST_HEADER = 12
        const val MAIN_HEADER = 40
        const val STREAM_HEADER = 28
        const val SOUND_FORMAT = 16
        const val PCM = 1
        const val MICROS_PER_SECOND = 1_000_000.0

        // Bigger than any header list or picture this camera writes; a chunk claiming more is not believed.
        const val LARGEST_KEPT = 16L * 1024 * 1024

        fun ascii(bytes: ByteArray, at: Int): String = String(bytes, at, 4, Charsets.US_ASCII)

        /** The chunks inside a list's body, after its type, each cut off at the end of the body if it claims more. */
        fun children(list: ByteArray): List<Pair<String, ByteArray>> {
            val children = ArrayList<Pair<String, ByteArray>>()
            var at = 4
            while (at + CHUNK_HEADER <= list.size) {
                val code = ascii(list, at)
                val length = minOf(
                    list.readUInt32LittleEndian(at + 4),
                    (list.size - at - CHUNK_HEADER).toLong(),
                ).toInt()
                children += code to list.copyOfRange(at + CHUNK_HEADER, at + CHUNK_HEADER + length)
                at += CHUNK_HEADER + padded(length.toLong()).toInt()
            }

            return children
        }

        /** Chunks are stored at even lengths, a pad byte following an odd one. */
        fun padded(length: Long): Long = length + (length and 1)
    }
}
