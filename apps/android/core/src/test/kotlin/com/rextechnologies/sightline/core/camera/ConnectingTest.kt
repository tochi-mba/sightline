package com.rextechnologies.sightline.core.camera

import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.link.LinkFailure
import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.FakeCamera
import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import com.rextechnologies.sightline.protocol.gpsock.GpSockCommand
import com.rextechnologies.sightline.protocol.gpsock.MenuIds
import com.rextechnologies.sightline.protocol.gpsock.MenuSettingKind
import com.rextechnologies.sightline.protocol.gpsock.NakCode
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.currentTime
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds

/** Connecting to the camera, staying connected, and getting it back when it is lost. */
class ConnectingTest {
    @Test
    fun `connecting joins the camera's network, opens the camera and reads it`() = runTest {
        val camera = ControllerHarness(this)

        val state = camera.connected()

        assertEquals(Connection.Joining(CameraNetwork()), camera.seen[1].connection)
        assertTrue(camera.seen.any { it.connection == Connection.Opening })
        assertEquals("ActionCam_000000000000", state.cameraName)
        assertEquals(CaptureMode.Video, state.mode)
        assertFalse(state.isRecording)
        assertEquals(CameraMode.Record, state.status?.mode)
        assertEquals(31998, state.status?.photosLeft)
        assertEquals(21, state.settings.size)
        assertEquals(listOf("Record", "Capture", "System", "Wifi"), state.settings.map { it.menu.category }.distinct())
    }

    @Test
    fun `each setting is read in the way its kind is read`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.values[MenuIds.RECORD_RESOLUTION] = byteArrayOf(2)

        val settings = camera.connected().settings.associateBy { it.menu.id }

