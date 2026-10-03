package com.rextechnologies.sightline.camera

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.Binder
import android.os.IBinder
import com.rextechnologies.sightline.MainActivity
import com.rextechnologies.sightline.core.model.AppModel
import com.rextechnologies.sightline.core.model.HudReading
import com.rextechnologies.sightline.network.LocalOnlyCameraNetwork
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

/** Bound while the UI is open; promoted to a connected-device foreground service for Sentry. */
class CameraService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private lateinit var engine: CameraEngine
    private val binder = LocalBinder()
    private var sentryStartedRecording = false

    val state: StateFlow<AppModel> get() = engine.state

    override fun onCreate() {
        super.onCreate()
        engine = CameraEngine(scope, LocalOnlyCameraNetwork(this))
        createNotificationChannel()
    }

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START_SENTRY -> startSentry()
            ACTION_STOP_SENTRY -> stopSentry()
        }
        return START_NOT_STICKY
    }

    fun connect(name: String, password: String) = scope.launch { engine.connect(name, password) }

    fun disconnect() = scope.launch { engine.disconnect() }

    fun capture() = scope.launch { engine.capture() }

    fun toggleRecording() = scope.launch { engine.toggleRecording() }

    fun writeSetting(id: Int, value: Int) = scope.launch { engine.writeSetting(id, value) }

    fun refreshLibrary() = scope.launch { engine.refreshLibrary() }

    fun setHudVisible(visible: Boolean) = engine.setHudVisible(visible)

    fun updateHud(reading: HudReading) = engine.updateHud(reading)

    fun startSentry() {
        startForeground(NOTIFICATION_ID, notification())
        engine.setSentryRunning(true)
        if (state.value.connected && !state.value.recording) {
            sentryStartedRecording = true
            scope.launch { engine.toggleRecording() }
        }
    }

    fun stopSentry() {
        engine.setSentryRunning(false)
        if (sentryStartedRecording && state.value.recording) scope.launch { engine.toggleRecording() }
        sentryStartedRecording = false
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        engine.close()
        scope.cancel()
        super.onDestroy()
    }

    inner class LocalBinder : Binder() {
        val service: CameraService get() = this@CameraService
    }

    private fun createNotificationChannel() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Sentry camera", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Keeps the local camera connection open while Sentry is watching."
            },
        )
    }

    private fun notification() = android.app.Notification.Builder(this, CHANNEL_ID)
        .setSmallIcon(android.R.drawable.presence_video_online)
        .setContentTitle("Sightline Sentry is watching")
        .setContentText("Camera traffic is local; mobile data and hotspot stay available.")
        .setOngoing(true)
        .setContentIntent(
            PendingIntent.getActivity(
                this,
                0,
                Intent(this, MainActivity::class.java),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            ),
        )
        .addAction(
            android.app.Notification.Action.Builder(
                null,
                "Stop",
                PendingIntent.getService(
                    this,
                    1,
                    Intent(this, CameraService::class.java).setAction(ACTION_STOP_SENTRY),
                    PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
                ),
            ).build(),
        )
        .build()

    companion object {
        const val ACTION_START_SENTRY = "com.rextechnologies.sightline.START_SENTRY"
        const val ACTION_STOP_SENTRY = "com.rextechnologies.sightline.STOP_SENTRY"
        private const val CHANNEL_ID = "sightline-sentry"
        private const val NOTIFICATION_ID = 41
    }
}
