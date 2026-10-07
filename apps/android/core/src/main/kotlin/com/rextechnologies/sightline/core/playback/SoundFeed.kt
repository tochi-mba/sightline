package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.media.AviSound
import java.io.Closeable

/** A sound device that plays PCM in the order it is given, without gaps between pieces. */
interface SoundDevice : Closeable {
    /** How many bytes it holds that have not finished playing. */
    val holding: Long

    /** Plays [pcm] after everything it holds. */
    fun play(pcm: ByteArray)

    /** Goes quiet at once, dropping everything it holds. */
    fun stop()
}

/**
 * Plays a clip's sound as its player says, keeping the device about a second ahead and no further: a long clip's
 * sound is never all in memory, and a pause or a jump is heard at once.
 *
 * The device keeps its own time once it has the sound, which holds with the player's clock to well within a picture
 * over the length of a clip. So the sound is never nudged: it is dropped and started again from the right place
 * whenever the player stops, waits or jumps, which the player says.
 *
 * @param device Where the sound plays.
 * @param format The clip's sound, which says how many bytes a second of it takes.
 * @param read Reads so many bytes of the clip at an offset.
 */
class SoundFeed(private val device: SoundDevice, format: AviSound, private val read: (Long, Int) -> ByteArray) {
    private val ahead = format.sampleRate.toLong() * format.channels * (format.bitsPerSample / 8)
    private val waiting = ArrayDeque<SoundSlice>()
    private var sounding = false

    /** Follows one step of the player, which was left in [phase], then tops the device up. */
    fun follow(phase: PlayerPhase, step: PlayerStep) {
        if (sounding && (phase != PlayerPhase.Playing || step.soundRestarts)) {
            device.stop()
            waiting.clear()
            sounding = false
        }

        waiting.addAll(step.sound)
        while (device.holding < ahead && waiting.isNotEmpty()) {
            val next = waiting.removeFirst()
            device.play(read(next.offset, next.length))
            sounding = true
        }
    }
}
