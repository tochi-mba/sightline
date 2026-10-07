package com.rextechnologies.sightline

import android.content.Context
import android.graphics.Bitmap
import com.rextechnologies.sightline.core.camera.CameraController
import com.rextechnologies.sightline.core.library.MediaSink
import com.rextechnologies.sightline.core.link.CameraLink
import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.link.PhoneNetworks
import com.rextechnologies.sightline.core.playback.ClipCache
import com.rextechnologies.sightline.core.playback.ClipSession
import com.rextechnologies.sightline.core.playback.SoundDevice
import com.rextechnologies.sightline.core.sentry.SentryActions
import com.rextechnologies.sightline.core.sentry.SentryRunner
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.core.settings.Settings
import com.rextechnologies.sightline.core.settings.SettingsStore
import com.rextechnologies.sightline.core.updates.Release
import com.rextechnologies.sightline.core.updates.UpdateCheck
import com.rextechnologies.sightline.core.updates.WhatsNewCatalog
import com.rextechnologies.sightline.core.updates.WhatsNewEntry
import com.rextechnologies.sightline.hud.HudFeed
import com.rextechnologies.sightline.library.GallerySink
import com.rextechnologies.sightline.link.AndroidCameraLink
import com.rextechnologies.sightline.link.AndroidNetworkSystem
import com.rextechnologies.sightline.link.PhoneNetworkReader
import com.rextechnologies.sightline.live.FrameDecoder
import com.rextechnologies.sightline.playback.AudioTrackDevice
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import com.rextechnologies.sightline.protocol.media.AviSound
import com.rextechnologies.sightline.sentry.AndroidSentryActions
import com.rextechnologies.sightline.sentry.LumaSampler
import com.rextechnologies.sightline.settings.PreferencesStore
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.onEach
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.time.TimeSource

/**
 * Everything the app shares, made once for the whole process and handed to whoever needs it.
 *
 * Hand-wired rather than injected by a framework: there are a dozen objects, and reading this file is the
 * whole of learning how they fit together. Every argument has the real thing as its default and a test
 * can replace any of them.
 *
 * The camera's controller lives here, not in a screen, so a recording, a download or Sentry's watch is
 * not ended by the screen turning off or rotating; the foreground service keeps the process alive while
 * any of them is running.
 */
class AppGraph(
    context: Context,
    val scope: CoroutineScope = MainScope(),
    store: SettingsStore = PreferencesStore(context),
    link: CameraLink = AndroidCameraLink(AndroidNetworkSystem(context)),
    val gallery: MediaSink = GallerySink(context.contentResolver),
    private val phoneNetworks: () -> PhoneNetworks = PhoneNetworkReader(context)::read,
    sentryActions: SentryActions = AndroidSentryActions(context),
    /** This build's version, as the app reports it and the what's-new check compares. */
    val version: String = BuildInfo.versionName(context),
    /** What each version brought, for the screen shown after an update. */
    val releaseNotes: List<WhatsNewEntry> = WhatsNewCatalog.entries,
    /** Fetches a web address as text, over the phone's ordinary internet: the update check. */
    private val fetch: (String) -> String = ::httpGet,
    /** Where blocking work runs: the update check's request. */
    private val io: CoroutineDispatcher = Dispatchers.IO,
    /** The clock frame rates, Sentry's timings and clips' playing are measured on. */
    private val timeSource: TimeSource = TimeSource.Monotonic,
    /** Clips played from the card, kept in the app's cache so they play again without the camera. */
    val clips: ClipCache = ClipCache(File(context.cacheDir, "clips")),
    /** Opens the phone's sound for a clip's, or says there is none. */
    private val openSound: (AviSound) -> SoundDevice? = AudioTrackDevice::open,
    /** Where a clip's pictures are decoded, away from the main thread. */
    private val decoding: CoroutineDispatcher = Dispatchers.Default,
) {
    val settings = Settings(store)
    val controller = CameraController(
        scope,
        link,
        timeSource = timeSource,
        reconnects = { settings[AppSettings.Reconnect] },
    )

    private val newer = MutableStateFlow<Release?>(null)
    private val clip = MutableStateFlow<ClipSession<Bitmap>?>(null)

    /**
     * The clip from the card playing, while one is. Held here rather than by a screen, so turning the phone does
     * not end it.
     */
    val playing: StateFlow<ClipSession<Bitmap>?> = clip.asStateFlow()

    /** A newer release than this one, once the update check has found one. */
    val update: StateFlow<Release?> = newer.asStateFlow()
    val sentry = SentryRunner(scope, controller, settings, sentryActions, LumaSampler::sample, timeSource = timeSource)

    /** The HUD's GPS, read only while the HUD is showing. */
    val hud = HudFeed(context)

    init {
        // Once a camera has said its name it is the one asked for next time, exactly, which Android can
        // grant with no dialog.
        controller.state.map { it.cameraName }.filterNotNull().distinctUntilChanged()
            .onEach { settings[AppSettings.CameraName] = it }
            .launchIn(scope)
    }

    /**
     * Asks GitHub, once, whether a newer release exists, when the person allows it. A check that fails,
     * offline or rate-limited, simply finds nothing: it is never what stops the app working.
     */
    fun checkForUpdate() {
        if (!settings[AppSettings.CheckForUpdates]) {
            return
        }

        scope.launch {
            newer.value =
                withContext(io) { runCatching { UpdateCheck.newer(fetch(UpdateCheck.LATEST), version) }.getOrNull() }
        }
    }

    /**
     * Plays [file], a video on the card: from the phone when it was played before, otherwise as it comes off the
     * card. Nothing happens when it can do neither, and the camera's notice says why.
     */
    fun play(file: CameraFile) {
        val started = controller.play(file, clips) ?: return
        stopPlaying()
        val began = timeSource.markNow()
        clip.value = ClipSession(started, scope, { began.elapsedNow() }, FrameDecoder()::decode, decoding, openSound)
    }

    /** Closes the clip playing, which stops fetching it if it still is. */
    fun stopPlaying() {
        clip.value?.close()
        clip.value = null
    }

    /** Pauses the clip playing, if one is: the app is out of sight. */
    fun pausePlaying() {
        clip.value?.pause()
    }

    /** What the phone's networks are doing now, for the sentence shown before connecting. */
    fun phoneNetworks(): PhoneNetworks = phoneNetworks.invoke()

    /** The camera to ask for: exactly the one last used, once there is one, with the saved password. */
    fun cameraNetwork(): CameraNetwork = CameraNetwork(
        name = settings[AppSettings.CameraName],
        password = settings[AppSettings.CameraPassword],
    )
}
