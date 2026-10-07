package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.FakeClip
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import com.rextechnologies.sightline.protocol.media.AviSound
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Rule
import org.junit.rules.TemporaryFolder
import java.io.File
import java.time.LocalDateTime
import kotlin.coroutines.CoroutineContext
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/** A clip playing on the phone, stepped on the test's virtual clock, its pictures "decoded" to what they say. */
class ClipSessionTest {
    @get:Rule
    val temp = TemporaryFolder()

    private val video = CameraFile('A', 7, LocalDateTime.of(2026, 10, 7, 9, 30), 40)
    private val sound = RecordedSound()
    private val decoded = mutableListOf<Int>()

    /**
     * Four pictures at four a second, each a different length, with half a second of sound after every two; or, with
     * no sound, a header that says its sound is not plain PCM, which a phone cannot play.
     */
    private fun clip(withSound: Boolean = true): ByteArray = FakeClip.reference(
        List(4) { FakeRtspCamera.jpeg(300 + it) },
        if (withSound) List(2) { ByteArray(16_000) } else emptyList(),
    ).copy(microsPerFrame = 250_000, rate = 4, picturesPerSound = 2, soundFormat = if (withSound) 1 else 0x55).build()

    /** [bytes] kept on the phone as [file], and played from there. */
    private suspend fun TestScope.kept(bytes: ByteArray, file: CameraFile = video): CardClip {
        val cache = ClipCache(File(temp.root, "clips"), io = StandardTestDispatcher(testScheduler))
        cache.start(file).use {
            it.output.write(bytes)
            it.keep()
        }
        return cache.open(file, this)!!.also { assertIs<ClipArrival.Kept>(it.arrival.await()) }
    }

    private fun TestScope.session(
        clip: CardClip,
        decoding: CoroutineDispatcher = StandardTestDispatcher(testScheduler),
        decode: (ByteArray) -> String? = { "picture of ${it.size}" },
        openSound: (AviSound) -> SoundDevice? = { sound },
    ) = ClipSession(
        clip,
        backgroundScope,
        { testScheduler.currentTime.milliseconds },
        { jpeg ->
            decoded += jpeg.size
            decode(jpeg)
        },
        decoding,
        openSound,
    )

    @Test
    fun `a kept clip plays its pictures and sound from the start to the end`() = runTest {
        val session = session(kept(clip()))
        runCurrent()

        val first = session.view.value
        assertEquals("picture of 300", first.picture)
        assertEquals(PlayerPhase.Playing, first.phase)
        assertEquals(1.seconds, first.duration)
        assertEquals(Triple(1920, 1080, 4.0), Triple(first.width, first.height, first.framesPerSecond))
        assertNull(first.startsIn)
        assertNull(first.failure)
        assertEquals(2, sound.played.size)

        advanceTimeBy(600.milliseconds)
        runCurrent()
        assertEquals("picture of 302", session.view.value.picture)
        advanceTimeBy(600.milliseconds)
        runCurrent()
        assertEquals(PlayerPhase.Ended, session.view.value.phase)
        assertEquals(1, sound.stops)

        // Played again, from the start.
        session.playPause()
        assertEquals(PlayerPhase.Playing, session.view.value.phase)
        assertEquals(Duration.ZERO, session.view.value.position)
    }

    @Test
    fun `pausing holds the picture and silences it and a jump goes where it is dropped`() = runTest {
        val session = session(kept(clip()))
        runCurrent()

        session.playPause()
        advanceTimeBy(500.milliseconds)
        runCurrent()

        assertEquals(PlayerPhase.Paused, session.view.value.phase)
        assertEquals(Duration.ZERO, session.view.value.position)
        assertEquals(1, sound.stops)

        session.seek(0.75.seconds)
        runCurrent()

        assertEquals("picture of 303", session.view.value.picture)
        assertEquals(0.75.seconds, session.view.value.position)

        // Played on from where it was dropped.
        session.playPause()
        advanceTimeBy(100.milliseconds)
        runCurrent()
        assertEquals(PlayerPhase.Playing, session.view.value.phase)
        assertTrue(session.view.value.position > 0.75.seconds)
    }

