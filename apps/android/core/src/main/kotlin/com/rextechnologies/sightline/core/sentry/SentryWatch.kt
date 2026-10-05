package com.rextechnologies.sightline.core.sentry

import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TimeMark
import kotlin.time.TimeSource

/**
 * How Sentry watches.
 *
 * @property sensitivity How much change counts as motion.
 * @property zone Which part of the picture is watched; all of it when null.
 * @property armDelay How long after arming before anything can raise the alarm, to leave the room.
 * @property sustain How long motion must last to raise the alarm: a moth across the lens is shorter.
 * @property quiet How long without motion before an alarm is over.
 * @property cooldown After an alarm, how long before another can be raised, so one person pacing is
 *   one alarm rather than twenty.
 */
data class SentrySettings(
    val sensitivity: Sensitivity = Sensitivity.Medium,
    val zone: Zone? = null,
    val armDelay: Duration = 10.seconds,
    val sustain: Duration = 0.5.seconds,
    val quiet: Duration = 5.seconds,
    val cooldown: Duration = 30.seconds,
) {
    init {
        require(!armDelay.isNegative() && !sustain.isNegative() && !cooldown.isNegative()) {
            "Sentry's times cannot be negative."
        }
        require(quiet.isPositive()) { "An alarm needs a moment of quiet to end." }
    }
}

/** Where Sentry is. */
sealed interface SentryState {
    /** Not watching. */
    data object Disarmed : SentryState

    /** Armed, waiting out the arm delay before it watches. */
    data object Arming : SentryState

    /** Watching for motion. */
    data object Watching : SentryState

    /** Motion was seen and is still going on; [peak] is the most of the picture that has changed at once. */
    data class Alarm(val peak: Double) : SentryState

    /** An alarm just ended; another cannot be raised until the cooldown is over. */
    data object Cooldown : SentryState
}

/** What one frame did to Sentry: the alarm starting or ending, or nothing worth telling anyone. */
enum class SentryEvent {
    AlarmRaised,
    AlarmEnded,
}

/**
 * Sentry's watch over the live view: arming, watching, raising the alarm and standing down.
 *
 * Pure: it is handed brightness grids and reads the time from [timeSource], so a whole night's watch is
 * tested in milliseconds. The app does what an event calls for: a notification, a snapshot, recording
 * on the camera's card while the alarm lasts.
 */
class SentryWatch(private val settings: SentrySettings, private val timeSource: TimeSource = TimeSource.Monotonic) {
    private val detector = MotionDetector(settings.sensitivity, settings.zone)
    private var since: TimeMark = timeSource.markNow()
    private var motionSince: TimeMark? = null

    /** When motion was last seen; only read during an alarm, which motion always started. */
    private var lastMotion: TimeMark = timeSource.markNow()

    /** Where Sentry is now. */
    var state: SentryState = SentryState.Disarmed
        private set

    /** The share of the picture that changed in the last frame, for a live meter while setting up. */
    var lastScore: Double = 0.0
        private set

    /** Starts watching, after the arm delay. Arming again restarts the delay. */
    fun arm() {
        detector.reset()
        enter(if (settings.armDelay.isPositive()) SentryState.Arming else SentryState.Watching)
    }

    /** Stops watching. An alarm in progress simply ends; nothing is reported for it. */
    fun disarm() {
        enter(SentryState.Disarmed)
    }

    /**
     * Looks at one frame's brightness.
     *
     * The background is learnt while arming as well, so watching starts against the scene as it
     * settled rather than against the room with the person who armed it still in it.
     *
     * @return [SentryEvent.AlarmRaised] when this frame raised the alarm, [SentryEvent.AlarmEnded] when
     *   it ended one, otherwise null.
     */
    fun observe(grid: LumaGrid): SentryEvent? {
        val now = state
        if (now == SentryState.Disarmed) {
            return null
        }

        val score = detector.observe(grid)
        lastScore = score
        val moving = detector.isMotion(score)
        if (moving) {
            lastMotion = timeSource.markNow()
            if (motionSince == null) {
                motionSince = timeSource.markNow()
            }
        } else {
            motionSince = null
        }

        return when {
            now == SentryState.Arming -> {
                if (since.elapsedNow() >= settings.armDelay) {
                    enter(SentryState.Watching)
                }
                null
            }

            now == SentryState.Watching -> raiseIfSustained(score)
            now is SentryState.Alarm -> continueAlarm(now, moving, score)
            // Cooling down, the only state left.
            since.elapsedNow() >= settings.cooldown -> {
                enter(SentryState.Watching)
                raiseIfSustained(score)
            }

            else -> null
        }
    }

    private fun continueAlarm(alarm: SentryState.Alarm, moving: Boolean, score: Double): SentryEvent? {
        if (moving) {
            state = SentryState.Alarm(maxOf(alarm.peak, score))
            return null
        }

        if (lastMotion.elapsedNow() < settings.quiet) {
            return null
        }

        enter(if (settings.cooldown.isPositive()) SentryState.Cooldown else SentryState.Watching)
        return SentryEvent.AlarmEnded
    }

    private fun raiseIfSustained(score: Double): SentryEvent? {
        val started = motionSince ?: return null
        if (started.elapsedNow() < settings.sustain) {
            return null
        }

        state = SentryState.Alarm(score)
        return SentryEvent.AlarmRaised
    }

    private fun enter(next: SentryState) {
        state = next
        since = timeSource.markNow()
    }
}