        // A choice is read as its value, text as its characters, and an action not at all.
        assertEquals(2, settings.getValue(MenuIds.RECORD_RESOLUTION).value)
        assertEquals("ActionCam_000000000000", settings.getValue(MenuIds.WIFI_NAME).text)
        assertEquals("12345678", settings.getValue(MenuIds.WIFI_PASSWORD).text)
        val format = settings.values.single { it.menu.name == "Format" }
        assertEquals(MenuSettingKind.Action, format.menu.kind)
        assertNull(format.value)
        assertNull(format.text)
        // The version reads back empty from this camera, and nothing is better than an empty string.
        assertNull(settings.values.single { it.menu.name == "Version" }.text)
    }

    @Test
    fun `a camera in photo mode connects in photo mode`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.mode = CameraMode.Capture

        assertEquals(CaptureMode.Photo, camera.connected().mode)
    }

    @Test
    fun `a camera left browsing its card is put back where the app was`() = runTest {
        val camera = ControllerHarness(this)
        camera.controller.switchMode(CaptureMode.Photo)
        camera.control.mode = CameraMode.Browse

        val state = camera.connected()

        assertEquals(CameraMode.Capture, camera.control.mode)
        assertEquals(CaptureMode.Photo, state.mode)
        assertEquals(CameraMode.Capture, state.status?.mode)
    }

    @Test
    fun `a camera already recording shows as recording`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.isRecording = true

        assertTrue(camera.connected().isRecording)
    }

    @Test
    fun `asking to connect while connecting or connected does nothing more`() = runTest {
        val camera = ControllerHarness(this)
        camera.controller.connect(CameraNetwork())

        assertNull(camera.controller.connect(CameraNetwork()))
        camera.until { it.isConnected }
        assertNull(camera.controller.connect(CameraNetwork()))
        assertEquals(1, camera.link.requests.size)
    }

    @Test
    fun `a network the phone cannot join is explained at once and not retried`() = runTest {
        val camera = ControllerHarness(this)
        camera.link.failures += LinkFailure.Unavailable

        camera.controller.connect(CameraNetwork())!!.join()
        val failed = camera.state.connection as Connection.Failed

        assertEquals(ProblemKind.NotJoined, failed.problem.kind)
        assertEquals("Not joined (Unavailable).", failed.problem.detail)
        advanceTimeBy(60.seconds)
        assertEquals(1, camera.link.requests.size)
    }

    @Test
    fun `wifi switched off and a missing permission each say what fixes them`() = runTest {
        val camera = ControllerHarness(this)
        camera.link.failures += listOf(LinkFailure.WifiOff, LinkFailure.PermissionDenied)

        camera.controller.connect(CameraNetwork())!!.join()
        val off = camera.state.connection as Connection.Failed
        camera.controller.connect(CameraNetwork())!!.join()
        val denied = camera.state.connection as Connection.Failed

        assertEquals(Remedy.OpenWifiSettings, off.problem.kind.remedy)
        assertEquals(Remedy.OpenAppPermissions, denied.problem.kind.remedy)
    }

    @Test
    fun `a camera that never answers its control port is reported and its network let go`() = runTest {
        val camera = ControllerHarness(this)
        camera.link.controlTransport = { Unanswering() }

        camera.controller.connect(CameraNetwork())!!.join()
        val failed = camera.state.connection as Connection.Failed

        assertEquals(ProblemKind.NoAnswer, failed.problem.kind)
        assertEquals(10_000, currentTime)
        assertTrue(camera.link.lease.closed)
    }

    @Test
    fun `a camera that hangs up while being read is reported as lost and let go`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.hangsUp = true

        camera.controller.connect(CameraNetwork())!!.join()
        val failed = camera.state.connection as Connection.Failed

        assertEquals(ProblemKind.Lost, failed.problem.kind)
        assertTrue(camera.control.wasDisposed)
        assertTrue(camera.link.lease.closed)
    }

    @Test
    fun `the camera's status is polled, so its own record button shows`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()

        camera.control.isRecording = true
        advanceTimeBy(2.1.seconds)

        assertTrue(camera.state.isRecording)
    }

    @Test
    fun `a refused poll is asked again rather than taken for a lost camera`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        camera.control.forcedRefusals[GpSockCommand.GetDeviceStatus] = NakCode.ServerBusy

        advanceTimeBy(5.seconds)
        camera.control.forcedRefusals.clear()
        camera.control.isRecording = true
        advanceTimeBy(2.1.seconds)

        assertEquals(Connection.Connected, camera.state.connection)
        assertTrue(camera.state.isRecording)
    }

    @Test
    fun `losing the camera's network reconnects to exactly that camera`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        val first = camera.link.lease

        first.lose()
        val reconnecting = camera.until {
            it.connection is Connection.Reconnecting
        }.connection as Connection.Reconnecting
        camera.until { it.isConnected }

        assertEquals(1, reconnecting.attempt)
        assertEquals(5, reconnecting.of)
        assertEquals(ProblemKind.Lost, reconnecting.problem.kind)
        assertTrue(first.closed)
        assertEquals(CameraNetwork(name = "ActionCam_000000000000"), camera.link.requests[1])
        assertEquals(2, camera.link.leases.size)
    }

    @Test
    fun `a camera that stops answering a poll is lost and reconnected`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()

        camera.control.goesQuiet = true
        val reconnecting = camera.until {
            it.connection is Connection.Reconnecting
        }.connection as Connection.Reconnecting
        camera.control.goesQuiet = false
        camera.until { it.isConnected }

        assertEquals(ProblemKind.NoAnswer, reconnecting.problem.kind)
        assertEquals("The camera did not answer within 10 seconds.", reconnecting.problem.detail)
    }

    @Test
    fun `a camera that never comes back is given up after five tries, waiting longer each time`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        camera.link.failures += List(5) { LinkFailure.Unavailable }
        val lostAt = currentTime

        camera.link.lease.lose()
        val failed = camera.until { it.connection is Connection.Failed }.connection as Connection.Failed

        assertEquals(ProblemKind.NotJoined, failed.problem.kind)
        assertEquals(6, camera.link.requests.size)
        assertEquals(
            listOf(1, 2, 3, 4, 5),
            camera.seen.mapNotNull {
                (it.connection as? Connection.Reconnecting)?.attempt
            }.distinct(),
        )
        // Three seconds, then six, nine, twelve and fifteen.
        assertEquals(45_000, currentTime - lostAt)
    }

    @Test
    fun `a camera that comes back on a later try is connected again and the count starts over`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        camera.link.failures += List(2) { LinkFailure.Unavailable }

        camera.link.lease.lose()
        camera.until { (it.connection as? Connection.Reconnecting)?.attempt == 3 }
        camera.until { it.isConnected }
        camera.link.failures += List(4) { LinkFailure.Unavailable }
        camera.link.lease.lose()
        camera.until { (it.connection as? Connection.Reconnecting)?.attempt == 5 }

        assertTrue(camera.until { it.isConnected }.isConnected)
    }

    @Test
    fun `disconnecting leaves the camera and its network and forgets it, but not the chosen mode`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.mode = CameraMode.Capture
        camera.connected()

        camera.controller.disconnect().join()

        assertEquals(CameraState(mode = CaptureMode.Photo), camera.state)
        assertNull(camera.controller.frames.value)
        assertTrue(camera.control.wasDisposed)
        assertTrue(camera.link.lease.closed)
    }

    @Test
    fun `disconnecting while the phone is still joining gives the request up`() = runTest {
        val camera = ControllerHarness(this)
        camera.link.waits = true
        camera.controller.connect(CameraNetwork())
        camera.until { it.connection is Connection.Joining }

        camera.controller.disconnect().join()

        assertEquals(Connection.Idle, camera.state.connection)
        assertTrue(camera.link.leases.isEmpty())
    }

    @Test
    fun `disconnecting while reconnecting stops trying`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        camera.link.lease.lose()
        camera.until { it.connection is Connection.Reconnecting }

        camera.controller.disconnect().join()
        advanceTimeBy(60.seconds)

        assertEquals(Connection.Idle, camera.state.connection)
        assertEquals(1, camera.link.requests.size)
    }

    @Test
    fun `an operation cut off by disconnecting ends without a word`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        camera.control.goesQuiet = true

        val photo = camera.controller.takePhoto()!!
        runCurrent()
        camera.controller.disconnect().join()
        photo.join()

        assertNull(camera.state.notice)
        assertNull(camera.state.task)
        assertEquals(Connection.Idle, camera.state.connection)
    }

    @Test
    fun `a failed connection can be tried again`() = runTest {
        val camera = ControllerHarness(this)
        camera.link.failures += LinkFailure.Unavailable
        camera.controller.connect(CameraNetwork())!!.join()

        camera.controller.connect(CameraNetwork())

        assertTrue(camera.until { it.isConnected }.isConnected)
    }

    @Test
    fun `asking the camera for anything before connecting says to connect first`() = runTest {
        val camera = ControllerHarness(this)

        assertNull(camera.controller.takePhoto())
        runCurrent()

        assertEquals("Connect to the camera first.", camera.state.notice?.text)
    }

    @Test
    fun `asking while reconnecting says to wait a moment`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        camera.link.lease.lose()
        camera.until { it.connection is Connection.Reconnecting }

        assertNull(camera.controller.toggleRecording())

        assertEquals("The camera is reconnecting. Try again in a moment.", camera.state.notice?.text)
    }

    @Test
    fun `with reconnecting switched off a lost camera is reported at once`() = runTest {
        val camera = ControllerHarness(this)
        val controller =
            CameraController(backgroundScope, camera.link, timeSource = testScheduler.timeSource, reconnects = {
                false
            })
        controller.connect(CameraNetwork())
        controller.state.first { it.isConnected }

        camera.link.lease.lose()
        val failed = controller.state.first { it.connection is Connection.Failed }.connection as Connection.Failed

        assertEquals(ProblemKind.Lost, failed.problem.kind)
        assertEquals(1, camera.link.requests.size)
    }

    /** A control port that accepts the connection attempt and never completes it. */
    private class Unanswering : CameraTransport {
        override val isConnected = false

        override suspend fun connect() = awaitCancellation()

        override suspend fun send(bytes: ByteArray) = error("Never connected.")

        override suspend fun receive(into: ByteArray): Int = error("Never connected.")

        override fun close() = Unit
    }

    @Test
    fun `a controller made with the defaults starts idle`() = runTest {
        val controller = CameraController(backgroundScope, ControllerHarness(this).link)

        assertEquals(CameraState(), controller.state.value)
    }

    @Test
    fun `disconnecting before ever connecting is harmless`() = runTest {
        val camera = ControllerHarness(this)

        camera.controller.disconnect().join()

        assertEquals(Connection.Idle, camera.state.connection)
    }

    @Test
    fun `a camera that does not say its name is reconnected with the same request`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.menuXml = FakeCamera().menuXml
        val state = camera.connected()

        camera.link.lease.lose()
        camera.until { it.connection is Connection.Reconnecting }
        camera.until { it.isConnected }

        assertNull(state.cameraName)
        assertEquals(CameraNetwork(), camera.link.requests[1])
    }

    @Test
    fun `settings the camera will not read back are shown without a value`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.forcedRefusals[GpSockCommand.MenuGetParameter] = NakCode.InvalidCommand

        val state = camera.connected()

        assertEquals(21, state.settings.size)
        assertTrue(state.settings.all { it.value == null && it.text == null })
        assertNull(state.cameraName)
    }

    @Test
    fun `dismissing when there is no notice changes nothing`() = runTest {
        val camera = ControllerHarness(this)

        camera.controller.dismissNotice(1)

        assertNull(camera.state.notice)
    }
}
