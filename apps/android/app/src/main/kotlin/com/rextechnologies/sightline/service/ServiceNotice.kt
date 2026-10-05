package com.rextechnologies.sightline.service

import com.rextechnologies.sightline.core.camera.CameraState
import com.rextechnologies.sightline.core.camera.CaptureMode
import com.rextechnologies.sightline.core.camera.Connection
import com.rextechnologies.sightline.core.sentry.SentryState
import com.rextechnologies.sightline.core.sentry.SentryStatus
import java.time.Duration

/** A button on the ongoing notification. */
enum class NoticeAction(val label: String) {
    StartRecording("Record"),
    StopRecording("Stop"),
    TakePhoto("Photo"),
    Disarm("Disarm"),
    Disconnect("Disconnect"),
}

/**
 * What the ongoing notification says, worked out from the camera and Sentry with no Android in it.
 *
 * @property title The first line.
 * @property text The second line.
 * @property actions Its buttons, at most three, most useful first.
 */
data class ServiceNotice(val title: String, val text: String, val actions: List<NoticeAction>) {
    companion object {
        /**
         * The notice for [camera] and [sentry], or null when nothing needs the service: no camera
         * connected or being connected to, and Sentry not armed.
         */
        fun of(camera: CameraState, sentry: SentryStatus): ServiceNotice? {
            val name = camera.cameraName ?: "the camera"
            val connection = camera.connection
            if (!sentry.armed && (connection == Connection.Idle || connection is Connection.Failed)) {
                return null
            }

            // Only a camera that sent its status can be recording, so recording comes with the status.
            val recording = camera.status?.takeIf { it.isRecording }
            val title = when {
                sentry.armed -> sentryTitle(sentry.watch)
                recording != null -> "Recording ${clip(recording.clipLength)}".trimEnd()
                connection is Connection.Reconnecting -> "Reconnecting to $name"
                connection == Connection.Connected -> "Connected to $name"
                else -> "Connecting to $name"
            }
            val text = when {
                sentry.armed && connection != Connection.Connected ->
                    "Waiting for the camera. Sentry watches once it is connected."
                connection != Connection.Connected -> "Mobile data and your hotspot stay on."
                camera.isRecording -> "On the camera's card, at its full quality."
                else -> "Camera traffic stays on its own Wi-Fi; your internet is untouched."
            }

            val actions = buildList {
                if (connection == Connection.Connected) {
                    add(if (camera.isRecording) NoticeAction.StopRecording else NoticeAction.StartRecording)
                    if (!camera.isRecording && camera.mode == CaptureMode.Photo) {
                        add(NoticeAction.TakePhoto)
                    }
                }
                if (sentry.armed) add(NoticeAction.Disarm)
                add(NoticeAction.Disconnect)
            }
            return ServiceNotice(title, text, actions.take(MAX_ACTIONS))
        }

        private fun sentryTitle(watch: SentryState): String = when (watch) {
            is SentryState.Alarm -> "Sentry: motion now"
            SentryState.Arming -> "Sentry is arming"
            SentryState.Cooldown -> "Sentry: motion seen, watching again shortly"
            else -> "Sentry is watching"
        }

        /**
         * A clip's length as minutes and seconds, or nothing when the camera did not say. Padded by hand:
         * formatting would write the digits of the phone's language, and a timer reads the same everywhere.
         */
        private fun clip(length: Duration?): String {
            val seconds = length?.seconds ?: return ""
            return "${seconds / 60}:${(seconds % 60).toString().padStart(2, '0')}"
        }

        /** Android shows three buttons on a notification at most. */
        private const val MAX_ACTIONS = 3
    }
}
