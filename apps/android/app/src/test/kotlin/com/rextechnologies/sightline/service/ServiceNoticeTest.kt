package com.rextechnologies.sightline.service

import com.rextechnologies.sightline.core.camera.CameraState
import com.rextechnologies.sightline.core.camera.CameraStatus
import com.rextechnologies.sightline.core.camera.CaptureMode
import com.rextechnologies.sightline.core.camera.Connection
import com.rextechnologies.sightline.core.camera.Problem
import com.rextechnologies.sightline.core.camera.ProblemKind
import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.sentry.SentryState
import com.rextechnologies.sightline.core.sentry.SentryStatus
import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import java.time.Duration
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class ServiceNoticeTest {
    private val idle =
        CameraStatus(
            CameraMode.Record,
            isRecording = false,
            onExternalPower = false,
            clipLength = null,
            recordTimeLeft = null,
            photosLeft = null,
        )
    private val connected = CameraState(connection = Connection.Connected, cameraName = "Trail Cam", status = idle)
    private val off = SentryStatus()

    @Test
    fun `nothing needs the service with no camera and sentry off`() {
        assertNull(ServiceNotice.of(CameraState(), off))
        assertNull(
            ServiceNotice.of(CameraState(connection = Connection.Failed(Problem(ProblemKind.Lost, "gone"))), off),
        )
    }

    @Test
    fun `while connecting it says so and offers to stop`() {
        val notice = ServiceNotice.of(CameraState(connection = Connection.Joining(CameraNetwork())), off)!!

        assertEquals("Connecting to the camera", notice.title)
        assertEquals("Mobile data and your hotspot stay on.", notice.text)
        assertEquals(listOf(NoticeAction.Disconnect), notice.actions)
        assertEquals(
            "Connecting to the camera",
            ServiceNotice.of(CameraState(connection = Connection.Opening), off)!!.title,
        )
    }

    @Test
    fun `connected it names the camera and offers to record`() {
        val notice = ServiceNotice.of(connected, off)!!

        assertEquals("Connected to Trail Cam", notice.title)
        assertEquals("Camera traffic stays on its own Wi-Fi; your internet is untouched.", notice.text)
        assertEquals(listOf(NoticeAction.StartRecording, NoticeAction.Disconnect), notice.actions)
    }

    @Test
    fun `in photo mode a photo can be taken from the notification`() {
        val notice = ServiceNotice.of(connected.copy(mode = CaptureMode.Photo), off)!!

        assertEquals(
            listOf(NoticeAction.StartRecording, NoticeAction.TakePhoto, NoticeAction.Disconnect),
            notice.actions,
        )
    }

    @Test
    fun `recording it shows the clip's length and offers to stop`() {
        val recording = connected.copy(
            status = idle.copy(isRecording = true, clipLength = Duration.ofSeconds(83)),
            mode = CaptureMode.Photo,
        )

        val notice = ServiceNotice.of(recording, off)!!

        assertEquals("Recording 1:23", notice.title)
        assertEquals("On the camera's card, at its full quality.", notice.text)
        assertEquals(listOf(NoticeAction.StopRecording, NoticeAction.Disconnect), notice.actions)
        assertEquals("Recording", ServiceNotice.of(connected.copy(status = idle.copy(isRecording = true)), off)!!.title)
    }

    @Test
    fun `reconnecting it says so`() {
        val camera = connected.copy(connection = Connection.Reconnecting(2, 5, Problem(ProblemKind.Lost, "gone")))

        assertEquals("Reconnecting to Trail Cam", ServiceNotice.of(camera, off)!!.title)
    }

    @Test
    fun `sentry's state leads, and it can be disarmed from the notification`() {
        fun title(watch: SentryState) = ServiceNotice.of(connected, SentryStatus(armed = true, watch = watch))!!.title

        assertEquals("Sentry is arming", title(SentryState.Arming))
        assertEquals("Sentry is watching", title(SentryState.Watching))
        assertEquals("Sentry: motion now", title(SentryState.Alarm(0.1)))
        assertEquals("Sentry: motion seen, watching again shortly", title(SentryState.Cooldown))
        assertEquals(
            listOf(NoticeAction.StartRecording, NoticeAction.Disarm, NoticeAction.Disconnect),
            ServiceNotice.of(connected, SentryStatus(armed = true, watch = SentryState.Watching))!!.actions,
        )
    }

    @Test
    fun `sentry armed without a camera waits for one, and keeps to three buttons`() {
        val waiting = ServiceNotice.of(CameraState(), SentryStatus(armed = true, watch = SentryState.Arming))!!
        val photo = ServiceNotice.of(
            connected.copy(mode = CaptureMode.Photo),
            SentryStatus(armed = true, watch = SentryState.Watching),
        )!!

        assertEquals("Waiting for the camera. Sentry watches once it is connected.", waiting.text)
        assertEquals(listOf(NoticeAction.Disarm, NoticeAction.Disconnect), waiting.actions)
        assertEquals(listOf(NoticeAction.StartRecording, NoticeAction.TakePhoto, NoticeAction.Disarm), photo.actions)
        assertEquals("Record", NoticeAction.StartRecording.label)
    }
}
