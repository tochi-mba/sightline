package com.rextechnologies.sightline.hud

import android.content.Context
import android.location.Location
import android.location.LocationManager
import android.os.SystemClock
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Shadows.shadowOf
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.nanoseconds

@RunWith(AndroidJUnit4::class)
class HudFeedTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val locations = context.getSystemService(LocationManager::class.java)

    private fun location(seconds: Long, latitude: Double, speed: Float? = null) = Location(
        LocationManager.GPS_PROVIDER,
    ).apply {
        this.latitude = latitude
        longitude = 0.0
        elapsedRealtimeNanos = seconds * 1_000_000_000
        time = System.currentTimeMillis()
        accuracy = 5f
        speed?.let { this.speed = it }
    }

    @Test
    fun `the gps runs only while the hud shows, and each fix adds to the ride`() {
        val feed = HudFeed(context)

        feed.start()
        feed.start()
        assertEquals(1, shadowOf(locations).getLocationRequests(LocationManager.GPS_PROVIDER).size)
        feed.onLocationChanged(location(1, 0.0, speed = 10f))
        feed.onLocationChanged(location(2, 0.0001, speed = 11f))

        val reading = feed.reading.value
        assertEquals(11.12, reading.distance, 0.01)
        assertEquals(11.0, reading.topSpeed)
        assertEquals(1, reading.moving.inWholeSeconds)

        feed.stop()
        assertTrue(shadowOf(locations).getLocationRequests(LocationManager.GPS_PROVIDER).isEmpty())
        feed.start()
        assertNull(feed.reading.value.fix)
    }

    @Test
    fun `a fix keeps only what the gps measured`() {
        val bare = Location(LocationManager.GPS_PROVIDER).apply {
            latitude = 51.5
            longitude = -0.12
            elapsedRealtimeNanos = SystemClock.elapsedRealtimeNanos()
        }
        val full = Location(bare).apply {
            speed = 3f
            altitude = 12.0
            bearing = 90f
            accuracy = 4f
        }

        val fix = HudFeed.fix(bare)
        assertNull(fix.speed)
        assertNull(fix.altitude)
        assertNull(fix.bearing)
        assertNull(fix.accuracy)
        assertEquals(bare.elapsedRealtimeNanos.nanoseconds, fix.at)
        val measured = HudFeed.fix(full)
        assertEquals(3.0, measured.speed)
        assertEquals(12.0, measured.altitude)
        assertEquals(90.0, measured.bearing)
        assertEquals(4.0, measured.accuracy)
    }
}
