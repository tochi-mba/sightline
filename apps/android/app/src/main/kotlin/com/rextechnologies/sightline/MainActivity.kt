package com.rextechnologies.sightline

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.net.toUri
import com.rextechnologies.sightline.design.RexTheme
import com.rextechnologies.sightline.service.CameraService
import com.rextechnologies.sightline.ui.Platform
import com.rextechnologies.sightline.ui.SightlineApp

/**
 * The one screen: the app's whole interface, and the things only an activity can do for it.
 *
 * Permissions are asked at the moment they are needed, after the screen has said why: nearby Wi-Fi when
 * connecting, location when the HUD opens, notifications when Sentry is armed. A refused permission is
 * not asked for again in a loop; the screen explains what it is for, and Settings can grant it.
 */
class MainActivity : ComponentActivity(), Platform {
    private var afterPermission: (() -> Unit)? = null

    private val permission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        val then = afterPermission
        afterPermission = null
        if (granted) then?.invoke()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        val graph = (application as SightlineApplication).graph
        if (savedInstanceState == null) {
            graph.checkForUpdate()
        }
        setContent { RexTheme { SightlineApp(graph, this) } }
    }

    override fun withNearbyPermission(then: () -> Unit) {
        // Android 13 split nearby Wi-Fi from location; before it, joining a camera by name needs neither.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            withPermission(Manifest.permission.NEARBY_WIFI_DEVICES, then)
        } else {
            then()
        }
    }

    override fun withLocationPermission(then: () -> Unit) = withPermission(
        Manifest.permission.ACCESS_FINE_LOCATION,
        then,
    )

    override fun withNotificationPermission(then: () -> Unit) {
        // Sentry still works without notifications; it just cannot say anything. So the question is asked,
        // and nothing waits for the answer.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            afterPermission = null
            permission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        then()
    }

    override fun openWifiSettings() {
        startActivity(Intent(Settings.Panel.ACTION_WIFI))
    }

    override fun openAppSettings() {
        startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", packageName, null)))
    }

    override fun openLink(url: String) {
        startActivity(Intent(Intent.ACTION_VIEW, url.toUri()))
    }

    override fun keepScreenOn(on: Boolean) {
        if (on) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        } else {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    override fun startCameraService() = CameraService.start(this)

    private fun withPermission(name: String, then: () -> Unit) {
        if (checkSelfPermission(name) == PackageManager.PERMISSION_GRANTED) {
            then()
        } else {
            afterPermission = then
            permission.launch(name)
        }
    }
}
