package com.rextechnologies.sightline

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.provider.Settings
import android.view.WindowManager
import androidx.activity.result.contract.ActivityResultContracts
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.service.CameraService
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowActivity
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class MainActivityTest {
    private val application = ApplicationProvider.getApplicationContext<SightlineApplication>()
    private val test = TestGraph().also {
        it.graph.settings[AppSettings.OnboardingDone] = true
        application.graph = it.graph
    }

    private fun activity(): MainActivity = Robolectric.buildActivity(MainActivity::class.java).setup().get()

    /** Answers the permission request the activity just made, or [request] when given. */
    private fun MainActivity.answer(
        granted: Boolean,
        request: ShadowActivity.PermissionsRequest = shadowOf(this).lastRequestedPermission,
    ) {
        val results = IntArray(request.requestedPermissions.size) {
            if (granted) PackageManager.PERMISSION_GRANTED else PackageManager.PERMISSION_DENIED
        }
        activityResultRegistry.dispatchResult(
            request.requestCode,
            Activity.RESULT_OK,
            Intent()
                .putExtra(
                    ActivityResultContracts.RequestMultiplePermissions.EXTRA_PERMISSIONS,
                    request.requestedPermissions,
                )
                .putExtra(ActivityResultContracts.RequestMultiplePermissions.EXTRA_PERMISSION_GRANT_RESULTS, results),
        )
    }

    @Test
    fun `a permission already granted runs the action at once`() {
        shadowOf(
            application,
        ).grantPermissions(Manifest.permission.NEARBY_WIFI_DEVICES, Manifest.permission.ACCESS_FINE_LOCATION)
        val activity = activity()
        var ran = 0

        activity.withNearbyPermission { ran++ }
        activity.withLocationPermission { ran++ }

        assertEquals(2, ran)
    }

    @Test
    fun `a permission asked for runs the action only once granted`() {
        val activity = activity()
        var ran = 0

        activity.withLocationPermission { ran++ }
        assertEquals(
            Manifest.permission.ACCESS_FINE_LOCATION,
            shadowOf(activity).lastRequestedPermission.requestedPermissions.single(),
        )
        activity.answer(granted = true)
        activity.withNearbyPermission { ran++ }
        activity.answer(granted = false)

        assertEquals(1, ran)
    }

    @Test
    @Config(sdk = [32])
    fun `before android 13 nearby wifi needs no permission and notifications need none`() {
        val activity = activity()
        var ran = 0

        activity.withNearbyPermission { ran++ }
        activity.withNotificationPermission { ran++ }

        assertEquals(2, ran)
    }

    @Test
    fun `sentry is armed whether or not notifications are allowed`() {
        val activity = activity()
        var ran = 0

        activity.withNotificationPermission { ran++ }
        assertEquals(1, ran)
        assertEquals(
            Manifest.permission.POST_NOTIFICATIONS,
            shadowOf(activity).lastRequestedPermission.requestedPermissions.single(),
        )
        activity.answer(granted = false)
        assertEquals(1, ran)

        shadowOf(application).grantPermissions(Manifest.permission.POST_NOTIFICATIONS)
        val asked = shadowOf(activity).lastRequestedPermission
        activity.withNotificationPermission { ran++ }

        assertEquals(2, ran)
        // Already allowed, so not asked again.
        assertEquals(asked, shadowOf(activity).lastRequestedPermission)
    }

    @Test
    fun `a permission answered after the screen was rebuilt runs nothing`() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        var ran = 0
        controller.get().withLocationPermission { ran++ }
        val request = shadowOf(controller.get()).lastRequestedPermission

        // Turned while the system asked: the new screen gets the answer, but not what the old one meant to do.
        controller.recreate()
        controller.get().answer(granted = true, request = request)

        assertEquals(0, ran)
    }

    @Test
    fun `the system screens and links open`() {
        val activity = activity()

        activity.openWifiSettings()
        assertEquals(Settings.Panel.ACTION_WIFI, shadowOf(activity).nextStartedActivity.action)
        activity.openAppSettings()
        val details = shadowOf(activity).nextStartedActivity
        assertEquals(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, details.action)
        assertEquals("package:${activity.packageName}", details.dataString)
        activity.openLink("https://github.com/tochi-mba/sightline")
        val link = shadowOf(activity).nextStartedActivity
        assertEquals(Intent.ACTION_VIEW, link.action)
        assertEquals("https://github.com/tochi-mba/sightline", link.dataString)
    }

    @Test
    fun `the screen is kept on only while asked`() {
        val activity = activity()

        activity.keepScreenOn(true)
        assertTrue(activity.window.attributes.flags and WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON != 0)
        activity.keepScreenOn(false)
        assertFalse(activity.window.attributes.flags and WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON != 0)
    }

    @Test
    fun `the camera service is started on request`() {
        val activity = activity()

        activity.startCameraService()

        assertEquals(CameraService::class.java.name, shadowOf(activity).nextStartedService.component?.className)
    }
}
