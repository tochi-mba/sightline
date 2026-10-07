package com.rextechnologies.sightline.ui.live

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.rextechnologies.sightline.AppGraph
import com.rextechnologies.sightline.core.camera.CameraState
import com.rextechnologies.sightline.core.camera.CaptureMode
import com.rextechnologies.sightline.core.camera.Connection
import com.rextechnologies.sightline.core.camera.LiveView
import com.rextechnologies.sightline.core.camera.Task
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.core.settings.GridOverlay
import com.rextechnologies.sightline.core.settings.PictureFit
import com.rextechnologies.sightline.core.settings.Settings
import com.rextechnologies.sightline.design.OutlineAction
import com.rextechnologies.sightline.design.RexColors
import com.rextechnologies.sightline.design.RexDialog
import com.rextechnologies.sightline.design.RexSpace
import com.rextechnologies.sightline.design.RexText
import com.rextechnologies.sightline.design.RexType
import com.rextechnologies.sightline.design.SegmentedChoice
import com.rextechnologies.sightline.design.Tone
import com.rextechnologies.sightline.design.rexClickable
import com.rextechnologies.sightline.ui.Platform
import com.rextechnologies.sightline.ui.hud.HudOverlay
import kotlinx.coroutines.delay
import java.time.Duration
import java.util.Locale

/** Who holds the live view open while this screen shows it. */
private const val LIVE_HOLDER = "live-screen"

/** How wide the control strip beside the picture is on a phone turned on its side. */
private val STRIP = 152.dp

/**
 * The camera's picture and its controls, or, before a camera is connected, what connecting will do and
 * the button that does it.
 *
 * Upright, the controls sit under the picture; on a [wide] screen, beside it. A phone on its side is wide
 * but [short]: there the controls stack in a narrow strip, like a camera app's, so the picture gets the
 * whole height.
 */
@Composable
fun LiveScreen(graph: AppGraph, platform: Platform, camera: CameraState, wide: Boolean, short: Boolean = false) {
    val connection = camera.connection
    if (connection != Connection.Connected && connection !is Connection.Reconnecting) {
        ConnectPanel(graph, platform, camera)
        return
    }

    val version by graph.settings.changes.collectAsState()
    val settings = remember(version) { LiveSettings.from(graph.settings) }
    var hudOn by remember { mutableStateOf(false) }

    // The stream runs while this screen shows it, and the screen stays on while it does when asked to.
    DisposableEffect(Unit) {
        graph.controller.holdLive(LIVE_HOLDER)
        onDispose { graph.controller.releaseLive(LIVE_HOLDER) }
    }
    DisposableEffect(settings.keepScreenOn) {
        platform.keepScreenOn(settings.keepScreenOn)
        onDispose { platform.keepScreenOn(false) }
    }

    val picture: @Composable (Modifier) -> Unit = { modifier ->
        Box(modifier) {
            LivePicture(graph.controller.frames, settings, Modifier.fillMaxSize())
            // The HUD under the badges: a picture that has stopped still says why with the HUD on.
            if (hudOn) {
                HudOverlay(graph, Modifier.fillMaxSize())
            }
            PictureOverlays(camera, settings, hudOn)
        }
    }
    val controls: @Composable (Modifier, Boolean) -> Unit = { modifier, stacked ->
        Controls(
            graph = graph,
            camera = camera,
            hudOn = hudOn,
            onHud = { if (hudOn) hudOn = false else platform.withLocationPermission { hudOn = true } },
            stacked = stacked,
            modifier = modifier,
        )
    }

    when {
        wide && short -> Row(Modifier.fillMaxSize()) {
            picture(Modifier.weight(1f).fillMaxHeight())
            controls(Modifier.width(STRIP).fillMaxHeight().padding(horizontal = RexSpace.Compact), true)
        }

        wide -> Row(Modifier.fillMaxSize()) {
            picture(Modifier.weight(1f).fillMaxSize())
            controls(Modifier.width(300.dp).padding(RexSpace.Medium), false)
        }

        else -> Column(Modifier.fillMaxSize()) {
            picture(Modifier.weight(1f).fillMaxWidth())
            controls(Modifier.fillMaxWidth().padding(RexSpace.Medium), false)
        }
    }
}

