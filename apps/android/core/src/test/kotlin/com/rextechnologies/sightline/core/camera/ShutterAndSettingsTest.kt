package com.rextechnologies.sightline.core.camera

import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import com.rextechnologies.sightline.protocol.gpsock.GpSockCommand
import com.rextechnologies.sightline.protocol.gpsock.MenuIds
import com.rextechnologies.sightline.protocol.gpsock.NakCode
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.seconds

/** The shutter, the capture mode and the camera's settings. */
class ShutterAndSettingsTest {
    @Test
    fun `the shutter records in video mode and says what the camera then reports`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()

        camera.controller.shutter()!!.join()

        assertTrue(camera.control.isRecording)
        assertTrue(camera.state.isRecording)
        assertEquals("Recording to the camera's card.", camera.state.notice?.text)
        assertNull(camera.state.task)
        assertTrue(camera.seen.any { it.task == Task.StartingRecording })
    }

    @Test
    fun `pressing it again stops the recording`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.isRecording = true
        camera.connected()

        camera.controller.shutter()!!.join()

        assertFalse(camera.control.isRecording)
        assertEquals("Recording stopped and saved to the card.", camera.state.notice?.text)
        assertTrue(camera.seen.any { it.task == Task.StoppingRecording })
    }

    @Test
    fun `recording from photo mode puts the camera back in video first`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.mode = CameraMode.Capture
        camera.connected()

        camera.controller.toggleRecording()!!.join()

        assertEquals(CameraMode.Record, camera.control.mode)
        assertTrue(camera.control.isRecording)
        // Found writing the Windows port: the app went on calling it photo mode while recording.
        assertEquals(CaptureMode.Video, camera.state.mode)
    }

    @Test
    fun `a camera that acknowledges the record button and does nothing is reported`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.forcedAnswers[GpSockCommand.RecordToggle] = ByteArray(0)
        camera.connected()

        camera.controller.toggleRecording()!!.join()
        assertEquals("The camera did not start recording.", camera.state.notice?.text)

        // Recording from its own button, which the next poll sees; the app's stop is then ignored too.
        camera.control.isRecording = true
        advanceTimeBy(2.1.seconds)
        camera.controller.toggleRecording()!!.join()
        assertEquals("The camera is still recording.", camera.state.notice?.text)
    }

    @Test
    fun `a photo is taken in capture mode and the camera is left there`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()

        camera.controller.takePhoto()!!.join()

        assertEquals(1, camera.control.picturesTaken)
        assertEquals(CameraMode.Capture, camera.control.mode)
        assertEquals("Photo saved to the camera's card.", camera.state.notice?.text)
    }

    @Test
    fun `the shutter takes a photo in photo mode`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.mode = CameraMode.Capture
        camera.connected()

        camera.controller.shutter()!!.join()

        assertEquals(1, camera.control.picturesTaken)
        assertEquals(CameraMode.Capture, camera.control.mode)
    }

    @Test
    fun `a photo is refused while the camera records`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.isRecording = true
        camera.connected()

        camera.controller.takePhoto()!!.join()

        assertEquals(0, camera.control.picturesTaken)
        assertTrue(camera.control.isRecording)
        assertEquals("Stop recording to take a photo.", camera.state.notice?.text)
    }

    @Test
    fun `a refusal from the camera is explained in words`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.forcedRefusals[GpSockCommand.CapturePicture] = NakCode.FullStorage
        camera.connected()

        camera.controller.takePhoto()!!.join()

        assertEquals("The camera said no: the card is full.", camera.state.notice?.text)
        assertEquals(Connection.Connected, camera.state.connection)
    }

    @Test
    fun `a second press while the first is still going is ignored`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()

        val first = camera.controller.takePhoto()
        val second = camera.controller.takePhoto()
        first!!.join()

        assertNull(second)
        assertEquals(1, camera.control.picturesTaken)
    }

    @Test
    fun `an operation that loses the camera starts a reconnect`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        camera.control.hangsUp = true

        camera.controller.takePhoto()!!.join()

        val reconnecting = camera.until {
            it.connection is Connection.Reconnecting
        }.connection as Connection.Reconnecting
        assertEquals(ProblemKind.Lost, reconnecting.problem.kind)
        assertNull(camera.state.task)
    }

    @Test
    fun `an operation cut short by a lost camera is cancelled with it`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        camera.control.goesQuiet = true

        val photo = camera.controller.takePhoto()!!
        camera.link.lease.lose()
        photo.join()

        assertTrue(photo.isCancelled)
        assertNull(camera.state.task)
    }

    @Test
    fun `choosing photos before connecting only chooses what the shutter does`() = runTest {
        val camera = ControllerHarness(this)

        assertNull(camera.controller.switchMode(CaptureMode.Photo))

        assertEquals(CaptureMode.Photo, camera.state.mode)
        assertTrue(camera.control.settingsWritten.isEmpty())
    }

    @Test
    fun `switching mode switches the camera so its own screen agrees`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()

        camera.controller.switchMode(CaptureMode.Photo)!!.join()

        assertEquals(CameraMode.Capture, camera.control.mode)
        assertEquals(CaptureMode.Photo, camera.state.mode)
        assertTrue(camera.seen.any { it.task == Task.SwitchingMode })
    }

    @Test
    fun `switching to the mode already chosen does nothing`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()

        assertNull(camera.controller.switchMode(CaptureMode.Video))
    }

    @Test
    fun `switching mode is refused while the camera records`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.isRecording = true
        camera.connected()

        camera.controller.switchMode(CaptureMode.Photo)!!.join()

        assertEquals(CaptureMode.Video, camera.state.mode)
        assertEquals("Stop recording to switch to photos.", camera.state.notice?.text)
    }

    @Test
    fun `a setting is written and then read back from the camera`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()

        camera.controller.changeSetting(MenuIds.RECORD_RESOLUTION, 1)!!.join()

        assertEquals(listOf(MenuIds.RECORD_RESOLUTION to 1), camera.control.settingsWritten)
        assertEquals(1, camera.state.settings.single { it.menu.id == MenuIds.RECORD_RESOLUTION }.value)
        assertNull(camera.state.notice)
    }

    @Test
    fun `a value the camera acknowledges but does not keep is reported`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.ignoredSettings += MenuIds.RECORD_RESOLUTION
        camera.connected()

        camera.controller.changeSetting(MenuIds.RECORD_RESOLUTION, 1)!!.join()

        val resolution = camera.state.settings.single { it.menu.id == MenuIds.RECORD_RESOLUTION }
        assertEquals(0, resolution.value)
        assertEquals("The camera kept Resolution at ${resolution.menu.labelFor(0)}.", camera.state.notice?.text)
    }

    @Test
    fun `a setting the camera does not offer, or a value it does not list, is refused before it is sent`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        val format = camera.state.settings.single { it.menu.name == "Format" }.menu.id

        camera.controller.changeSetting(0x7777, 0)!!.join()
        camera.controller.changeSetting(MenuIds.RECORD_RESOLUTION, 200)!!.join()
        camera.controller.changeSetting(format, 0)!!.join()
        camera.controller.changeSetting(MenuIds.WIFI_PASSWORD, 0)!!.join()

        assertTrue(camera.control.settingsWritten.isEmpty())
        assertEquals("The camera does not offer that setting.", camera.state.notice?.text)
    }

    @Test
    fun `settings are locked while the camera records`() = runTest {
        val camera = ControllerHarness(this)
        camera.control.isRecording = true
        camera.connected()

        camera.controller.changeSetting(MenuIds.RECORD_RESOLUTION, 1)!!.join()

        assertTrue(camera.control.settingsWritten.isEmpty())
        assertEquals("Settings cannot change while the camera records.", camera.state.notice?.text)
    }

    @Test
    fun `a notice is dismissed only by its own number`() = runTest {
        val camera = ControllerHarness(this)
        camera.connected()
        camera.controller.takePhoto()!!.join()
        val photo = camera.state.notice!!
        camera.controller.takePhoto()!!.join()
        val second = camera.state.notice!!

        camera.controller.dismissNotice(photo.id)
        assertEquals(second, camera.state.notice)

        camera.controller.dismissNotice(second.id)
        assertNull(camera.state.notice)
        assertTrue(second.id > photo.id)
    }
}
