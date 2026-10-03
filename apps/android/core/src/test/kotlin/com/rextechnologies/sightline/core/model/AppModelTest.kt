package com.rextechnologies.sightline.core.model

import com.rextechnologies.sightline.core.navigation.Destination
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue

class AppModelTest {
    @Test
    fun `new model is disconnected and has honest unknown values`() {
        val model = AppModel()

        assertEquals(Destination.Live, model.destination)
        assertFalse(model.connected)
        assertEquals(CameraLibrary(), model.library)
        assertEquals("--", model.hud.speedText)
        assertEquals("-- m", model.hud.altitudeText)
        assertEquals("---°", model.hud.headingText)
    }

    @Test
    fun `connected is true only for connected and each state carries useful context`() {
        assertFalse(AppModel(connection = ConnectionState.Disconnected).connected)
        assertFalse(AppModel(connection = ConnectionState.Connecting("ActionCam_1")).connected)
        assertFalse(AppModel(connection = ConnectionState.Failed("No camera answered")).connected)
        assertTrue(AppModel(connection = ConnectionState.Connected("ActionCam_1")).connected)
    }

    @Test
    fun `HUD converts and formats sensor values without inventing absent ones`() {
        val reading = HudReading(
            speedKilometresPerHour = HudReading.metresPerSecondToKilometresPerHour(10f),
            altitudeMetres = 124.6,
            headingDegrees = -1f,
        )

        assertEquals("36", reading.speedText)
        assertEquals("125 m", reading.altitudeText)
        assertEquals("359°", reading.headingText)
        assertEquals("001°", HudReading(headingDegrees = 361f).headingText)
    }

    @Test
    fun `library rejects an impossible negative count`() {
        assertFailsWith<IllegalArgumentException> { CameraLibrary(count = -1) }
    }

    @Test
    fun `models compare frame bytes by content and include them in the hash`() {
        val first = AppModel(jpeg = byteArrayOf(1, 2, 3))
        val same = AppModel(jpeg = byteArrayOf(1, 2, 3))
        val other = AppModel(jpeg = byteArrayOf(1, 2, 4))

        assertEquals(first, same)
        assertEquals(first.hashCode(), same.hashCode())
        assertNotEquals(first, other)
        assertNotEquals(first, "not a model")
        assertEquals(AppModel(), AppModel())
    }

    @Test
    fun `all screen facing model fields participate in equality`() {
        val baseline = AppModel()
        val changes = listOf(
            baseline.copy(destination = Destination.Settings),
            baseline.copy(connection = ConnectionState.Connected("camera")),
            baseline.copy(framesPerSecond = 12.0),
            baseline.copy(recording = true),
            baseline.copy(hudVisible = true),
            baseline.copy(hud = HudReading(1.0, 2.0, 3f)),
            baseline.copy(sentryRunning = true),
            baseline.copy(motionEvents = 1),
            baseline.copy(library = CameraLibrary(1)),
            baseline.copy(message = "message"),
        )

        changes.forEach { changed -> assertNotEquals(baseline, changed) }
        assertNotEquals(baseline.hashCode(), baseline.copy(recording = true).hashCode())
    }
}
