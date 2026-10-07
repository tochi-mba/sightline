package com.rextechnologies.sightline.core.camera

import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import com.rextechnologies.sightline.protocol.gpsock.DeviceStatus
import com.rextechnologies.sightline.protocol.gpsock.MenuSetting
import com.rextechnologies.sightline.protocol.gpsock.MenuSettingKind
import java.time.Duration

/** Where the connection to the camera is. */
sealed interface Connection {
    /** Not connected, and not trying. */
    data object Idle : Connection

    /** Waiting for the phone to join [network], which can mean waiting on the person to pick it. */
    data class Joining(val network: CameraNetwork) : Connection

    /** On the camera's network, opening the control channel and reading the camera. */
    data object Opening : Connection

    /** Connected; commands are accepted. */
    data object Connected : Connection

    /** The camera was lost, and this is attempt [attempt] of at most [of] to get it back. */
    data class Reconnecting(val attempt: Int, val of: Int, val problem: Problem) : Connection

    /** Not connected, because of [problem]. */
    data class Failed(val problem: Problem) : Connection
}

/** What the shutter does. The camera is switched to match, so its own screen agrees. */
enum class CaptureMode(val cameraMode: CameraMode) {
    Video(CameraMode.Record),
    Photo(CameraMode.Capture),
}

/** The live picture's state. The pictures themselves are in [CameraController.frames]. */
sealed interface LiveView {
    /** Nobody is watching, or there is no camera. */
    data object Off : LiveView

    /** Starting the stream. */
    data object Starting : LiveView

    /** Pictures are arriving, [framesPerSecond] of them a second over the last second. */
    data class Playing(val framesPerSecond: Double) : LiveView

    /** Stopped while the camera's card is being read, which the camera cannot do while streaming. */
    data object Paused : LiveView

    /** The stream failed for [reason]; it is started again shortly. */
    data class Interrupted(val reason: String) : LiveView
}

/** One picture from the live view. [number] counts up, so the same bytes twice are still two frames. */
class LiveFrame(val jpeg: ByteArray, val width: Int, val height: Int, val number: Long)

/**
 * What the camera last reported about itself, in the terms the app shows.
 *
 * Only the fields the status decoder has pinned down on the reference camera. Its battery level is
 * not one of them, so none is shown rather than a guess.
 */
data class CameraStatus(
    val mode: CameraMode?,
    val isRecording: Boolean,
    val onExternalPower: Boolean,
    val clipLength: Duration?,
    val recordTimeLeft: Duration?,
    val photosLeft: Int?,
) {
    companion object {
        /** The app's view of a status the camera sent. */
        fun of(status: DeviceStatus): CameraStatus = CameraStatus(
            mode = status.mode,
            isRecording = status.isRecording,
            onExternalPower = status.onExternalPower,
            clipLength = status.clipLength,
            recordTimeLeft = status.recordTimeLeft,
            photosLeft = status.photosLeft,
        )
    }
}

/**
 * One of the camera's settings and what it is set to now.
 *
 * @property menu The setting as the camera's own menu describes it.
 * @property value The choice it is set to, read back from the camera; null for a setting that is not a
 *   choice, or one the camera did not answer for.
 * @property text Its text, for a text or read-only setting the camera reported.
 */
data class CameraSetting(val menu: MenuSetting, val value: Int?, val text: String?) {
    /** The camera's name for the current choice, its text, or null when neither is known. */
    val shown: String? get() = value?.let(menu::labelFor) ?: text

    /**
     * Whether the app offers to change it.
     *
     * Choices only. Text settings, the Wi-Fi name and password, have a format on the wire that has not
     * been proven on the reference camera, and writing a password wrongly would lock the person out of
     * their camera; actions, formatting the card among them, are left to the camera's own menu.
     */
    val isChangeable: Boolean get() = menu.kind == MenuSettingKind.Choice && menu.choices.isNotEmpty()
}

/** One file being copied off the card. */
sealed interface Transfer {
    /** Waiting for the files before it. */
    data object Queued : Transfer

    /** [copied] bytes so far of about [expected]. */
    data class Copying(val copied: Long, val expected: Long) : Transfer

    /** Saved, to [where]. */
    data class Saved(val where: String) : Transfer

    /** Not saved, because of [reason]. */
    data class Failed(val reason: String) : Transfer
}

/**
 * What is on the camera's card.
 *
 * @property files Every file, newest last; null until the card has been read.
 * @property thumbnails Each file's thumbnail, once fetched.
 * @property reading Whether the card is being read now.
 * @property transfers Each file being, or that has been, copied in this session.
 */
data class Library(
    val files: List<CameraFile>? = null,
    val thumbnails: Map<CameraFile, Thumbnail> = emptyMap(),
    val reading: Boolean = false,
    val transfers: Map<CameraFile, Transfer> = emptyMap(),
)

/** A thumbnail's JPEG. A class of its own so a library's equality compares the bytes. */
class Thumbnail(val jpeg: ByteArray) {
    override fun equals(other: Any?): Boolean = other is Thumbnail && other.jpeg.contentEquals(jpeg)

    override fun hashCode(): Int = jpeg.contentHashCode()
}

/** What the camera is doing for the person right now; the controls that would clash wait for it. */
enum class Task {
    TakingPhoto,
    StartingRecording,
    StoppingRecording,
    SwitchingMode,
    ChangingSetting,
    ReadingCard,
    Copying,
    Deleting,
    Playing,
}

/**
 * A message for the person about something that just happened.
 *
 * @property id Counts up, so the same message twice is still shown twice, and dismissing one cannot
 *   dismiss a newer one.
 */
data class Notice(val id: Long, val text: String)

/** Everything the screens show about the camera. */
data class CameraState(
    val connection: Connection = Connection.Idle,
    /** The camera's own Wi-Fi name, read from it once connected. */
    val cameraName: String? = null,
    val status: CameraStatus? = null,
    val mode: CaptureMode = CaptureMode.Video,
    val live: LiveView = LiveView.Off,
    val settings: List<CameraSetting> = emptyList(),
    val library: Library = Library(),
    val task: Task? = null,
    val notice: Notice? = null,
) {
    /** Whether commands are accepted. */
    val isConnected: Boolean get() = connection == Connection.Connected

    /** Whether the camera is recording to its card. */
    val isRecording: Boolean get() = status?.isRecording == true
}
