package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.media.AviClip
import com.rextechnologies.sightline.protocol.media.AviFormatException
import com.rextechnologies.sightline.protocol.media.AviReader
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.DurationUnit

/**
 * A stretch of a clip's sound to play: when it starts, and where its samples lie in the file.
 *
 * @property at When it starts, from the start of the clip.
 * @property offset Where the samples start in the file.
 * @property length How many bytes of samples.
 */
data class SoundSlice(val at: Duration, val offset: Long, val length: Int)

/** A picture of a clip with its number, counted from 0 in the order the pictures show. */
data class NumberedPicture(val number: Int, val picture: TimedPicture)

/** Stretches of a clip's sound, and how many runs of it have arrived, which is where to carry on from. */
data class SoundRuns(val slices: List<SoundSlice>, val runs: Int)

/**
 * A clip coming off the card, read and timed as its bytes arrive, for a player to play while the rest is still on
 * its way.
 *
 * The download writes into this from one thread while a player reads it from another, so every member takes the
 * same lock. What the player needs back is small: where a picture lies in the file, not the picture.
 */
class ClipReader {
    private val lock = Any()
    private val avi = AviReader()
    private var timeline: ClipTimeline? = null
    private var read = 0L

    /** The clip's headers, once they have arrived. */
    val clip: AviClip?
        get() = synchronized(lock) { timeline?.clip }

    /** How many bytes have arrived. */
    val bytesRead: Long
        get() = synchronized(lock) { read }

    /** How far into the clip everything has arrived and been timed. */
    val ready: Duration
        get() = synchronized(lock) { timeline?.ready ?: Duration.ZERO }

    /** Whether the whole clip has arrived. */
    val isComplete: Boolean
        get() = synchronized(lock) { timeline?.isComplete == true }

    /**
     * Takes the next [length] bytes of the file from [bytes] at [offset].
     *
     * @throws AviFormatException The file is not a clip this can play.
     */
    fun push(bytes: ByteArray, offset: Int, length: Int) {
        synchronized(lock) {
            read += length
            val chunks = avi.push(bytes, offset, length)
            // A picture or a run of sound comes only after the headers that say what it is.
            val headers = avi.clip ?: return
            val timed = timeline ?: ClipTimeline(headers).also { timeline = it }
            chunks.forEach(timed::add)
        }
    }

    /**
     * Says the whole file has arrived.
     *
     * @throws AviFormatException It ended before its headers did.
     */
    fun finish() {
        synchronized(lock) {
            (timeline ?: throw AviFormatException("The clip ended before its headers did.")).finish()
        }
    }

    /** The picture showing at [position], with its number, or null before the first. */
    fun pictureAt(position: Duration): NumberedPicture? = synchronized(lock) {
        val timed = timeline ?: return null
        val number = timed.pictureAt(position)
        if (number < 0) null else NumberedPicture(number, timed.pictures[number])
    }

    /**
     * The sound from [position] on, as far as it has arrived: the rest of the run playing at that moment, then every
     * run after it; and how many runs have arrived, which is where to carry on from.
     */
    fun soundFrom(position: Duration): SoundRuns = synchronized(lock) {
        val timed = timeline ?: return SoundRuns(emptyList(), 0)
        val sound = timed.clip.sound ?: return SoundRuns(emptyList(), 0)
        val frame = sound.channels * (sound.bitsPerSample / 8)
        val bytesPerSecond = sound.sampleRate * frame
        val slices = ArrayList<SoundSlice>()
        for (run in timed.sounds) {
            val end = run.at + (run.length.toDouble() / bytesPerSecond).seconds
            if (end <= position) {
                continue
            }

            // Into the run by whole sample frames, so the samples stay aligned.
            val into = if (run.at >= position) 0.0 else (position - run.at).toDouble(DurationUnit.SECONDS)
            val skip = (into * bytesPerSecond).toInt() / frame * frame
            slices +=
                SoundSlice(run.at + (skip.toDouble() / bytesPerSecond).seconds, run.offset + skip, run.length - skip)
        }

        SoundRuns(slices, timed.sounds.size)
    }

    /** Every whole run of sound from run number [first] on, and how many runs have arrived. */
    fun soundAfter(first: Int): SoundRuns = synchronized(lock) {
        val runs = timeline?.sounds.orEmpty()
        SoundRuns(runs.drop(first).map { SoundSlice(it.at, it.offset, it.length) }, runs.size)
    }
}
