package com.rextechnologies.sightline.service

import android.app.Notification
import android.app.NotificationManager
import android.content.Intent
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.Notifications
import com.rextechnologies.sightline.SightlineApplication
import com.rextechnologies.sightline.TestGraph
import com.rextechnologies.sightline.core.camera.CaptureMode
import com.rextechnologies.sightline.core.camera.Connection
import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.Shadows.shadowOf
import org.robolectric.android.controller.ServiceController
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class CameraServiceTest {
    private val context = ApplicationProvider.getApplicationContext<SightlineApplication>()
    private val test = TestGraph().also { context.graph = it.graph }
    private val manager = context.getSystemService(NotificationManager::class.java)

    private fun service(): ServiceController<CameraService> = Robolectric.buildService(
        CameraService::class.java,
    ).create()

    private fun ServiceController<CameraService>.command(action: NoticeAction? = null) = apply {
        get().onStartCommand(Intent(context, CameraService::class.java).setAction(action?.name), 0, 1)
    }

    private fun shown(): Notification = shadowOf(manager).getNotification(Notifications.CAMERA_NOTIFICATION)

    @Test
    fun `it goes into the foreground at once and says what the camera is doing`() {
        test.connected()
        val controller = service().command()
        test.settle()

        val foreground = shadowOf(controller.get()).lastForegroundNotification
        assertTrue(foreground.flags and Notification.FLAG_ONGOING_EVENT != 0)
        assertEquals("Connected to ActionCam_000000000000", shown().extras.getString(Notification.EXTRA_TITLE))
        assertEquals(listOf("Record", "Disconnect"), shown().actions.map { it.title.toString() })
    }

    @Test
    fun `its buttons drive the camera`() {
        test.connected()
        val controller = service().command()

        controller.command(NoticeAction.StartRecording)
        test.settle()
        assertTrue(test.camera.isRecording)
        controller.command(NoticeAction.StopRecording)
        test.settle()
        assertFalse(test.camera.isRecording)
        test.graph.controller.switchMode(CaptureMode.Photo)
        test.settle()
        controller.command(NoticeAction.TakePhoto)
        test.settle()
        assertEquals(1, test.camera.picturesTaken)
        assertEquals(CameraMode.Capture, test.camera.mode)
    }

    @Test
    fun `disarming stands sentry down, and disconnecting leaves the camera and stops the service`() {
        test.connected()
        test.graph.sentry.arm()
        val controller = service().command()

        controller.command(NoticeAction.Disarm)
        assertFalse(test.graph.sentry.status.value.armed)
        test.graph.sentry.arm()
        controller.command(NoticeAction.Disconnect)
        test.settle()

        assertFalse(test.graph.sentry.status.value.armed)
        assertEquals(Connection.Idle, test.graph.controller.state.value.connection)
        assertTrue(shadowOf(controller.get()).isStoppedBySelf)
        controller.destroy()
    }

    @Test
    fun `a service android ends before it was ever started goes quietly`() {
        service().destroy()
    }

    @Test
    fun `an unknown action is ignored`() {
        test.connected()
        val controller = service()

        controller.get().onStartCommand(Intent(context, CameraService::class.java).setAction("something else"), 0, 1)
        controller.get().onStartCommand(null, 0, 2)

        assertEquals(Connection.Connected, test.graph.controller.state.value.connection)
        assertEquals(null, controller.get().onBind(Intent()))
    }

    @Test
    fun `starting it asks android for a foreground service`() {
        CameraService.start(context)

        assertEquals(CameraService::class.java.name, shadowOf(context).nextStartedService.component?.className)
    }
}