    @Test
    fun `sent behind something else it pauses, and only once`() = runTest {
        val session = session(kept(clip()))
        runCurrent()

        session.pause()
        session.pause()
        advanceTimeBy(500.milliseconds)
        runCurrent()

        assertEquals(PlayerPhase.Paused, session.view.value.phase)
        assertEquals(Duration.ZERO, session.view.value.position)
        assertEquals(1, sound.stops)
        val waiting = session(CardClip(video.copy(index = 9)))
        runCurrent()
        waiting.pause()
        assertEquals(PlayerPhase.Paused, waiting.view.value.phase)
    }

    @Test
    fun `pictures falling due while one decodes wait and only the newest is decoded`() = runTest {
        val held = HeldDispatcher()
        val session = session(kept(clip()), decoding = held)
        runCurrent()

        // The first decodes; the second and the third fall due before it is back, and only the third waits.
        advanceTimeBy(300.milliseconds)
        runCurrent()
        advanceTimeBy(300.milliseconds)
        runCurrent()
        held.release()
        runCurrent()
        held.release()
        runCurrent()

        assertEquals(listOf(300, 302), decoded)
        assertEquals("picture of 302", session.view.value.picture)
    }

    @Test
    fun `closed while a picture decodes it shows nothing more and decodes nothing more`() = runTest {
        val held = HeldDispatcher()
        val clip = kept(clip())
        val session = session(clip, decoding = held)
        runCurrent()
        advanceTimeBy(300.milliseconds)
        runCurrent()

        session.close()
        session.close()
        held.release()
        runCurrent()

        assertNull(session.view.value.picture)
        assertEquals(listOf(300), decoded)
        assertTrue(clip.isStopped)
        assertTrue(sound.closed)
    }

    @Test
    fun `a picture that does not decode leaves none showing`() = runTest {
        val session = session(kept(clip()), decode = { null })
        runCurrent()

        assertNull(session.view.value.picture)
        assertEquals(PlayerPhase.Playing, session.view.value.phase)
    }

    @Test
    fun `a clip without sound or a phone without a sound device plays silent`() = runTest {
        val opened = mutableListOf<AviSound>()
        val silent = session(kept(clip(withSound = false))) {
            opened += it
            sound
        }
        runCurrent()
        assertTrue(opened.isEmpty())

        val unheard = session(kept(clip(), video.copy(index = 8))) { null }
        runCurrent()

        assertEquals(PlayerPhase.Playing, silent.view.value.phase)
        assertEquals(PlayerPhase.Playing, unheard.view.value.phase)
        assertTrue(sound.played.isEmpty())
        silent.close()
        assertTrue(silent.clip.isStopped)
    }

    @Test
    fun `a clip the phone cannot read stops and says so`() = runTest {
        val clip = kept(clip())
        File(temp.root, "clips").listFiles()!!.forEach { it.delete() }
        val session = session(clip)

        runCurrent()

        val failure = session.view.value.failure
        assertTrue(failure!!.startsWith("This phone could not read the clip: "), failure)
        session.playPause()
        advanceTimeBy(1.seconds)
        runCurrent()
        assertNull(session.view.value.picture)
    }

    @Test
    fun `a clip that stopped arriving says why`() = runTest {
        val clip = CardClip(video)
        val session = session(clip)

        clip.failed("The camera said no: the card is full.")
        runCurrent()

        assertEquals("The camera said no: the card is full.", session.view.value.failure)
        assertEquals(PlayerPhase.Waiting, session.view.value.phase)
        session.close()
        assertTrue(clip.isStopped)
    }

    /** A dispatcher that runs what it is given only when the test says, as a slow decoder would. */
    private class HeldDispatcher : CoroutineDispatcher() {
        private val held = ArrayDeque<Runnable>()

        override fun dispatch(context: CoroutineContext, block: Runnable) {
            held += block
        }

        fun release() {
            while (held.isNotEmpty()) {
                held.removeFirst().run()
            }
        }
    }

    /** A sound device that keeps what it was given and plays none of it. */
    private class RecordedSound : SoundDevice {
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

        override fun close() {
            closed = true
        }
    }
}
