package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.media.AviChunk
import com.rextechnologies.sightline.protocol.media.AviClip
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * One picture of a clip: when it shows, and where its JPEG lies in the file.
 *
 * @property at When it shows, from the start of the clip.
 * @property offset Where its bytes start in the file.
 * @property length How many bytes it is.
 */
data class TimedPicture(val at: Duration, val offset: Long, val length: Int)

/**
 * One run of a clip's sound: when it starts, and where its samples lie in the file.
 *
 * @property at When it starts, from the start of the clip.
 * @property offset Where its bytes start in the file.
 * @property length How many bytes it is.
 */
data class TimedSound(val at: Duration, val offset: Long, val length: Int)

/**
 * When each picture of a clip is shown, worked out as the clip arrives off the card.
 *
 * A clip's header says 30 pictures a second, but the reference camera takes about 25 and makes up the rest by
 * repeating some, which only the index at the very end of the file says (measured 2026-10-07: 122 pictures
 * filling 149 places). A clip still arriving has no index yet, so its sound keeps time instead. The camera writes
 * a run of sound every half second, after the pictures it took during that run, so the pictures between two runs
 * of sound are spread evenly across the second run.
 *
 * Pictures after the last run of sound are spread at the rate the clip kept until then, once the clip is known to
 * have ended. A clip with no sound, or sound that is not plain PCM, is timed by its header's rate.
 */
class ClipTimeline(val clip: AviClip) {
    private val soundBytesPerSecond = clip.sound?.let { it.sampleRate * it.channels * (it.bitsPerSample / 8.0) } ?: 0.0
    private val timedPictures = ArrayList<TimedPicture>()
    private val timedSounds = ArrayList<TimedSound>()
    private val waiting = ArrayList<AviChunk.Picture>()
    private var soundTime = Duration.ZERO

    /** Every picture timed so far, in the order they show. */
    val pictures: List<TimedPicture>
        get() = timedPictures

    /** Every run of sound timed so far, in the order they play. */
    val sounds: List<TimedSound>
        get() = timedSounds

    /** How far into the clip everything is timed: playing can go this far without waiting. */
    var ready: Duration = Duration.ZERO
        private set

    /** Whether the whole clip has arrived and been timed. */
    var isComplete: Boolean = false
        private set

    /** Times the next piece of the clip, in the order it is stored. */
    fun add(chunk: AviChunk) {
        if (chunk is AviChunk.Picture) {
            if (soundBytesPerSecond > 0) {
                waiting += chunk
            } else {
                timedPictures += TimedPicture(clip.timeOf(timedPictures.size), chunk.offset, chunk.jpeg.size)
                ready = clip.timeOf(timedPictures.size)
            }
        } else if (soundBytesPerSecond > 0) {
            val sound = chunk as AviChunk.Sound
            val length = (sound.pcm.size / soundBytesPerSecond).seconds
            spread(soundTime, length)
            timedSounds += TimedSound(soundTime, sound.offset, sound.pcm.size)
            soundTime += length
            ready = soundTime
        }
    }

    /** Says the clip has ended, timing the pictures after its last run of sound. */
    fun finish() {
        if (waiting.isNotEmpty()) {
            val each = if (timedPictures.isNotEmpty()) soundTime / timedPictures.size else clip.timeOf(1)
            val length = each * waiting.size
            spread(soundTime, length)
            ready = soundTime + length
        }

        isComplete = true
    }

    /** The number of the picture showing at [position]: the last one due by then, or -1 before the first. */
    fun pictureAt(position: Duration): Int {
        var low = 0
        var high = timedPictures.size - 1
        var found = -1
        while (low <= high) {
            val middle = (low + high) / 2
            if (timedPictures[middle].at <= position) {
                found = middle
                low = middle + 1
            } else {
                high = middle - 1
            }
        }

        return found
    }

    /** Spreads the pictures waiting for a time evenly from [from] over [length]. */
    private fun spread(from: Duration, length: Duration) {
        waiting.forEachIndexed { i, picture ->
            timedPictures += TimedPicture(from + length * i / waiting.size, picture.offset, picture.jpeg.size)
        }

        waiting.clear()
    }
}
