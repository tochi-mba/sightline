package com.rextechnologies.sightline.playback

import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.protocol.media.AviSound
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** A clip's sound through the phone's AudioTrack, as Robolectric's stands in for it. */
@RunWith(AndroidJUnit4::class)
class AudioTrackDeviceTest {
    // 16 kHz mono 16-bit, as the reference camera records.
    private val camera = AviSound(16_000, 1, 16)

    @Test
    fun `the camera's sound opens and each piece is written after the last`() {
        val device = assertNotNull(AudioTrackDevice.open(camera))

        device.play(ByteArray(16_000))
        device.play(ByteArray(8_000))

        assertTrue(device.holding in 0..24_000L, "holding ${device.holding}")
        device.stop()
        assertEquals(0L, device.holding)
        device.play(ByteArray(4_000))
        assertTrue(device.holding in 0..4_000L, "holding ${device.holding}")
        device.close()
    }

    @Test
    fun `eight-bit and stereo sound open too`() {
        assertNotNull(AudioTrackDevice.open(AviSound(16_000, 2, 8))).close()
    }

    @Test
    fun `sound the phone cannot play has no device`() {
        assertNull(AudioTrackDevice.open(AviSound(16_000, 1, 24)))
        assertNull(AudioTrackDevice.open(AviSound(16_000, 6, 16)))
        assertNull(AudioTrackDevice.open(AviSound(-1, 1, 16)))
    }
}
