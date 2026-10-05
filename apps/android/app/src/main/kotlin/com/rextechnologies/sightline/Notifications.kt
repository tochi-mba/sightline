package com.rextechnologies.sightline

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent

/**
 * The app's notification channels, and the intent every notification opens.
 *
 * Two channels, so a person can silence one without the other: the quiet one that says a camera is
 * connected, and Sentry's alarms, which are meant to be heard.
 */
object Notifications {
    /** The ongoing notification while a camera is connected or Sentry is armed. */
    const val CAMERA_CHANNEL = "camera"

    /** Sentry's alarms. */
    const val SENTRY_CHANNEL = "sentry"

    /** The ongoing notification's id; one at a time. */
    const val CAMERA_NOTIFICATION = 1

    /** The latest alarm's id; a new alarm replaces the last rather than piling up. */
    const val SENTRY_NOTIFICATION = 2

    /** Makes both channels; harmless when they exist. */
    fun ensureChannels(context: Context) {
        val manager: NotificationManager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CAMERA_CHANNEL, "Connected camera", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Shown while a camera is connected or Sentry is watching, with its controls."
            },
        )
        manager.createNotificationChannel(
            NotificationChannel(SENTRY_CHANNEL, "Sentry alarms", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "When Sentry sees motion."
            },
        )
    }

    /** Opens the app where it was. */
    fun openApp(context: Context): PendingIntent = PendingIntent.getActivity(
        context,
        0,
        Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )
}
