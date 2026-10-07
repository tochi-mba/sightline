package com.rextechnologies.sightline.core.sentry

import com.rextechnologies.sightline.core.camera.CameraController
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.core.settings.Settings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.time.Clock
import java.time.Instant
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TimeMark
import kotlin.time.TimeSource

/**
 * What Sentry does when the alarm goes up or comes down, which takes the phone: a notification, a
 * snapshot in the gallery.
 */
interface SentryActions {
    /** The alarm went up at [at]; [snapshot] is the picture that raised it. */
    fun alarmRaised(at: Instant, snapshot: ByteArray, saveSnapshot: Boolean)

    /** The alarm that went up came down. */
    fun alarmEnded()
}

/** One alarm, for the Sentry screen's list. */
class SentryAlarm(val number: Int, val at: Instant, val snapshot: ByteArray)

/**
 * Sentry as the app runs it.
 *
 * @property armed Whether Sentry has been armed, whatever it is doing now.
 * @property watch Where the watch itself is: arming, watching, raising the alarm.
 * @property score How much of the picture changed in the last frame looked at, for a live meter.
 * @property alarms The alarms raised since it was armed, newest first, the latest few only.
 */
data class SentryStatus(
    val armed: Boolean = false,
    val watch: SentryState = SentryState.Disarmed,
    val score: Double = 0.0,
    val alarms: List<SentryAlarm> = emptyList(),
)

/**
 * Runs Sentry on the live view: arms the watch, feeds it frames, and does what an alarm calls for.
 *
 * Armed, it holds the live view open so pictures keep coming with the screen off, and looks at no more
 * than five frames a second: enough to see a person cross the picture, few enough that decoding them
 * costs the phone little. When the alarm goes up it tells [actions], and if the setting says so starts the
 * camera recording to its own card at full quality, unless the camera was recording already; when the
 * alarm ends, it stops a recording it started, and only one it started.
 *
 * Everything runs in [scope], which must use one thread, as the controller's does.
 *
 * @param sampler Turns a frame's JPEG into a brightness grid, or null when it cannot be decoded.
 */
class SentryRunner(
    private val scope: CoroutineScope,
    private val controller: CameraController,
    private val settings: Settings,
    private val actions: SentryActions,
    private val sampler: (ByteArray) -> LumaGrid?,
    private val clock: Clock = Clock.systemDefaultZone(),
    private val timeSource: TimeSource = TimeSource.Monotonic,
) {
    private val mutableStatus = MutableStateFlow(SentryStatus())
    private var watching: Job? = null
    private var lastLooked: TimeMark? = null
    private var recordingForAlarm = false
    private var alarmNumber = 0

    /** Sentry's state, for its screen and its notification. */
    val status: StateFlow<SentryStatus> = mutableStatus.asStateFlow()

    /** Arms Sentry with the settings as they are now. Arming again starts over. */
    fun arm() {
        disarm()
        val armed = SentryWatch(
            SentrySettings(
                sensitivity = settings[AppSettings.SentrySensitivity],
                armDelay = settings[AppSettings.SentryArmDelay].seconds,
                cooldown = settings[AppSettings.SentryCooldown].seconds,
            ),
            timeSource,
        )
        armed.arm()
        mutableStatus.value = SentryStatus(armed = true, watch = armed.state)
        controller.holdLive(LIVE_HOLDER)
        watching = scope.launch { controller.frames.filterNotNull().collect { look(armed, it.jpeg) } }
    }

    /** Stands Sentry down, stopping a recording it started. Harmless when not armed. */
    fun disarm() {
        watching?.cancel()
        watching = null
        lastLooked = null
        controller.releaseLive(LIVE_HOLDER)
        stopRecordingForAlarm()
        mutableStatus.update { it.copy(armed = false, watch = SentryState.Disarmed, score = 0.0) }
    }

    private fun look(armed: SentryWatch, jpeg: ByteArray) {
        val last = lastLooked
        if (last != null && last.elapsedNow() < lookEvery) {
            return
        }

        val grid = sampler(jpeg) ?: return
        lastLooked = timeSource.markNow()
        when (armed.observe(grid)) {
            SentryEvent.AlarmRaised -> raise(jpeg)
            SentryEvent.AlarmEnded -> {
                stopRecordingForAlarm()
                actions.alarmEnded()
            }

            null -> Unit
        }

        mutableStatus.update { it.copy(watch = armed.state, score = armed.lastScore) }
    }

    private fun raise(jpeg: ByteArray) {
        val alarm = SentryAlarm(++alarmNumber, clock.instant(), jpeg)
        mutableStatus.update { it.copy(alarms = (listOf(alarm) + it.alarms).take(KEPT_ALARMS)) }
        actions.alarmRaised(alarm.at, jpeg, settings[AppSettings.SentrySnapshots])
        if (settings[AppSettings.SentryRecords] && !controller.state.value.isRecording) {
            // Busy with something else the person asked for, the camera is left to it: this alarm then
            // has a snapshot and a notification, but no recording.
            recordingForAlarm = controller.toggleRecording() != null
        }
    }

    private fun stopRecordingForAlarm() {
        if (recordingForAlarm && controller.state.value.isRecording) {
            controller.toggleRecording()
        }

        recordingForAlarm = false
    }

    private companion object {
        /** Who holds the live view open while Sentry is armed. */
        const val LIVE_HOLDER = "sentry"

        /** How many alarms the Sentry screen lists. */
        const val KEPT_ALARMS = 50
    }
}

/** At most five frames a second are looked at. */
private val lookEvery = 200.milliseconds
