package com.rextechnologies.sightline.core.hud

import com.rextechnologies.sightline.core.settings.UnitSystem
import kotlin.math.asin
import kotlin.math.cos
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.sqrt
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * One reading from the phone's GPS.
 *
 * @property at When it was taken, on the phone's monotonic clock.
 * @property latitude Degrees north.
 * @property longitude Degrees east.
 * @property speed Metres a second, from the GPS's own Doppler measurement, when it gave one.
 * @property altitude Metres above sea level, when it gave one.
 * @property bearing Degrees clockwise from north, when it gave one; meaningless when standing still.
 * @property accuracy How far off the position may be, in metres, when it said.
 */
data class Fix(
    val at: Duration,
    val latitude: Double,
    val longitude: Double,
    val speed: Double? = null,
    val altitude: Double? = null,
    val bearing: Double? = null,
    val accuracy: Double? = null,
)

/**
 * A ride, built up fix by fix: how far, how fast at most, and for how long moving.
 *
 * Speed is taken from the GPS's own measurement rather than worked out from positions, which jitter.
 * Distance adds up only fixes the GPS was sure of, and a fix that would mean an impossible speed is a GPS
 * glitch rather than a teleport, so neither a tunnel nor a bad satellite adds phantom kilometres.
 */
class Ride {
    private var lastCounted: Fix? = null

    /** The newest fix, whatever its accuracy. */
    var latest: Fix? = null
        private set

    /** Metres travelled. */
    var distance: Double = 0.0
        private set

    /** The highest speed seen, metres a second. */
    var topSpeed: Double = 0.0
        private set

    /** How long the rider has been moving faster than walking pace. */
    var moving: Duration = Duration.ZERO
        private set

    /** Adds [fix] to the ride. A fix older than the last one is ignored: fixes can arrive out of order. */
    fun add(fix: Fix) {
        val previous = latest
        if (previous != null && fix.at <= previous.at) {
            return
        }

        latest = fix
        fix.speed?.let { topSpeed = maxOf(topSpeed, it) }
        if (previous != null && (fix.speed ?: 0.0) >= MOVING_SPEED) {
            moving += fix.at - previous.at
        }

        if ((fix.accuracy ?: Double.MAX_VALUE) > TRUSTED_ACCURACY) {
            return
        }

        val counted = lastCounted
        if (counted == null) {
            lastCounted = fix
            return
        }

        val step = metresBetween(counted, fix)
        val gap = fix.at - counted.at
        when {
            step / (gap.inWholeMilliseconds / 1000.0) <= IMPOSSIBLE_SPEED -> {
                distance += step
                lastCounted = fix
            }

            // A glitch is brief. A jump that lasts is a real move the GPS missed, through a tunnel or on a
            // train, so after a while the ride carries on from the new place without counting the gap.
            gap >= reanchorAfter -> lastCounted = fix

            // Otherwise a glitch: measuring carries on from the last good fix as if it never happened.
            else -> Unit
        }
    }

    companion object {
        /** Faster than this, metres a second, is moving: a brisk walk. */
        const val MOVING_SPEED = 1.5

        /** A position the GPS is surer of than this, in metres, is counted towards distance. */
        const val TRUSTED_ACCURACY = 25.0

        /** Faster than this, metres a second (about 400 km/h), is a GPS glitch, not a ride. */
        const val IMPOSSIBLE_SPEED = 110.0

        private const val EARTH_RADIUS = 6_371_000.0

        /** The great-circle distance between two fixes, in metres. */
        fun metresBetween(from: Fix, to: Fix): Double {
            val lat1 = Math.toRadians(from.latitude)
            val lat2 = Math.toRadians(to.latitude)
            val dLat = lat2 - lat1
            val dLon = Math.toRadians(to.longitude - from.longitude)
            val h = sin(dLat / 2) * sin(dLat / 2) + cos(lat1) * cos(lat2) * sin(dLon / 2) * sin(dLon / 2)
            return 2 * EARTH_RADIUS * asin(sqrt(h))
        }
    }
}

/**
 * The HUD's numbers in the rider's units.
 *
 * Whole numbers only: a speed that flickers between 41.6 and 41.7 is unreadable at a glance, and a rider
 * only ever glances.
 */
class HudFormat(private val units: UnitSystem) {
    /** The speed unit's label. */
    val speedUnit: String get() = if (units == UnitSystem.Metric) "km/h" else "mph"

    /** [metresPerSecond] as a whole number in the rider's unit, or a dash when unknown. */
    fun speed(metresPerSecond: Double?): String {
        val perUnit = if (units == UnitSystem.Metric) KMH_PER_MPS else MPH_PER_MPS
        return metresPerSecond?.let { (it * perUnit).roundToInt().toString() } ?: DASH
    }

    /** [metres] above sea level, in metres or feet, or a dash when unknown. */
    fun altitude(metres: Double?): String = metres?.let {
        if (units == UnitSystem.Metric) "${it.roundToInt()} m" else "${(it * FEET_PER_METRE).roundToInt()} ft"
    } ?: DASH

    /** [metres] travelled, to a tenth of a kilometre or mile. */
    fun distance(metres: Double): String {
        val amount = metres / if (units == UnitSystem.Metric) METRES_PER_KM else METRES_PER_MILE
        val tenths = (amount * 10).roundToInt()
        return "${tenths / 10}.${tenths % 10} ${if (units == UnitSystem.Metric) "km" else "mi"}"
    }

    /** [degrees] as a compass point and a three-digit bearing, or a dash when unknown. */
    fun heading(degrees: Double?): String = degrees?.let {
        val normal = ((it % 360) + 360) % 360
        val point = compassPoints[((normal + 22.5) / 45).toInt() % compassPoints.size]
        "$point ${normal.roundToInt().mod(360).toString().padStart(3, '0')}°"
    } ?: DASH

    /** [elapsed] as hours, minutes and seconds: 1:05:09, or 5:09 under an hour. */
    fun duration(elapsed: Duration): String {
        val total = elapsed.inWholeSeconds
        val hours = total / 3600
        val minutes = (total % 3600) / 60
        val seconds = (total % 60).toString().padStart(2, '0')
        return if (hours > 0) "$hours:${minutes.toString().padStart(2, '0')}:$seconds" else "$minutes:$seconds"
    }

    private companion object {
        const val DASH = "–"
        const val KMH_PER_MPS = 3.6
        const val MPH_PER_MPS = 2.236936
        const val FEET_PER_METRE = 3.28084
        const val METRES_PER_KM = 1000.0
        const val METRES_PER_MILE = 1609.344
    }
}

/** How long a jump must last to be taken as a real move rather than a glitch. */
private val reanchorAfter = 10.seconds

private val compassPoints = listOf("N", "NE", "E", "SE", "S", "SW", "W", "NW")
