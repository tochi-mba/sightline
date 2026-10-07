package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.FakeClip
import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds
import kotlin.time.DurationUnit

/** Playing a clip still coming off the card, on a clock the test turns. */
class ClipPlayerTest {
    // Two pictures and half a second of sound in each run, as the camera interleaves them.
    private val picture = ByteArray(100)
    private val halfSecond = ByteArray(16_000)

    private var clock = Duration.ZERO

    @Test
    fun `a clip that has all arrived plays at once each picture at its time and ends`() {
        val player = player(arrived(runs = 2))

        val first = player.step()
        assertEquals(PlayerPhase.Playing, player.phase)
        assertEquals(Duration.ZERO, first.picture!!.at)
        assertTrue(first.soundRestarts)
        assertEquals(2, first.sound.size)

        turn(0.1)
        assertNull(player.step().picture)
        turn(0.2)
        assertEquals(0.25.seconds, player.step().picture!!.at)
        turn(0.8)
        player.step()

        assertEquals(PlayerPhase.Ended, player.phase)
        assertEquals(1.seconds, player.position)
        assertEquals(1.seconds, player.duration)
    }

    @Test
    fun `played again after the end it starts from the beginning`() {
        val player = player(arrived(runs = 1))
        player.step()
        turn(1.0)
        player.step()
        assertEquals(PlayerPhase.Ended, player.phase)

        player.play()
        val step = player.step()

        assertEquals(PlayerPhase.Playing, player.phase)
        assertEquals(Duration.ZERO, player.position)
        assertEquals(Duration.ZERO, step.picture!!.at)
    }

    @Test
    fun `a clip arriving slower than it plays waits until the rest will arrive in time`() {
        // Six seconds of clip arriving in nine.
        val file = clip(runs = 12)
        val reader = ClipReader()
        val player = player(reader, file.size.toLong())
        var sent = feed(reader, file, upTo = 0)

        for (tick in 1..120) {
            clock = (tick * 0.1).seconds
            sent = feed(reader, file, upTo = (file.size * minOf(1.0, tick * 0.1 / 9)).toInt())
            player.step()
            if (player.phase == PlayerPhase.Playing) {
                break
            }
        }

        // The last of it is due at nine, a second before it is needed if playing starts at four, by when two and a
        // half seconds are ready.
        assertEquals(PlayerPhase.Playing, player.phase)
        assertTrue(abs(clock.toDouble(DurationUnit.SECONDS) - 4) <= 0.15, "started at $clock")
        assertEquals(2.5.seconds, reader.ready)
        assertTrue(sent < file.size)
    }

    @Test
    fun `a clip arriving at a third of its speed starts late enough never to wait again`() {
        // As the reference camera sent a 1080p clip: bytes steadily, at a third of the speed it plays, and so ready
        // in steps of a whole run of sound at a time. Stepped as the window's timer steps it.
        val file = clip(runs = 12)
        val reader = ClipReader()
        val player = player(reader, file.size.toLong())
        var waitedAgain = 0
        var started = Duration.ZERO
        var tick = 0
        while (tick <= 1500 && player.phase != PlayerPhase.Ended) {
            clock = (tick * 0.03).seconds
            feed(reader, file, upTo = (file.size * minOf(1.0, clock.toDouble(DurationUnit.SECONDS) / 18)).toInt())
            val before = player.phase
            player.step()
            if (before == PlayerPhase.Waiting && player.phase == PlayerPhase.Playing && started == Duration.ZERO) {
                started = clock
            }

            if (before == PlayerPhase.Playing && player.phase == PlayerPhase.Waiting) {
                waitedAgain++
            }

            tick++
        }

        assertEquals(PlayerPhase.Ended, player.phase)
        assertEquals(0, waitedAgain)
        // The last of it arrives at 18 seconds and it plays for six, so it cannot start before 12.
        assertTrue(started >= 12.seconds, "started at $started")
    }