/** The live view's look, from the app's settings, read together so the screen keys on one value. */
data class LiveSettings(
    val fit: PictureFit,
    val grid: GridOverlay,
    val flip: Boolean,
    val mirror: Boolean,
    val keepScreenOn: Boolean,
    val showFrameRate: Boolean,
) {
    companion object {
        fun from(settings: Settings) = LiveSettings(
            fit = settings[AppSettings.Fit],
            grid = settings[AppSettings.Grid],
            flip = settings[AppSettings.Flip],
            mirror = settings[AppSettings.Mirror],
            keepScreenOn = settings[AppSettings.KeepScreenOn],
            showFrameRate = settings[AppSettings.ShowStreamStats],
        )
    }
}

/**
 * What sits over the picture: the recording badge, the frame rate, and why the picture is not moving. With
 * the HUD on, the frame rate gives way to its figures and the reason moves to the middle, clear of them.
 */
@Composable
private fun PictureOverlays(camera: CameraState, settings: LiveSettings, hudOn: Boolean) {
    Box(Modifier.fillMaxSize().padding(RexSpace.Compact)) {
        if (camera.isRecording) {
            RecordingBadge(camera.status?.clipLength, Modifier.align(Alignment.TopStart))
        }

        val live = camera.live
        if (settings.showFrameRate && !hudOn && live is LiveView.Playing && live.framesPerSecond > 0) {
            Badge("%.1f fps".format(Locale.ROOT, live.framesPerSecond), Modifier.align(Alignment.TopEnd))
        }

        pictureMessage(camera)?.let {
            Badge(it, Modifier.align(if (hudOn) Alignment.Center else Alignment.BottomCenter))
        }
    }
}

/** Why the picture is not simply playing, in words, or null when it is. */
fun pictureMessage(camera: CameraState): String? {
    val connection = camera.connection
    return when {
        connection is Connection.Reconnecting ->
            "Reconnecting to the camera, attempt ${connection.attempt} of ${connection.of}"
        camera.live is LiveView.Interrupted -> "The picture stopped. Starting it again."
        camera.live == LiveView.Starting -> "Starting the picture"
        camera.live == LiveView.Paused -> "Paused while the card is read"
        else -> null
    }
}

@Composable
private fun RecordingBadge(clip: Duration?, modifier: Modifier) {
    val elapsed = rememberTicking(clip)
    Row(
        modifier = modifier
            .background(RexColors.Scrim, RoundedCornerShape(50))
            .border(RexSpace.Hairline, RexColors.Live, RoundedCornerShape(50))
            .padding(horizontal = 10.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Box(Modifier.size(8.dp).background(RexColors.Live, CircleShape))
        RexText(
            text = if (elapsed == null) "REC" else "REC ${clock(elapsed)}",
            style = RexType.LabelSmall.copy(color = RexColors.Live),
        )
    }
}

/**
 * [base] plus the seconds since it last changed, counting up once a second.
 *
 * The camera reports a clip's length every two seconds; counting between reports keeps the timer
 * moving one second at a time rather than jumping by two.
 */
@Composable
fun rememberTicking(base: Duration?): Duration? {
    var extra by remember(base) { mutableStateOf(Duration.ZERO) }
    LaunchedEffect(base) {
        while (base != null) {
            delay(1_000)
            extra = extra.plusSeconds(1)
        }
    }
    return base?.plus(extra)
}

@Composable
private fun Badge(text: String, modifier: Modifier) {
    Box(
        modifier
            .background(RexColors.Scrim, RoundedCornerShape(50))
            .padding(horizontal = 10.dp, vertical = 6.dp),
    ) {
        RexText(text = text.uppercase(Locale.ROOT), style = RexType.LabelSmall.copy(color = RexColors.Text))
    }
}

/**
 * The mode, the shutter, what is left on the card, the HUD, and leaving: in a row under or beside the
 * picture, or [stacked] in a narrow strip when the screen is short.
 */
@Composable
private fun Controls(
    graph: AppGraph,
    camera: CameraState,
    hudOn: Boolean,
    onHud: () -> Unit,
    stacked: Boolean,
    modifier: Modifier,
) {
    var confirmLeave by remember { mutableStateOf(false) }
    val connected = camera.connection == Connection.Connected
    val busy = camera.task != null
    val hud: @Composable (Modifier) -> Unit = {
        OutlineAction(
            text = if (hudOn) "Hide HUD" else "HUD",
            onClick = onHud,
            tone = if (hudOn) Tone.Signal else Tone.Neutral,
            modifier = it,
        )
    }
    val leave: @Composable (Modifier) -> Unit = {
        OutlineAction(
            text = "Leave",
            onClick = { if (camera.isRecording) confirmLeave = true else graph.controller.disconnect() },
            modifier = it,
        )
    }

    Column(
        modifier,
        verticalArrangement = if (stacked) {
            Arrangement.spacedBy(RexSpace.Compact, Alignment.CenterVertically)
        } else {
            Arrangement.spacedBy(RexSpace.Compact)
        },
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        RexText(text = statusLine(camera), style = RexType.BodySmall, maxLines = if (stacked) 3 else 1)
        SegmentedChoice(
            options = CaptureMode.entries,
            selected = camera.mode,
            label = { if (it == CaptureMode.Video) "Video" else "Photo" },
            onSelect = { graph.controller.switchMode(it) },
            enabled = connected && !camera.isRecording && !busy,
            modifier = Modifier.fillMaxWidth(),
        )
        if (stacked) {
            Shutter(camera, enabled = connected && !busy) { graph.controller.shutter() }
            hud(Modifier.fillMaxWidth())
            leave(Modifier.fillMaxWidth())
        } else {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.weight(1f), contentAlignment = Alignment.CenterStart) { hud(Modifier) }
                Shutter(camera, enabled = connected && !busy) { graph.controller.shutter() }
                Box(Modifier.weight(1f), contentAlignment = Alignment.CenterEnd) { leave(Modifier) }
            }
        }
    }

    if (confirmLeave) {
        RexDialog(
            title = "Stop recording and leave?",
            body = "The camera stops recording when Sightline leaves it. " +
                "What it has recorded so far stays on its card.",
            confirm = "Leave" to {
                confirmLeave = false
                graph.controller.disconnect()
            },
            destructive = true,
            onDismiss = { confirmLeave = false },
        )
    }
}

