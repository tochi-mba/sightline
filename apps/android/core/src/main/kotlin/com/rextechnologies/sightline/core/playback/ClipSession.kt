package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.media.AviSound
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.Closeable
import java.io.IOException
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds

// How often the player is stepped: about sixty times a second, smoother than any clip's pictures and nothing for a phone.
private val TICK = 15.milliseconds

/**
 * What a clip's player shows.
 *
 * @property picture The picture showing, once one has been decoded.
 * @property phase What the player is doing.
 * @property position Where in the clip it is.
 * @property duration How long the clip plays for, by its header; zero until the header has arrived.
 * @property width Pixels across, by the clip's own header; zero until it has arrived.
 * @property height Pixels down, likewise.
 * @property framesPerSecond Pictures a second, likewise.
 * @property startsIn While it waits, how long until it can play, as far as can be told.
 * @property failure Why the clip stopped arriving, or cannot be read, when it did.
 */
data class ClipView<P : Any>(
    val picture: P? = null,
    val phase: PlayerPhase = PlayerPhase.Waiting,
    val position: Duration = Duration.ZERO,
    val duration: Duration = Duration.ZERO,
    val width: Int = 0,
    val height: Int = 0,
    val framesPerSecond: Double = 0.0,
    val startsIn: Duration? = null,
    val failure: String? = null,
)

/**
 * A clip from the card playing, while it is still coming off the card or from the phone: the player stepped on a
 * timer, its pictures decoded away from the timer's thread and its sound fed to a device. What a screen shows is
 * [view]; a screen that goes away and comes back, as on turning the phone, finds the clip where it left it.
 *
 * Everything but decoding runs in [scope], on one thread. A picture that falls due is read from the clip there, a
 * small read, and decoded on [decoding]; while one decodes only the newest waits, so a slow decode drops pictures
 * rather than holding the clip back. The clip's file is opened for each read and closed straight after, so nothing
 * here holds it open when the fetch gives it its real name.
 *
 * @param P What a decoded picture is on this platform.
 * @param clip The clip, arriving or kept, which this lets go of when closed.
 * @param scope Where the player is stepped, on one thread.
 * @param now The time on a clock that only goes forward.
 * @param decode Turns a JPEG into a picture, or null when it does not decode.
 * @param decoding Where pictures are decoded.
 * @param openSound Opens a sound device for the clip's sound, or says there is none.
 */
class ClipSession<P : Any>(
    val clip: CardClip,
    private val scope: CoroutineScope,
    now: () -> Duration,
    private val decode: (ByteArray) -> P?,
    private val decoding: CoroutineDispatcher,
    private val openSound: (AviSound) -> SoundDevice?,
) : Closeable {
    private val player = ClipPlayer(clip.reader, clip.file.approximateBytes, now)
    private val shown = MutableStateFlow(ClipView<P>())
    private val ticking: Job
    private var sound: SoundDevice? = null
    private var feed: SoundFeed? = null
    private var soundOpened = false
    private var decodingNow = false
    private var newest: ByteArray? = null
    private var failed: String? = null
    private var closed = false

    /** What the player shows now. */
    val view: StateFlow<ClipView<P>> = shown.asStateFlow()

    init {
        ticking = scope.launch {
            while (true) {
                step()
                delay(TICK)
            }
        }
        scope.launch {
            (clip.arrival.await() as? ClipArrival.Failed)?.let {
                failed = it.reason
                describe()
            }
        }
    }

    /** Plays, or pauses; after the end, plays from the start. */
    fun playPause() {
        if (player.phase == PlayerPhase.Paused || player.phase == PlayerPhase.Ended) {
            player.play()
        } else {
            player.pause()
        }

        step()
    }

    /** Pauses, unless it already has or has ended: what happens when the phone shows something else. */
    fun pause() {
        if (player.phase == PlayerPhase.Playing || player.phase == PlayerPhase.Waiting) {
            player.pause()
            step()
        }
    }

    /** Goes to [to], as far as the clip has arrived. */
    fun seek(to: Duration) {
        player.seek(to)
        step()
    }

    /** Stops the clip, its sound and its fetch if it is still being fetched. */
    override fun close() {
        if (closed) {
            return
        }

        closed = true
        ticking.cancel()
        clip.close()
        sound?.close()
    }

    /** Moves the clip on to now: shows the picture due, plays the sound due, and says where it is. */
    private fun step() {
        // Closed, or the clip cannot be read: either way the timer has stopped, and nothing more happens.
        if (!ticking.isActive) {
            return
        }

        val step = player.step()
        try {
            step.picture?.let { show(read(it.offset, it.length)) }
            follow(step)
        } catch (unreadable: IOException) {
            // Nothing more of it can be read, so nothing more can be shown.
            failed = "This phone could not read the clip: ${unreadable.message}"
            ticking.cancel()
        }

        describe()
    }

    private fun read(offset: Long, length: Int): ByteArray = clip.openRead().use { input ->
        ByteArray(length).also {
            input.seek(offset)
            input.readFully(it)
        }
    }

    private fun follow(step: PlayerStep) {
        val avi = clip.reader.clip
        if (!soundOpened && avi != null) {
            soundOpened = true
            val format = avi.sound
            val device = format?.let(openSound)
            if (device != null) {
                sound = device
                feed = SoundFeed(device, format) { offset, length -> read(offset, length) }
            }
        }

        feed?.follow(player.phase, step)
    }

    private fun show(jpeg: ByteArray) {
        if (decodingNow) {
            newest = jpeg
            return
        }

        decodeLater(jpeg)
    }

    private fun decodeLater(jpeg: ByteArray) {
        decodingNow = true
        scope.launch {
            val picture = withContext(decoding) { decode(jpeg) }
            if (!closed && picture != null) {
                shown.update { it.copy(picture = picture) }
            }

            val next = newest
            newest = null
            if (next != null && !closed) {
                decodeLater(next)
            } else {
                decodingNow = false
            }
        }
    }

    private fun describe() {
        val avi = clip.reader.clip
        shown.update {
            it.copy(
                phase = player.phase,
                position = player.position,
                duration = player.duration,
                width = avi?.width ?: 0,
                height = avi?.height ?: 0,
                framesPerSecond = avi?.framesPerSecond ?: 0.0,
                startsIn = player.startsIn,
                failure = failed,
            )
        }
    }
}
