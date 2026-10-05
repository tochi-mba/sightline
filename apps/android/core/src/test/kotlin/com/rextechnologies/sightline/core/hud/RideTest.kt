package com.rextechnologies.sightline.core.hud

import com.rextechnologies.sightline.core.settings.UnitSystem
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

class RideTest {
    /** A fix [step] steps north of the equator, a ten-thousandth of a degree (about 11.1 m) each, a second apart. */
    private fun north(step: Int, accuracy: Double? = 5.0, speed: Double? = 11.1, at: Duration = step.seconds) =
        Fix(at = at, latitude = step * 0.0001, longitude = 0.0, speed = speed, accuracy = accuracy)

    @Test
    fun `the distance between two fixes is along the earth's surface`() {
        val oneDegree = Ride.metresBetween(Fix(Duration.ZERO, 0.0, 0.0), Fix(Duration.ZERO, 1.0, 0.0))

        assertEquals(111_194.9, oneDegree, 0.1)
    }

    @Test
    fun `fixes the gps is sure of add up to the distance ridden`() {
        val ride = Ride()

        (0..10).forEach { ride.add(north(it)) }

        assertEquals(111.19, ride.distance, 0.01)
        assertEquals(11.1, ride.topSpeed)
        assertEquals(10.seconds, ride.moving)
    }

    @Test
    fun `fixes the gps is unsure of are not counted, and the next sure one is measured from the last`() {
        val ride = Ride()

        ride.add(north(0))
        ride.add(north(1, accuracy = 80.0))
        ride.add(north(2, accuracy = null))
        ride.add(north(3))

        assertEquals(33.36, ride.distance, 0.01)
    }

    @Test
    fun `a glitch adds nothing and the ride carries on from the last good fix`() {
        val ride = Ride()
        ride.add(north(0))

        ride.add(north(1).copy(latitude = 1.0))
        ride.add(north(2))

        assertEquals(22.24, ride.distance, 0.01)
    }

    @Test
    fun `a jump that lasts is taken as a move the gps missed, without counting the gap`() {
        val ride = Ride()
        ride.add(north(0))

        ride.add(Fix(at = 12.seconds, latitude = 1.0, longitude = 0.0, accuracy = 5.0))
        ride.add(Fix(at = 13.seconds, latitude = 1.0001, longitude = 0.0, accuracy = 5.0))

        assertEquals(11.12, ride.distance, 0.01)
    }

    @Test
    fun `standing still is not moving, and a fix without speed does not lower the top speed`() {
        val ride = Ride()
        ride.add(north(0, speed = 20.0))

        ride.add(north(1, speed = 0.5))
        ride.add(north(2, speed = null))

        assertEquals(20.0, ride.topSpeed)
        assertEquals(Duration.ZERO, ride.moving)
    }

    @Test
    fun `a fix older than the last is ignored`() {
        val ride = Ride()
        ride.add(north(5))

        ride.add(north(4))
        ride.add(north(5))

        assertEquals(north(5), ride.latest)
        assertEquals(0.0, ride.distance)
    }

    @Test
    fun `numbers read in the rider's units, whole, with a dash for unknown`() {
        val metric = HudFormat(UnitSystem.Metric)
        val imperial = HudFormat(UnitSystem.Imperial)

        assertEquals("40", metric.speed(11.1))
        assertEquals("25", imperial.speed(11.1))
        assertEquals("–", metric.speed(null))
        assertEquals("km/h", metric.speedUnit)
        assertEquals("mph", imperial.speedUnit)
        assertEquals("120 m", metric.altitude(120.4))
        assertEquals("395 ft", imperial.altitude(120.4))
        assertEquals("–", imperial.altitude(null))
        assertEquals("12.3 km", metric.distance(12_345.0))
        assertEquals("7.7 mi", imperial.distance(12_345.0))
        assertEquals("0.0 km", metric.distance(0.0))
    }

    @Test
    fun `a heading reads as a compass point and a bearing`() {
        val format = HudFormat(UnitSystem.Metric)

        assertEquals("N 000°", format.heading(0.0))
        assertEquals("N 022°", format.heading(22.4))
        assertEquals("NE 045°", format.heading(45.0))
        assertEquals("W 270°", format.heading(-90.0))
        assertEquals("N 000°", format.heading(359.6))
        assertEquals("SE 135°", format.heading(495.0))
        assertEquals("–", format.heading(null))
    }

    @Test
    fun `ride time reads as minutes and seconds, with hours once there are any`() {
        val format = HudFormat(UnitSystem.Metric)

        assertEquals("0:00", format.duration(Duration.ZERO))
        assertEquals("5:09", format.duration(309.seconds))
        assertEquals("1:05:09", format.duration(3909.seconds))
    }

    @Test
    fun `a fix keeps everything the gps said`() {
        val fix = Fix(1.seconds, 51.5, -0.12, speed = 3.0, altitude = 12.0, bearing = 90.0, accuracy = 4.0)

        assertEquals(12.0, fix.altitude)
        assertEquals(90.0, fix.bearing)
    }
}