/**
 * The shutter: a red dot to record, a red square to stop, a white disc for a photo. Its shape says what
 * it will do, and so does what a screen reader calls it.
 */
@Composable
private fun Shutter(camera: CameraState, enabled: Boolean, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .semantics { contentDescription = shutterLabel(camera) }
            .rexClickable(enabled = enabled, onClick = onClick)
            .size(76.dp)
            .border(3.dp, RexColors.Text, CircleShape)
            .padding(8.dp),
        contentAlignment = Alignment.Center,
    ) {
        when {
            camera.isRecording -> Box(Modifier.size(26.dp).background(RexColors.Live, RoundedCornerShape(6.dp)))
            camera.mode == CaptureMode.Video -> Box(Modifier.fillMaxSize().background(RexColors.Live, CircleShape))
            else -> Box(Modifier.fillMaxSize().background(RexColors.Text, CircleShape))
        }
    }
}

/** What the shutter will do, as a screen reader says it. */
fun shutterLabel(camera: CameraState): String = when {
    camera.isRecording -> "Stop recording"
    camera.mode == CaptureMode.Video -> "Start recording"
    else -> "Take a photo"
}

/**
 * The line above the controls: what the camera is doing for the person, or what its card still holds in
 * the current mode, and whether it is on USB power.
 */
fun statusLine(camera: CameraState): String {
    val task = camera.task
    if (task != null) {
        return taskWords(task)
    }

    val status = camera.status ?: return ""
    val room = when {
        status.isRecording -> "Recording to the camera's card"
        camera.mode == CaptureMode.Photo -> status.photosLeft?.let { "Room for $it more photos" }
        else -> status.recordTimeLeft?.let { "Room for ${clock(it)} more video" }
    }
    val power = "On USB power".takeIf { status.onExternalPower }
    return listOfNotNull(room, power).joinToString("  ·  ")
}

private fun taskWords(task: Task): String = when (task) {
    Task.TakingPhoto -> "Taking a photo"
    Task.StartingRecording -> "Starting to record"
    Task.StoppingRecording -> "Stopping the recording"
    Task.SwitchingMode -> "Switching mode"
    Task.ChangingSetting -> "Changing a setting"
    Task.ReadingCard -> "Reading the card"
    Task.Copying -> "Copying from the card"
    Task.Deleting -> "Deleting from the card"
}

/**
 * A length of time as hours, minutes and seconds: 1:05:09, or 5:09 under an hour. Built by hand, because
 * formatting would write the digits of the phone's language, and a timer reads the same everywhere.
 */
fun clock(length: Duration): String {
    val total = length.seconds
    val hours = total / 3600
    val minutes = (total % 3600) / 60
    val seconds = (total % 60).toString().padStart(2, '0')
    return if (hours > 0) "$hours:${minutes.toString().padStart(2, '0')}:$seconds" else "$minutes:$seconds"
}
