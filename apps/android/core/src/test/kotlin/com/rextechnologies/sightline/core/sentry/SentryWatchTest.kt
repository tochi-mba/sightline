package com.rextechnologies.sightline.core.sentry

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TestTimeSource

/** Sentry's watch: arming, raising the alarm on sustained motion, and standing down. */
class SentryWatchTest {
    private val clock = TestTimeSource()
    private var movingFrame = 0

    private val still = LumaGrid(10, 10, IntArray(100) { 100 })

    /** A frame with something moving: a bright square in a different place each time. */
    private fun moving(): LumaGrid {
        val at = (movingFrame++ % 2) * 5
        return LumaGrid(10, 10, IntArray(100) { if (it % 10 in at until at + 4 && it / 10 < 4) 250 else 100 })
    }

    private fun watch(settings: SentrySettings = SentrySettings()): SentryWatch = SentryWatch(settings, clock)

    /** Feeds [frame] every [every] for [period], returning every event raised. */
    private fun SentryWatch.feed(
        period: Duration,
        every: Duration = 200.milliseconds,
        frame: () -> LumaGrid,
    ): List<SentryEvent> {
        val events = mutableListOf<SentryEvent>()
        var fed = Duration.ZERO
        while (fed < period) {
            observe(frame())?.let(events::add)
            clock += every
            fed += every
        }
        return events
    }

    /** Arms with no delay and settles the background on a still scene. */
    private fun watching(settings: SentrySettings = SentrySettings(armDelay = Duration.ZERO)): SentryWatch =
        watch(settings).apply {
            arm()
            observe(still)
        }

    @Test
    fun `a disarmed watch ignores everything`() {
        val watch = watch()

        assertTrue(watch.feed(5.seconds) { moving() }.isEmpty())
        assertEquals(SentryState.Disarmed, watch.state)
        assertEquals(0.0, watch.lastScore)
    }

    @Test
    fun `nothing raises the alarm until the arm delay has passed`() {
        val watch = watch()
        watch.arm()

        val whileArming = watch.feed(10.seconds) { moving() }

        assertTrue(whileArming.isEmpty())
        assertEquals(SentryState.Arming, watch.state)
        watch.observe(still)
        assertEquals(SentryState.Watching, watch.state)
    }

    @Test
    fun `motion that lasts raises the alarm once`() {
        val watch = watching()

        val events = watch.feed(2.seconds) { moving() }

        assertEquals(listOf(SentryEvent.AlarmRaised), events)
        assertTrue(watch.state is SentryState.Alarm)
        assertTrue(watch.lastScore > 0)
    }

    @Test
    fun `a flicker shorter than the sustain time does not`() {
        val watch = watching()

        watch.observe(moving())
        clock += 200.milliseconds
        val events = watch.feed(3.seconds) { still }

        assertTrue(events.isEmpty())
        assertEquals(SentryState.Watching, watch.state)
    }

    @Test
    fun `the alarm keeps the most of the picture that changed at once`() {
        val watch = watching()
        watch.feed(1.seconds) { moving() }
        val first = (watch.state as SentryState.Alarm).peak

        watch.observe(LumaGrid(10, 10, IntArray(100) { if (it < 60) 250 else 100 }))

        assertTrue((watch.state as SentryState.Alarm).peak > first)
        watch.observe(moving())
        assertTrue((watch.state as SentryState.Alarm).peak > first)
    }

    @Test
    fun `an alarm ends after five quiet seconds and cools down before another`() {
        val watch = watching()
        watch.feed(1.seconds) { moving() }

        val calming = watch.feed(4.seconds) { still }
        assertTrue(calming.isEmpty())
        val ended = watch.feed(1.seconds) { still }
        assertEquals(listOf(SentryEvent.AlarmEnded), ended)
        assertEquals(SentryState.Cooldown, watch.state)

        assertTrue(watch.feed(29.seconds) { moving() }.isEmpty())
        assertEquals(listOf(SentryEvent.AlarmRaised), watch.feed(1.seconds) { moving() })
    }

    @Test
    fun `a cooldown that passes in quiet goes back to watching`() {
        val watch = watching()
        watch.feed(1.seconds) { moving() }
        watch.feed(6.seconds) { still }

        watch.feed(31.seconds) { still }

        assertEquals(SentryState.Watching, watch.state)
    }

    @Test
    fun `with no cooldown an ended alarm goes straight back to watching`() {
        val watch = watching(SentrySettings(armDelay = Duration.ZERO, cooldown = Duration.ZERO))
        watch.feed(1.seconds) { moving() }

        watch.feed(6.seconds) { still }

        assertEquals(SentryState.Watching, watch.state)
        assertEquals(listOf(SentryEvent.AlarmRaised), watch.feed(1.seconds) { moving() })
    }

    @Test
    fun `disarming during an alarm ends it without a word`() {
        val watch = watching()
        watch.feed(1.seconds) { moving() }

        watch.disarm()

        assertEquals(SentryState.Disarmed, watch.state)
        assertNull(watch.observe(still))
    }

    @Test
    fun `arming again starts the delay again`() {
        val watch = watch()
        watch.arm()
        watch.feed(8.seconds) { still }

        watch.arm()
        watch.feed(8.seconds) { still }

        assertEquals(SentryState.Arming, watch.state)
    }

    @Test
    fun `times that make no sense are refused`() {
        assertFailsWith<IllegalArgumentException> { SentrySettings(armDelay = (-1).seconds) }
        assertFailsWith<IllegalArgumentException> { SentrySettings(sustain = (-1).seconds) }
        assertFailsWith<IllegalArgumentException> { SentrySettings(cooldown = (-1).seconds) }
        assertFailsWith<IllegalArgumentException> { SentrySettings(quiet = Duration.ZERO) }
    }

    @Test
    fun `a watch made with the defaults keeps real time and starts disarmed`() {
        assertEquals(SentryState.Disarmed, SentryWatch(SentrySettings()).state)
    }
}
