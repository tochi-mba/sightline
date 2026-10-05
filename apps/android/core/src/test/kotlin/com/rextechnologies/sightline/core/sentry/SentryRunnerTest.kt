package com.rextechnologies.sightline.core.sentry

import com.rextechnologies.sightline.core.camera.ControllerHarness
import com.rextechnologies.sightline.core.camera.LiveView
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.core.settings.Settings
import com.rextechnologies.sightline.core.settings.SettingsStore
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import java.time.Clock
import java.time.Instant
import java.time.ZoneOffset
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/**
 * Sentry on a live view: a fake camera streams a still scene, then something moving, then stillness
 * again, ten pictures a second. Each picture's JPEG carries which scene it is in its fill byte, and the
 * sampler reads that back as a brightness grid, as decoding the real picture would.
 */
class SentryRunnerTest {
    private class MapStore : SettingsStore {
        val saved = mutableMapOf<String, String>()

        override fun read(key: String): String? = saved[key]

        override fun write(key: String, value: String?) {
            if (value == null) saved.remove(key) else saved[key] = value
        }
    }

    private class RecordedActions : SentryActions {
        val raised = mutableListOf<Pair<Instant, Boolean>>()
        var ended = 0

        override fun alarmRaised(at: Instant, snapshot: ByteArray, saveSnapshot: Boolean) {
            raised += at to saveSnapshot
        }

        override fun alarmEnded() {
            ended++
        }
    }

    private val noon = Instant.parse("2026-10-05T12:00:00Z")

    /** Still, moving, still: [still] pictures, then [moving], then [stillAfter], ten a second. */
    private fun scenes(still: Int, moving: Int, stillAfter: Int): List<ByteArray> =
        List(still) { FakeRtspCamera.jpeg(200, STILL) } +
            List(moving) { FakeRtspCamera.jpeg(200, if (it % 2 == 0) LEFT else RIGHT) } +
            List(stillAfter) { FakeRtspCamera.jpeg(200, STILL) }

    private fun sample(jpeg: ByteArray): LumaGrid? {
        val scene = jpeg[4]
        if (scene == UNREADABLE) {
            return null
        }

        val left = scene == LEFT
        val right = scene == RIGHT
        return LumaGrid(
            10,
            10,
            IntArray(100) { index ->
                val x = index % 10
                if ((left && x < 4 && index < 40) || (right && x >= 6 && index < 40)) 250 else 100
            },
        )
    }

    private inner class Rig(test: TestScope, frames: List<ByteArray>) {
        val camera = ControllerHarness(test).apply {
            stream = {
                this.frames += frames
                pace = 100.milliseconds
            }
        }
        val settings = Settings(MapStore()).apply {
            this[AppSettings.SentryArmDelay] = 0
            this[AppSettings.SentryCooldown] = 10
        }
        val actions = RecordedActions()
        val runner = SentryRunner(
            test.backgroundScope,
            camera.controller,
            settings,
            actions,
            ::sample,
            Clock.fixed(noon, ZoneOffset.UTC),
            test.testScheduler.timeSource,
        )
    }

    @Test
    fun `motion raises the alarm, records on the camera and saves the picture, then stands down`() = runTest {
        val rig = Rig(this, scenes(still = 10, moving = 20, stillAfter = 80))
        rig.camera.connected()

        rig.runner.arm()
        rig.camera.until { it.isRecording }
        val alarm = rig.runner.status.value
        advanceTimeBy(12.seconds)

        assertEquals(listOf(noon to true), rig.actions.raised)
        assertTrue(alarm.watch is SentryState.Alarm)
        assertEquals(1, alarm.alarms.single().number)
        assertEquals(noon, alarm.alarms.single().at)
        assertTrue(alarm.alarms.single().snapshot.isNotEmpty())
        assertTrue(alarm.score > 0)
        assertEquals(1, rig.actions.ended)
        assertFalse(rig.camera.control.isRecording)
        assertEquals(SentryState.Cooldown, rig.runner.status.value.watch)
    }

    @Test
    fun `while armed it holds the live view open, and lets go when disarmed`() = runTest {
        val rig = Rig(this, scenes(still = 50, moving = 0, stillAfter = 0))
        rig.camera.connected()

        rig.runner.arm()
        rig.camera.until { it.live is LiveView.Playing }
        assertTrue(rig.runner.status.value.armed)
        rig.runner.disarm()
        runCurrent()

        assertEquals(LiveView.Off, rig.camera.state.live)
        assertEquals(SentryStatus(), rig.runner.status.value)
    }