    @Test
    fun `while it waits it says how long until it can play`() {
        val file = clip(runs = 12)
        val reader = ClipReader()
        val player = player(reader, file.size.toLong())
        feed(reader, file, upTo = file.size / 8)
        player.step()
        assertNull(player.startsIn)

        turn(1.5)
        feed(reader, file, upTo = file.size / 4)
        player.step()

        // An eighth of it in a second and a half: the rest takes nine seconds, and six of clip play in six.
        assertEquals(PlayerPhase.Waiting, player.phase)
        assertTrue(abs(player.startsIn!!.toDouble(DurationUnit.SECONDS) - 4) < 0.001, "starts in ${player.startsIn}")
    }

    @Test
    fun `a clip that stops arriving mid play waits rather than skips`() {
        val file = clip(runs = 4)
        val reader = ClipReader()
        val player = player(reader, file.size.toLong(), lead = 0.5.seconds)
        feed(reader, file, upTo = file.size / 2)
        player.step()
        turn(2.0)
        feed(reader, file, upTo = file.size * 7 / 8)
        player.step()
        assertEquals(PlayerPhase.Playing, player.phase)

        turn(5.0)
        player.step()

        assertEquals(PlayerPhase.Waiting, player.phase)
        assertEquals(reader.ready, player.position)
    }

    @Test
    fun `pausing holds the place and playing again carries on from it`() {
        val player = player(arrived(runs = 4))
        player.step()
        turn(0.5)
        player.pause()
        assertEquals(PlayerPhase.Paused, player.phase)
        assertEquals(0.5.seconds, player.position)

        turn(3.0)
        player.step()
        assertEquals(0.5.seconds, player.position)
        player.seek(0.25.seconds)
        assertEquals(PlayerPhase.Paused, player.phase)
        player.seek(0.5.seconds)

        player.play()
        val resumed = player.step()
        assertEquals(PlayerPhase.Playing, player.phase)
        assertTrue(resumed.soundRestarts)
        assertEquals(0.5.seconds, resumed.sound[0].at)
        turn(0.25)
        player.step()
        assertEquals(0.75.seconds, player.position)
    }

    @Test
    fun `play pressed while it plays changes nothing`() {
        val player = player(arrived(runs = 2))
        player.step()
        turn(0.5)

        player.play()
        player.step()

        assertEquals(PlayerPhase.Playing, player.phase)
        assertEquals(0.5.seconds, player.position)
    }

    @Test
    fun `before its headers arrive there is nothing to play or show`() {
        val player = player(ClipReader())

        val step = player.step()

        assertEquals(PlayerPhase.Waiting, player.phase)
        assertNull(step.picture)
        assertNull(player.startsIn)
    }

    @Test
    fun `a clip that has stopped arriving cannot say when it will play`() {
        val file = clip(runs = 12)
        val reader = ClipReader()
        val player = player(reader, file.size.toLong())
        feed(reader, file, upTo = file.size / 8)
        player.step()

        turn(2.0)
        player.step()

        assertEquals(PlayerPhase.Waiting, player.phase)
        assertNull(player.startsIn)
    }

    @Test
    fun `paused before it starts it does not start`() {
        val player = player(arrived(runs = 2))

        player.pause()
        player.step()

        assertEquals(PlayerPhase.Paused, player.phase)
    }

    @Test
    fun `a jump goes only as far as has arrived and starts the sound again there`() {
        val player = player(arrived(runs = 4))
        player.step()

        player.seek(1.25.seconds)
        val step = player.step()
        assertTrue(step.soundRestarts)
        assertEquals(1.25.seconds, step.sound[0].at)
        assertEquals(1.25.seconds, step.picture!!.at)

        player.seek(5.minutes)
        assertEquals(2.seconds, player.position)
        player.seek((-1).seconds)
        assertEquals(Duration.ZERO, player.position)
    }

