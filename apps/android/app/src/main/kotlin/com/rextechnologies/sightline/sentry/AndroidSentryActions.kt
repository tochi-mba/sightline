package com.rextechnologies.sightline.sentry

import android.app.Notification
import android.app.NotificationManager
import android.content.Context
import android.graphics.BitmapFactory
import com.rextechnologies.sightline.Notifications
import com.rextechnologies.sightline.R
import com.rextechnologies.sightline.core.library.MediaKind
import com.rextechnologies.sightline.core.sentry.SentryActions
import com.rextechnologies.sightline.library.GallerySink
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * What an alarm does on the phone: a notification with the picture that raised it, and that picture in
 * the gallery when the setting says so.
 */
class AndroidSentryActions(
    private val context: Context,
    private val gallery: GallerySink = GallerySink(context.contentResolver),
    private val zone: ZoneId = ZoneId.systemDefault(),
) : SentryActions {
    private val manager: NotificationManager = context.getSystemService(NotificationManager::class.java)

    override fun alarmRaised(at: Instant, snapshot: ByteArray, saveSnapshot: Boolean) {
        val time = TIME.withZone(zone).format(at)
        val saved = if (saveSnapshot) {
            runCatching {
                gallery.saveWhole("SENTRY_${FILE_TIME.withZone(zone).format(at)}", MediaKind.Jpeg, snapshot)
            }.isSuccess
        } else {
            false
        }

        val builder = Notification.Builder(context, Notifications.SENTRY_CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Motion seen at $time")
            .setContentText(if (saved) "The picture is in your gallery." else "Open Sightline to see the camera.")
            .setCategory(Notification.CATEGORY_ALARM)
            .setAutoCancel(true)
            .setContentIntent(Notifications.openApp(context))
        BitmapFactory.decodeByteArray(snapshot, 0, snapshot.size)?.let { picture ->
            builder.setLargeIcon(picture).setStyle(Notification.BigPictureStyle().bigPicture(picture))
        }

        manager.notify(Notifications.SENTRY_NOTIFICATION, builder.build())
    }

    override fun alarmEnded() {
        // The notification stays, so an alarm that came and went while nobody was looking is still there
        // to be seen.
    }
}

private val TIME: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm:ss")
private val FILE_TIME: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss")
