package com.rextechnologies.sightline.playback

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import com.rextechnologies.sightline.core.playback.SoundDevice
import com.rextechnologies.sightline.protocol.media.AviSound

/**
 * A clip's sound on the phone's speaker or headphones, through AudioTrack.
 *
 * Streamed into a buffer of two seconds, twice what the feed keeps ahead, so a write never waits: the timer that
 * steps the player must never block on the sound. What it holds is what was written less what the device has
 * played, which the device counts in frames from its last stop.
 */
class AudioTrackDevice private constructor(private val track: AudioTrack, private val frame: Int) : SoundDevice {
    private var written = 0L

    override val holding: Long
        get() = maxOf(0L, written - (track.playbackHeadPosition.toLong() and UNSIGNED) * frame)

    override fun play(pcm: ByteArray) {
        if (track.playState != AudioTrack.PLAYSTATE_PLAYING) {
            track.play()
        }

        // An error comes back as a negative count, and is nothing written.
        written += maxOf(0, track.write(pcm, 0, pcm.size, AudioTrack.WRITE_NON_BLOCKING))
    }

    override fun stop() {
        // Paused and flushed, which drops what was queued and counts the device's frames from nought again.
        track.pause()
        track.flush()
        written = 0
    }

    override fun close() {
        track.release()
    }

    companion object {
        // The device's count of frames played is an unsigned 32-bit number in a signed one.
        private const val UNSIGNED = 0xFFFF_FFFFL
        private const val BUFFER_SECONDS = 2

        /** The phone's sound, open for [sound]; null when the phone cannot play it. */
        fun open(sound: AviSound): SoundDevice? {
            val encoding = when (sound.bitsPerSample) {
                8 -> AudioFormat.ENCODING_PCM_8BIT
                16 -> AudioFormat.ENCODING_PCM_16BIT
                else -> return null
            }
            val channels = when (sound.channels) {
                1 -> AudioFormat.CHANNEL_OUT_MONO
                2 -> AudioFormat.CHANNEL_OUT_STEREO
                else -> return null
            }
            val frame = sound.channels * sound.bitsPerSample / 8
            return try {
                val track = AudioTrack.Builder()
                    .setAudioAttributes(
                        AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_MEDIA)
                            .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                            .build(),
                    )
                    .setAudioFormat(
                        AudioFormat.Builder()
                            .setSampleRate(sound.sampleRate)
                            .setEncoding(encoding)
                            .setChannelMask(channels)
                            .build(),
                    )
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setBufferSizeInBytes(sound.sampleRate * frame * BUFFER_SECONDS)
                    .build()
                AudioTrackDevice(track, frame)
            } catch (refused: RuntimeException) {
                // A rate the phone will not take, or no sound to be had: the clip plays silent.
                null
            }
        }
    }
}
