package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.media.AviSound
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.time.Duration

/** A clip's sound, fed to the device a second ahead, and dropped the moment the player stops or jumps. */
class SoundFeedTest {
    // 16 kHz mono 16-bit, as the reference camera records: 32,000 bytes a second.
    private val format = AviSound(16_000, 1, 16)
    private val device = FakeDevice()
    private val reads = mutableListOf<Pair<Long, Int>>()

    private fun feed() = SoundFeed(device, format) { offset, length ->
        reads += offset to length
        ByteArray(length)
    }

    private fun step(restarts: Boolean, vararg slices: Pair<Long, Int>) =
        PlayerStep(null, slices.map { SoundSlice(Duration.ZERO, it.first, it.second) }, restarts)

    @Test
    fun `sound is read from the clip and played in order a second ahead and no further`() {
        val feed = feed()

        feed.follow(PlayerPhase.Playing, step(true, 100L to 16_000, 20_000L to 16_000, 40_000L to 16_000))

        assertEquals(listOf(100L to 16_000, 20_000L to 16_000), reads)
        assertEquals(2, device.played.size)
        assertEquals(0, device.stops)

        device.finish(1)
        feed.follow(PlayerPhase.Playing, step(false, 60_000L to 16_000))

        assertEquals(listOf(100L to 16_000, 20_000L to 16_000, 40_000L to 16_000), reads)
        device.finish(2)
        feed.follow(PlayerPhase.Playing, step(false))
        assertEquals(60_000L to 16_000, reads.last())
    }

    @Test
    fun `a jump drops what was held and what waited and plays from the new place`() {
        val feed = feed()
        feed.follow(PlayerPhase.Playing, step(true, 100L to 16_000, 20_000L to 16_000, 40_000L to 16_000))

        feed.follow(PlayerPhase.Playing, step(true, 90_000L to 8_000))

        assertEquals(1, device.stops)
        assertEquals(90_000L to 8_000, reads.last())
        assertEquals(8_000L, device.holding)
    }

    @Test
    fun `sound stops the moment the player does and not again until it plays`() {
        val feed = feed()
        feed.follow(PlayerPhase.Waiting, step(false))
        assertEquals(0, device.stops)
        feed.follow(PlayerPhase.Playing, step(true, 100L to 16_000, 20_000L to 16_000, 40_000L to 16_000))

        feed.follow(PlayerPhase.Paused, step(false))
        feed.follow(PlayerPhase.Paused, step(false))

        assertEquals(1, device.stops)
        assertEquals(0L, device.holding)
        assertEquals(2, reads.size)
        device.close()
        assertTrue(device.closed)
    }

    private class FakeDevice : SoundDevice {
        val played = mutableListOf<ByteArray>()
        var stops = 0
        var closed = false

        override val holding: Long
            get() = played.sumOf { it.size.toLong() }

        override fun play(pcm: ByteArray) {
            played += pcm
        }

        override fun stop() {
            stops++
            played.clear()
        }

        /** The first [count] pieces finish playing. */
        fun finish(count: Int) {
            repeat(count) { played.removeAt(0) }
        }

        override fun close() {
            closed = true
        }
    }
}
