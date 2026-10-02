package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.Golden
import com.rextechnologies.sightline.protocol.bytes
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
 * The reference payload is `000280010025b3000000fe7c0000a501`: sixteen bytes, read on
 * 2026-10-02 while the camera sat in record mode on USB power. The vendor documentation for a
 * later firmware describes twenty bytes with a different layout, so the fields past the first few
 * are deliberately not decoded — see [DeviceStatus].
 */
class DeviceStatusTest {
    private fun reference(): ByteArray = Golden.text("gpsock/device-status-16byte.hex").trim().hexToByteArray()

    @Test
    fun `the reference camera answers with sixteen bytes not the documented twenty`() {
        assertEquals(16, DeviceStatus(reference()).length)
    }

    @Test
    fun `it reads the mode the camera was actually in`() {
        assertEquals(CameraMode.Record, DeviceStatus(reference()).mode)
    }

    @Test
    fun `it reads that the camera was on external power`() {
        // The camera was plugged into USB when this was captured.
        assertTrue(DeviceStatus(reference()).isCharging)
    }

    @Test
    fun `it reads that the camera was not recording`() {
        assertFalse(DeviceStatus(reference()).isBusy)
    }

    @Test
    fun `battery is reported as unknown rather than as a misread byte`() {
        // The documented layout puts a level in byte 2, but this firmware reports 0x80 there on
        // mains power, which is not a percentage. Showing nothing beats showing "128%".
        assertNull(DeviceStatus(reference()).batteryPercent)
    }

    @Test
    fun `free space is reported as unknown because this payload does not reach it`() {
        assertNull(DeviceStatus(reference()).freeSpaceBytes)
    }

    @Test
    fun `every byte stays available so diagnostics can show what is not understood`() {
        val status = DeviceStatus(reference())

        assertContentEquals(reference(), status.raw)
        assertContains(status.describe(), "000280010025B3000000FE7C0000A501")
    }

    @Test
    fun `a payload too short to hold a mode is refused`() {
        assertFailsWith<GpSockProtocolException> { DeviceStatus(bytes(0x00, 0x02)) }
    }

    @Test
    fun `a camera in browse mode reads as browse`() {
        assertEquals(CameraMode.Browse, DeviceStatus(bytes(CameraMode.Browse.code, 0, 0, 0)).mode)
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
            "mode=Record busy=false audio=true charging=true raw=000280010025B3000000FE7C0000A501",
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
    fun `a camera on its battery is not read as charging`() {
        assertFalse(DeviceStatus(bytes(0, 0, 0, 0)).isCharging)
    }

    @Test
    fun `the bytes handed out are a copy that cannot change the status`() {
        val status = DeviceStatus(reference())

        status.raw.fill(0x7F)

        assertContentEquals(reference(), status.raw)
        assertEquals(CameraMode.Record, status.mode)
    }
}
