package com.rextechnologies.sightline.core.camera

import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import com.rextechnologies.sightline.protocol.gpsock.DeviceStatus
import com.rextechnologies.sightline.protocol.gpsock.MenuChoice
import com.rextechnologies.sightline.protocol.gpsock.MenuSetting
import com.rextechnologies.sightline.protocol.gpsock.MenuSettingKind
import java.time.Duration
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CameraStateTest {
    private val resolution =
        MenuSetting(
            0,
            "Resolution",
            "Record",
            MenuSettingKind.Choice,
            0,
            listOf(MenuChoice(0, "4K"), MenuChoice(1, "1080P")),
        )

    @Test
    fun `a choice shows its label, text shows itself, and an unknown value shows nothing`() {
        assertEquals("1080P", CameraSetting(resolution, 1, null).shown)
        assertEquals("7", CameraSetting(resolution, 7, null).shown)
        val name = MenuSetting(0x300, "WifiName", "Wifi", MenuSettingKind.Text, 0, emptyList())
        assertEquals("ActionCam_1", CameraSetting(name, null, "ActionCam_1").shown)
        assertNull(CameraSetting(name, null, null).shown)
    }

    @Test
    fun `only a choice with something to choose is offered for change`() {
        assertTrue(CameraSetting(resolution, 0, null).isChangeable)
        assertFalse(CameraSetting(resolution.copy(choices = emptyList()), 0, null).isChangeable)
        assertFalse(CameraSetting(resolution.copy(kind = MenuSettingKind.Text), null, "x").isChangeable)
        assertFalse(CameraSetting(resolution.copy(kind = MenuSettingKind.Action), null, null).isChangeable)
        assertFalse(CameraSetting(resolution.copy(kind = null), null, null).isChangeable)
    }

    @Test
    fun `a status is shown as the camera decoded it`() {
        val recording = ByteArray(16).also {
            it[0] = CameraMode.Record.code.toByte()
            it[1] = 1
            it[3] = 1
            it[5] = 42
        }

        val status = CameraStatus.of(DeviceStatus(recording))

        assertEquals(CameraMode.Record, status.mode)
        assertTrue(status.isRecording)
        assertTrue(status.onExternalPower)
        assertEquals(Duration.ofSeconds(42), status.clipLength)
        assertNull(status.recordTimeLeft)
    }

    @Test
    fun `a thumbnail is equal to another with the same bytes`() {
        val one = Thumbnail(byteArrayOf(1, 2, 3))

        assertEquals(one, Thumbnail(byteArrayOf(1, 2, 3)))
        assertEquals(one.hashCode(), Thumbnail(byteArrayOf(1, 2, 3)).hashCode())
        assertNotEquals(one, Thumbnail(byteArrayOf(1, 2)))
        assertFalse(one.equals("not a thumbnail"))
    }

    @Test
    fun `the capture modes are the camera's record and capture modes`() {
        assertEquals(CameraMode.Record, CaptureMode.Video.cameraMode)
        assertEquals(CameraMode.Capture, CaptureMode.Photo.cameraMode)
    }

    @Test
    fun `a state is connected and recording only when the camera says so`() {
        assertFalse(CameraState().isConnected)
        assertFalse(CameraState().isRecording)
        assertTrue(CameraState(connection = Connection.Connected).isConnected)
    }

    @Test
    fun `each state carries what it says`() {
        assertEquals(CameraNetwork(name = "Trail"), Connection.Joining(CameraNetwork(name = "Trail")).network)
        assertEquals(500L, Transfer.Copying(100, 500).expected)
        assertEquals("Gallery/PICT0001.jpg", Transfer.Saved("Gallery/PICT0001.jpg").where)
        assertEquals("No space.", Transfer.Failed("No space.").reason)
        assertEquals(3, Thumbnail(byteArrayOf(1, 2, 3)).jpeg.size)
    }
}
