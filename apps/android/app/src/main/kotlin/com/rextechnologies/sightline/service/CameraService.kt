package com.rextechnologies.sightline.service

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import com.rextechnologies.sightline.AppGraph
import com.rextechnologies.sightline.Notifications
import com.rextechnologies.sightline.R
import com.rextechnologies.sightline.SightlineApplication
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.onEach

/**
 * Keeps the app alive while a camera is connected or Sentry is armed, and carries the notification that
 * says so, with the controls that matter.
 *
 * A connected-device foreground service: the kind Android allows for an app holding a connection to a
 * nearby device, with no daily limit. It does no work of its own. The controller and Sentry live in the
 * app's graph; this only stops Android ending the process while they are busy with the screen off, and
 * stops itself the moment neither is.
 */
class CameraService : Service() {
    private lateinit var graph: AppGraph
    private var watching: Job? = null

    override fun onCreate() {
        super.onCreate()
        graph = (application as SightlineApplication).graph
        Notifications.ensureChannels(this)
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Android requires the promise to be kept at once, before anything that might take a moment.
        startForeground(
            Notifications.CAMERA_NOTIFICATION,
            notification(ServiceNotice("Sightline", "Connecting to the camera.", emptyList())),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
        )

        when (intent?.action?.let { action -> NoticeAction.entries.firstOrNull { it.name == action } }) {
            NoticeAction.StartRecording, NoticeAction.StopRecording -> graph.controller.toggleRecording()
            NoticeAction.TakePhoto -> graph.controller.takePhoto()
            NoticeAction.Disarm -> graph.sentry.disarm()
            NoticeAction.Disconnect -> {
                graph.sentry.disarm()
                graph.controller.disconnect()
            }

            null -> Unit
        }

        if (watching == null) {
            watching = combine(graph.controller.state, graph.sentry.status, ServiceNotice::of)
                .onEach { notice ->
                    if (notice == null) {
                        stopSelf()
                    } else {
                        getSystemService(
                            NotificationManager::class.java,
                        ).notify(Notifications.CAMERA_NOTIFICATION, notification(notice))
                    }
                }
                .launchIn(graph.scope)
        }

        return START_NOT_STICKY
    }

    override fun onDestroy() {
        watching?.cancel()
        super.onDestroy()
    }

    private fun notification(notice: ServiceNotice): Notification {
        val builder = Notification.Builder(this, Notifications.CAMERA_CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(notice.title)
            .setContentText(notice.text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setContentIntent(Notifications.openApp(this))
        notice.actions.forEach { action ->
            builder.addAction(Notification.Action.Builder(null, action.label, pendingAction(action)).build())
        }
        return builder.build()
    }

    private fun pendingAction(action: NoticeAction): PendingIntent = PendingIntent.getService(
        this,
        action.ordinal + 1,
        Intent(this, CameraService::class.java).setAction(action.name),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
    )

    companion object {
        /** Starts the service; called as a camera is being connected to, or Sentry armed. */
        fun start(context: Context) {
            context.startForegroundService(Intent(context, CameraService::class.java))
        }
    }
}