    @Test
    fun `a jump back after the end plays on and a paused one stays paused`() {
        val player = player(arrived(runs = 1))
        player.step()
        turn(1.0)
        player.step()

        player.seek(0.25.seconds)
        assertEquals(PlayerPhase.Waiting, player.phase)
        assertNotNull(player.step().picture)
        assertEquals(PlayerPhase.Playing, player.phase)

        turn(1.0)
        player.step()
        player.pause()
        player.seek(Duration.ZERO)
        assertEquals(PlayerPhase.Paused, player.phase)
    }

    @Test
    fun `sound that arrives while playing is queued behind what is queued`() {
        // Arriving as fast as it plays, so playing starts with a second of it ready.
        val file = clip(runs = 6)
        val reader = ClipReader()
        val player = player(reader, file.size.toLong(), lead = 0.5.seconds)
        feed(reader, file, upTo = file.size / 3)
        player.step()
        turn(1.0)
        feed(reader, file, upTo = file.size * 2 / 3)
        val first = player.step()
        assertEquals(PlayerPhase.Playing, player.phase)
        assertTrue(first.soundRestarts)
        val queued = first.sound.size

        feed(reader, file, upTo = file.size)
        turn(0.1)
        val next = player.step()

        assertFalse(next.soundRestarts)
        assertEquals(6 - queued, next.sound.size)
        turn(0.1)
        assertTrue(player.step().sound.isEmpty())
    }

    @Test
    fun `a clip coming fast still waits until a lead of it is ready`() {
        // Ready half a second in, with most of the next run of sound in but not all of it: fast enough that the rest
        // is in time, and still too little ready to start on.
        val file = clip(runs = 12)
        val reader = ClipReader()
        val player = player(reader, file.size.toLong(), lead = 0.6.seconds)
        player.step()
        var sent = 0
        while (reader.ready < 0.5.seconds) {
            sent = feed(reader, file, upTo = sent + 100)
        }

        feed(reader, file, upTo = sent + 16_000)
        turn(1.0)
        player.step()

        assertEquals(PlayerPhase.Waiting, player.phase)
        assertNull(player.startsIn)
        assertEquals(0.5.seconds, reader.ready)
    }

    @Test
    fun `a player needs a clip of no fewer than no bytes`() {
        assertFailsWith<IllegalArgumentException> { ClipPlayer(ClipReader(), -1, { Duration.ZERO }) }
        assertEquals(Duration.ZERO, ClipPlayer(ClipReader(), 0, { Duration.ZERO }).duration)
    }

    /**
     * A clip of [runs] half-seconds, two pictures and a run of sound in each, whose header says as much: four pictures
     * a second, so its length by the header is its length by its sound.
     */
    private fun clip(runs: Int): ByteArray = FakeClip.reference(List(runs * 2) { picture }, List(runs) { halfSecond })
        .copy(picturesPerSound = 2, microsPerFrame = 250_000, rate = 4)
        .build()

    private fun arrived(runs: Int): ClipReader {
        val file = clip(runs)
        return ClipReader().apply {
            push(file, 0, file.size)
            finish()
        }
    }

    /** Sends the file on up to [upTo] bytes, finishing it once it is all sent. */
    private fun feed(reader: ClipReader, file: ByteArray, upTo: Int): Int {
        val from = reader.bytesRead.toInt()
        if (upTo > from) {
            reader.push(file, from, upTo - from)
            if (upTo == file.size) {
                reader.finish()
            }
        }

        return maxOf(from, upTo)
    }

    /** A player for a clip that has all arrived. */
    private fun player(reader: ClipReader): ClipPlayer = ClipPlayer(reader, reader.bytesRead, { clock })

    /** A player for a clip of [length] bytes, arriving. */
    private fun player(reader: ClipReader, length: Long, lead: Duration = 1.seconds): ClipPlayer =
        ClipPlayer(reader, length, { clock }, lead)

    private fun turn(seconds: Double) {
        clock += seconds.seconds
    }
}
