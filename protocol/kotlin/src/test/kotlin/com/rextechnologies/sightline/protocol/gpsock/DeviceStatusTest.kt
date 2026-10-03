package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.Golden
import com.rextechnologies.sightline.protocol.bytes
import java.time.Duration
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The device status, against the bytes the reference camera actually sent.
 *
 * `device-status-16byte.hex` is the first payload read, in record mode on USB power.
 * `device-status-sequence.txt` is a run of payloads read while a clip was recorded and a photo
 * taken, which is what pins the decoded fields down; its header says what moved and when.
 */
class DeviceStatusTest {
    private fun reference(): ByteArray = Golden.text("gpsock/device-status-16byte.hex").trim().hexToByteArray()

    private fun captured(label: String): DeviceStatus {
        for (line in Golden.text("gpsock/device-status-sequence.txt").lineSequence()) {
            val parts = line.trim().split(Regex("\\s+"))
            if (parts.size == 2 && parts[0] == label) {
                return DeviceStatus(parts[1].hexToByteArray())
            }
        }

        error("No captured status called $label.")
    }

    @Test
    fun `the reference camera answers with sixteen bytes not the documented twenty`() {
        assertEquals(16, DeviceStatus(reference()).length)
    }

    @Test
    fun `it reads the mode the camera was actually in`() {
        assertEquals(CameraMode.Record, DeviceStatus(reference()).mode)
        assertEquals(CameraMode.Capture, captured("capture-idle").mode)
    }

    @Test
    fun `it reads that the camera was on external power`() {
        assertTrue(DeviceStatus(reference()).onExternalPower)
        assertFalse(DeviceStatus(bytes(0, 0, 0, 0)).onExternalPower)
    }

    @Test
    fun `recording is read from the busy flag in record mode`() {
        assertFalse(captured("record-idle").isRecording)
        assertTrue(captured("recording-1s").isRecording)
        assertFalse(captured("record-stopped").isRecording)
    }

    @Test
    fun `busy in browse mode is playback not recording`() {
        val playingBack = DeviceStatus(bytes(CameraMode.Browse.code, 0b01, 0, 0))

        assertTrue(playingBack.isBusy)
        assertFalse(playingBack.isRecording)
    }

    @Test
    fun `while recording the clock counts the clip`() {
        assertEquals(Duration.ofSeconds(1), captured("recording-1s").clipLength)
        assertEquals(Duration.ofSeconds(3), captured("recording-3s").clipLength)
        assertNull(captured("recording-3s").recordTimeLeft)
    }

    @Test
    fun `when idle the same bytes count the video the card still holds`() {
        assertEquals(Duration.ofSeconds(45_861), captured("record-idle").recordTimeLeft)
        assertEquals(Duration.ofSeconds(45_857), captured("record-stopped").recordTimeLeft)
        assertNull(captured("record-idle").clipLength)
    }

    @Test
    fun `photos left drops by one when a photo is taken`() {
        assertEquals(31_997, captured("capture-idle").photosLeft)
        assertEquals(31_996, captured("capture-after-photo").photosLeft)
    }

    @Test
    fun `the resolutions match the settings they mirror`() {
        val status = captured("record-idle")

        assertEquals(0, status.recordResolution)
        assertEquals(0, status.photoResolution)
    }

    @Test
    fun `a payload that stops short reports its missing fields as unknown`() {
        val shortest = DeviceStatus(bytes(0, 0, 0x80, 1))

        assertNull(shortest.recordResolution)
        assertNull(shortest.photoResolution)
        assertNull(shortest.recordTimeLeft)
        assertNull(shortest.photosLeft)
    }

    @Test
    fun `a negative count is unknown rather than shown`() {
        val odd = "00028001000000008000000000800001".hexToByteArray()

        assertNull(DeviceStatus(odd).recordTimeLeft)
        assertNull(DeviceStatus(odd).photosLeft)
    }

    @Test
    fun `battery is reported as unknown rather than as a misread byte`() {
        assertNull(DeviceStatus(reference()).batteryPercent)
    }

    @Test
    fun `every byte stays available so diagnostics can show what is not understood`() {
        val status = DeviceStatus(reference())

        assertContentEquals(reference(), status.raw)
        assertContains(status.describe(), "000280010025B3000000FE7C0000A501")
        assertContains(status.describe(), "recording=false")
    }

    @Test
    fun `a payload too short to hold a mode is refused`() {
        assertFailsWith<GpSockProtocolException> { DeviceStatus(bytes(0x00, 0x02)) }
    }

    @Test
    fun `the busy and audio flags are read independently`() {
        assertTrue(DeviceStatus(bytes(0, 0b01, 0, 0)).isBusy)
        assertFalse(DeviceStatus(bytes(0, 0b01, 0, 0)).recordsAudio)
        assertTrue(DeviceStatus(bytes(0, 0b10, 0, 0)).recordsAudio)
        assertFalse(DeviceStatus(bytes(0, 0b10, 0, 0)).isBusy)
    }

    @Test
    fun `the diagnostics line names what is known`() {
        assertEquals(
            "mode=Record recording=false busy=false audio=true external-power=true " +
                "raw=000280010025B3000000FE7C0000A501",
            DeviceStatus(reference()).describe(),
        )
    }

    @Test
    fun `a mode this library does not know is reported as unknown rather than guessed`() {
        val status = DeviceStatus(bytes(7, 0, 0, 0))

        assertNull(status.mode)
        assertContains(status.describe(), "mode=7 ")
    }

    @Test
    fun `the bytes handed out are a copy that cannot change the status`() {
        val status = DeviceStatus(reference())

        status.raw.fill(0x7F)

        assertContentEquals(reference(), status.raw)
        assertEquals(CameraMode.Record, status.mode)
    }
}
