package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.media.AviChunk
import com.rextechnologies.sightline.protocol.media.AviClip
import com.rextechnologies.sightline.protocol.media.AviSound
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/** When each picture of a clip shows, worked out as the clip arrives, against clips shaped like the reference camera's. */
class ClipTimelineTest {
    private val withSound = AviClip(1920, 1080, 30.0, 149, AviSound(16_000, 1, 16))
    private val silent = AviClip(1920, 1080, 30.0, 149, null)

    @Test
    fun `pictures are spread over the run of sound written after them`() {
        val timeline = ClipTimeline(withSound)

        timeline.add(picture(100))
        timeline.add(picture(200))
        assertTrue(timeline.pictures.isEmpty())
        timeline.add(sound(300, 32_000))

        assertEquals(
            listOf(TimedPicture(Duration.ZERO, 100, 10), TimedPicture(0.5.seconds, 200, 10)),
            timeline.pictures,
        )
        assertEquals(listOf(100L to 10, 200L to 10), timeline.pictures.map { it.offset to it.length })
        assertEquals(listOf(TimedSound(Duration.ZERO, 300, 32_000)), timeline.sounds)
        assertEquals(1.seconds, timeline.ready)
        assertFalse(timeline.isComplete)
    }

    @Test
    fun `each run takes its own rate as the reference camera writes them`() {
        // 10 pictures, then half a second of sound (16,376 bytes at 16 kHz mono), then 13 more and another.
        val timeline = ClipTimeline(withSound)
        repeat(10) { timeline.add(picture(0)) }
        timeline.add(sound(0, 16_376))
        repeat(13) { timeline.add(picture(0)) }
        timeline.add(sound(0, 16_376))

        val run = (16_376 / 32_000.0).seconds
        assertEquals(run * 9 / 10, timeline.pictures[9].at)
        assertEquals(run, timeline.pictures[10].at)
        assertEquals(run + run * 12 / 13, timeline.pictures[22].at)
        assertEquals(run * 2, timeline.ready)
    }

    @Test
    fun `pictures after the last run of sound keep the rate the clip kept until then`() {
        val timeline = ClipTimeline(withSound)
        repeat(5) { timeline.add(picture(0)) }
        timeline.add(sound(0, 48_000))
        timeline.add(picture(500))
        timeline.add(picture(600))

        timeline.finish()

        assertEquals(1.5.seconds, timeline.pictures[5].at)
        assertEquals(1.8.seconds, timeline.pictures[6].at)
        assertEquals(2.1.seconds, timeline.ready)
        assertTrue(timeline.isComplete)
    }

    @Test
    fun `pictures with no sound before them take the headers rate when the clip ends`() {
        val timeline = ClipTimeline(withSound)
        timeline.add(sound(0, 32_000))
        timeline.add(picture(0))
        timeline.add(picture(0))

        timeline.finish()

        assertEquals((1 + 1 / 30.0).seconds, timeline.pictures[1].at)
    }

    @Test
    fun `a clip that ends with nothing waiting is complete as it stands`() {
        val timeline = ClipTimeline(withSound)
        timeline.add(picture(0))
        timeline.add(sound(0, 32_000))

        timeline.finish()

        assertEquals(1.seconds, timeline.ready)
        assertTrue(timeline.isComplete)
    }

    @Test
    fun `a clip with no sound is timed by its headers rate as each picture arrives`() {
        val timeline = ClipTimeline(silent)

        timeline.add(picture(0))
        timeline.add(picture(0))
        timeline.add(sound(0, 32_000))

        assertEquals(listOf(Duration.ZERO, (1 / 30.0).seconds), timeline.pictures.map { it.at })
        assertTrue(timeline.sounds.isEmpty())
        assertEquals((2 / 30.0).seconds, timeline.ready)
        assertEquals(silent, timeline.clip)
    }

    @Test
    fun `the picture showing at a moment is the last one due by then`() {
        val timeline = ClipTimeline(silent)
        repeat(4) { timeline.add(picture(0)) }

        assertEquals(-1, timeline.pictureAt(-1.milliseconds))
        assertEquals(0, timeline.pictureAt(Duration.ZERO))
        assertEquals(2, timeline.pictureAt(silent.timeOf(2)))
        assertEquals(2, timeline.pictureAt((2.5 / 30).seconds))
        assertEquals(3, timeline.pictureAt(1.minutes))
        assertEquals(-1, ClipTimeline(silent).pictureAt(Duration.ZERO))
    }

    private var number = 0

    private fun picture(offset: Long) = AviChunk.Picture(number++, offset, ByteArray(10))

    private fun sound(offset: Long, length: Int) = AviChunk.Sound(offset, ByteArray(length))
}
