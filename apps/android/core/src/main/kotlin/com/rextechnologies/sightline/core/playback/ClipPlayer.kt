package com.rextechnologies.sightline.core.playback

import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds
import kotlin.time.DurationUnit

// How much of the clip must be ready ahead of where it plays before playing starts, and how long before it is needed
// the last of it must be judged to arrive.
private val DEFAULT_LEAD = 1.seconds

// How far back the rate the clip arrives at is judged over, and how much of that is needed to judge it at all.
private val RATE_WINDOW = 4.seconds
private val RATE_NEEDS = 1.seconds

/** What a player is doing. */
enum class PlayerPhase {
    /** Waiting for enough of the clip to play through without stopping. */
    Waiting,

    /** Playing. */
    Playing,

    /** Stopped where it was, because the person said so. */
    Paused,

    /** Played to the end. */
    Ended,
}

/**
 * What one step of a player says to do.
 *
 * @property picture A picture to show now, or null to keep showing the one shown last.
 * @property sound Sound to queue behind whatever is already queued.
 * @property soundRestarts Whether to drop the sound already queued first: the clip paused, waited or jumped.
 */
data class PlayerStep(val picture: TimedPicture?, val sound: List<SoundSlice>, val soundRestarts: Boolean)

/**
 * Plays a clip that is still coming off the card: which picture shows when, which sound to queue, and when to wait
 * for more of the clip rather than stop part-way.
 *
 * The card is slower than the clip: a 1080p clip comes off it at a third to two-thirds of the speed it plays at
 * (measured 2026-10-07). So playing starts only once the rest will arrive a little before it is needed; until then
 * the first picture shows and the wait is said. That is judged in bytes, from how fast they have been coming and how
 * many the clip has: they come steadily, where the clip is ready only a whole run of sound at a time, which would
 * make a judgement in seconds of clip hopeful just after each run. If the download falls behind all the same, the
 * player waits rather than skips.
 *
 * It does nothing by itself: whatever shows the clip calls [step] on a timer, and the clock is handed in, so it is
 * tested on time a test controls.
 *
 * @param clip The clip, arriving.
 * @param length How many bytes the whole clip has, as the card lists it.
 * @param now The time on a clock that only goes forward.
 * @param lead How much must be ready ahead before playing starts, and how long before it is needed the last of the
 *   clip must be due.
 */
class ClipPlayer(
    private val clip: ClipReader,
    private val length: Long,
    private val now: () -> Duration,
    private val lead: Duration = DEFAULT_LEAD,
) {
    private val arrivals = ArrayDeque<Pair<Duration, Long>>()
    private var startedAt = Duration.ZERO
    private var startedFrom = Duration.ZERO
    private var soundRunsQueued = 0
    private var soundRestarts = true
    private var wanted = true
    private var shown = -1

    init {
        require(length >= 0) { "A clip cannot be shorter than no bytes." }
    }

    /** What the player is doing. */
    var phase: PlayerPhase = PlayerPhase.Waiting
        private set

    /** Where in the clip it is. */
    var position: Duration = Duration.ZERO
        private set

    /** How long the clip plays for, by its header; zero until the header has arrived. */
    val duration: Duration
        get() = clip.clip?.duration ?: Duration.ZERO

    /**
     * While waiting, how long until playing can start as far as can be told; null when it cannot be told yet or the
     * player is not waiting.
     */
    var startsIn: Duration? = null
        private set

    /** Plays from where it is, or from the start once it has ended. */
    fun play() {
        wanted = true
        if (phase == PlayerPhase.Ended) {
            position = Duration.ZERO
            shown = -1
        }

        if (phase == PlayerPhase.Paused || phase == PlayerPhase.Ended) {
            phase = PlayerPhase.Waiting
        }
    }

    /** Stops where it is. */
    fun pause() {
        wanted = false
        if (phase == PlayerPhase.Playing) {
            position = playhead(now())
        }

        if (phase != PlayerPhase.Ended) {
            phase = PlayerPhase.Paused
        }

        soundRestarts = true
    }

    /** Goes to [to], as far as the clip has arrived. */
    fun seek(to: Duration) {
        position = to.coerceIn(Duration.ZERO, clip.ready)
        shown = -1
        soundRestarts = true
        if (phase == PlayerPhase.Playing) {
            startedAt = now()
            startedFrom = position
        } else if (phase == PlayerPhase.Ended) {
            phase = if (wanted) PlayerPhase.Waiting else PlayerPhase.Paused
        }
    }

    /** Moves the player on to the present, and says what to show and play. */
    fun step(): PlayerStep {
        val wall = now()
        measure(wall)
        if (phase == PlayerPhase.Waiting && canPlay(wall)) {
            phase = PlayerPhase.Playing
            startedAt = wall
            startedFrom = position
            soundRestarts = true
        } else if (phase == PlayerPhase.Playing) {
            position = playhead(wall)
            val ready = clip.ready
            if (position >= ready) {
                position = ready
                phase = if (clip.isComplete) PlayerPhase.Ended else PlayerPhase.Waiting
                soundRestarts = true
            }
        }

        var picture: TimedPicture? = null
        val due = clip.pictureAt(position)
        if (due != null && due.number != shown) {
            shown = due.number
            picture = due.picture
        }

        val restarts = soundRestarts
        var sound = emptyList<SoundSlice>()
        if (phase == PlayerPhase.Playing) {
            // From where it plays after a jump; otherwise every run that has arrived since, counted exactly.
            val runs = if (restarts) clip.soundFrom(position) else clip.soundAfter(soundRunsQueued)
            sound = runs.slices
            soundRunsQueued = runs.runs
            soundRestarts = false
        }

        return PlayerStep(picture, sound, restarts && phase == PlayerPhase.Playing)
    }

    private fun playhead(wall: Duration): Duration = startedFrom + (wall - startedAt)

    /** Notes how much of the clip has arrived by now, keeping the last few seconds of that. */
    private fun measure(wall: Duration) {
        arrivals.addLast(wall to clip.bytesRead)
        while (arrivals.size > 2 && wall - arrivals.first().first > RATE_WINDOW) {
            arrivals.removeFirst()
        }
    }

    /**
     * Whether playing can start from where the player is: the whole clip is here, or, at the rate its bytes have
     * been coming, the last of them will be here a lead before the clip reaches them, with a lead ready already.
     */
    private fun canPlay(wall: Duration): Boolean {
        startsIn = null
        if (clip.clip == null) {
            return false
        }

        if (clip.isComplete) {
            return true
        }

        val (firstWall, firstBytes) = arrivals.first()
        val watched = wall - firstWall
        if (watched < RATE_NEEDS) {
            return false
        }

        val perSecond = (clip.bytesRead - firstBytes) / watched.toDouble(DurationUnit.SECONDS)
        if (perSecond <= 0) {
            return false
        }

        val rest = (maxOf(0L, length - clip.bytesRead) / perSecond).seconds
        val wait = rest + lead - (duration - position)
        if (wait > Duration.ZERO) {
            startsIn = wait
            return false
        }

        // The rest is in time; the start must not be on the very edge of what has arrived either.
        return clip.ready - position >= lead
    }
}
