package com.rextechnologies.sightline

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.rextechnologies.sightline.camera.CameraService
import com.rextechnologies.sightline.design.SightlineTheme
import com.rextechnologies.sightline.hud.HudLocationTracker
import com.rextechnologies.sightline.ui.SightlineApp

class MainActivity : ComponentActivity() {
    private var cameraService by mutableStateOf<CameraService?>(null)
    private var pendingConnection: Pair<String, String>? = null
    private var locationTracker: HudLocationTracker? = null
    private val preferences by lazy { getSharedPreferences("sightline", MODE_PRIVATE) }

    private val nearbyPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        val pending = pendingConnection
        pendingConnection = null
        if (granted && pending != null) cameraService?.connect(pending.first, pending.second)
    }
    private val locationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) startHud()
    }
    private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    private val serviceConnection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, binder: IBinder?) {
            cameraService = (binder as CameraService.LocalBinder).service
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            cameraService = null
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            SightlineTheme {
                SightlineApp(
                    service = cameraService,
                    onboardingComplete = preferences.getBoolean(ONBOARDING_COMPLETE, false),
                    onFinishOnboarding = {
                        preferences.edit().putBoolean(ONBOARDING_COMPLETE, true).apply()
                        recreate()
                    },
                    onConnect = ::connect,
                    onHudChanged = ::setHud,
                    onStartSentry = ::startSentry,
                    onStopSentry = { cameraService?.stopSentry() },
                )
            }
        }
    }

    override fun onStart() {
        super.onStart()
        bindService(Intent(this, CameraService::class.java), serviceConnection, Context.BIND_AUTO_CREATE)
    }

    override fun onStop() {
        locationTracker?.close()
        locationTracker = null
        cameraService?.setHudVisible(false)
        cameraService = null
        unbindService(serviceConnection)
        super.onStop()
    }

    private fun connect(name: String, password: String) {
        val permission = if (Build.VERSION.SDK_INT >= 33) {
            Manifest.permission.NEARBY_WIFI_DEVICES
        } else {
            Manifest.permission.ACCESS_FINE_LOCATION
        }
        if (checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED) {
            cameraService?.connect(name, password)
        } else {
            pendingConnection = name to password
            nearbyPermission.launch(permission)
        }
    }

    private fun setHud(visible: Boolean) {
        cameraService?.setHudVisible(visible)
        if (!visible) {
            locationTracker?.close()
            locationTracker = null
            return
        }
        if (checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED) {
            startHud()
        } else {
            locationPermission.launch(Manifest.permission.ACCESS_FINE_LOCATION)
        }
    }

    private fun startHud() {
        locationTracker?.close()
        locationTracker = HudLocationTracker(this) { reading -> cameraService?.updateHud(reading) }.also { it.start() }
    }

    private fun startSentry() {
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        startForegroundService(Intent(this, CameraService::class.java).setAction(CameraService.ACTION_START_SENTRY))
    }

    private companion object {
        const val ONBOARDING_COMPLETE = "onboarding_complete"
    }
}