    @Test
    fun `no more than five pictures a second are looked at`() = runTest {
        var looked = 0
        val rig = Rig(this, scenes(still = 30, moving = 0, stillAfter = 0))
        val counting = SentryRunner(
            backgroundScope,
            rig.camera.controller,
            rig.settings,
            rig.actions,
            { jpeg -> looked++.let { sample(jpeg) } },
            timeSource = testScheduler.timeSource,
        )
        rig.camera.connected()

        counting.arm()
        advanceTimeBy(2.05.seconds)

        // Pictures arrive ten a second from 0.3 s; one in two is looked at.
        assertTrue(looked in 8..10, "$looked looked at")
    }

    @Test
    fun `a camera already recording is left recording, and only the alarm is raised`() = runTest {
        val rig = Rig(this, scenes(still = 10, moving = 20, stillAfter = 80))
        rig.camera.control.isRecording = true
        rig.camera.connected()

        rig.runner.arm()
        advanceTimeBy(12.seconds)

        assertEquals(1, rig.actions.raised.size)
        assertTrue(rig.camera.control.isRecording)
    }

    @Test
    fun `with recording and snapshots off an alarm only notifies`() = runTest {
        val rig = Rig(this, scenes(still = 10, moving = 20, stillAfter = 0))
        rig.settings[AppSettings.SentryRecords] = false
        rig.settings[AppSettings.SentrySnapshots] = false
        rig.camera.connected()

        rig.runner.arm()
        advanceTimeBy(4.seconds)

        assertEquals(listOf(noon to false), rig.actions.raised)
        assertFalse(rig.camera.control.isRecording)
    }

    @Test
    fun `disarming during an alarm stops the recording it started`() = runTest {
        val rig = Rig(this, scenes(still = 10, moving = 200, stillAfter = 0))
        rig.camera.connected()
        rig.runner.arm()
        rig.camera.until { it.isRecording }

        rig.runner.disarm()
        rig.camera.until { !it.isRecording }

        assertFalse(rig.camera.control.isRecording)
        assertFalse(rig.runner.status.value.armed)
    }

    @Test
    fun `a camera busy with something the person asked for records nothing for that alarm`() = runTest {
        val rig = Rig(this, scenes(still = 30, moving = 30, stillAfter = 80))
        rig.camera.connected()
        rig.runner.arm()
        rig.camera.until { it.live is LiveView.Playing }

        // A slow camera taking a photo is busy for seconds, through the moment the alarm goes up.
        rig.camera.control.answerDelay = 2.seconds
        rig.camera.controller.takePhoto()
        advanceTimeBy(6.seconds)
        rig.camera.control.answerDelay = kotlin.time.Duration.ZERO
        advanceTimeBy(10.seconds)

        assertEquals(1, rig.actions.raised.size)
        assertFalse(rig.camera.control.isRecording)
    }

    @Test
    fun `pictures that cannot be decoded are passed over`() = runTest {
        val rig = Rig(this, List(20) { FakeRtspCamera.jpeg(200, UNREADABLE) })
        rig.camera.connected()

        rig.runner.arm()
        advanceTimeBy(3.seconds)

        assertTrue(rig.actions.raised.isEmpty())
        assertEquals(SentryState.Watching, rig.runner.status.value.watch)
    }

    @Test
    fun `only the latest fifty alarms are kept`() = runTest {
        // Each a second of motion and then twenty still, longer than an alarm and its cooldown take.
        val busy = List(60) { scenes(still = 2, moving = 10, stillAfter = 200) }.flatten()
        val rig = Rig(this, busy)
        rig.settings[AppSettings.SentryRecords] = false
        rig.camera.connected()

        rig.runner.arm()
        advanceTimeBy(22.minutes())

        assertEquals(50, rig.runner.status.value.alarms.size)
        assertEquals(rig.actions.raised.size, rig.runner.status.value.alarms.first().number)
    }

    @Test
    fun `a recording the person stopped is not started again when the alarm ends`() = runTest {
        val rig = Rig(this, scenes(still = 10, moving = 20, stillAfter = 80))
        rig.camera.connected()
        rig.runner.arm()
        rig.camera.until { it.isRecording }

        rig.camera.controller.toggleRecording()!!.join()
        advanceTimeBy(12.seconds)

        assertEquals(1, rig.actions.ended)
        assertFalse(rig.camera.control.isRecording)
    }

    @Test
    fun `a runner made with the defaults keeps real time and starts disarmed`() = runTest {
        val rig = Rig(this, emptyList())
        val runner = SentryRunner(backgroundScope, rig.camera.controller, rig.settings, rig.actions, ::sample)

        runner.disarm()

        assertEquals(SentryStatus(), runner.status.value)
    }

    private fun Int.minutes() = (this * 60).seconds

    private companion object {
        const val STILL: Byte = 0x10
        const val LEFT: Byte = 0x20
        const val RIGHT: Byte = 0x30
        const val UNREADABLE: Byte = 0x40
    }
}
