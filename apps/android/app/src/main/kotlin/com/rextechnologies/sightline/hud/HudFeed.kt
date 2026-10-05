package com.rextechnologies.sightline.hud

import android.annotation.SuppressLint
import android.content.Context
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.os.Looper
import com.rextechnologies.sightline.core.hud.Fix
import com.rextechnologies.sightline.core.hud.Ride
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlin.time.Duration
import kotlin.time.Duration.Companion.nanoseconds

/**
 * What the HUD shows: the latest fix and the ride so far.
 *
 * @property fix The newest GPS reading, or null before the first.
 * @property distance Metres ridden since the HUD was opened.
 * @property topSpeed The highest speed, metres a second.
 * @property moving How long the rider has been moving.
 */
data class HudReading(
    val fix: Fix? = null,
    val distance: Double = 0.0,
    val topSpeed: Double = 0.0,
    val moving: Duration = Duration.ZERO,
)

/**
 * The phone's GPS, for the HUD, read only while the HUD is showing.
 *
 * A fix a second is plenty for a speedometer read at a glance and costs far less battery than the GPS's
 * fastest rate. Starting it needs the location permission, which the screen asks for first; nothing else
 * in the app uses location.
 */
class HudFeed(context: Context) : LocationListener {
    private val locations: LocationManager = context.getSystemService(LocationManager::class.java)
    private val mutableReading = MutableStateFlow(HudReading())
    private var ride = Ride()
    private var running = false

    /** The HUD's readings as they arrive. */
    val reading: StateFlow<HudReading> = mutableReading.asStateFlow()

    /** Starts a new ride and the GPS. The caller has the location permission. */
    @SuppressLint("MissingPermission")
    fun start() {
        if (running) {
            return
        }

        running = true
        ride = Ride()
        mutableReading.value = HudReading()
        locations.requestLocationUpdates(
            LocationManager.GPS_PROVIDER,
            INTERVAL_MILLIS,
            0f,
            this,
            Looper.getMainLooper(),
        )
    }

    /** Stops the GPS. */
    fun stop() {
        running = false
        locations.removeUpdates(this)
    }

    override fun onLocationChanged(location: Location) {
        ride.add(fix(location))
        mutableReading.value = HudReading(ride.latest, ride.distance, ride.topSpeed, ride.moving)
    }

    companion object {
        private const val INTERVAL_MILLIS = 1_000L

        /** A platform location as the core's fix, leaving out whatever the GPS did not measure. */
        fun fix(location: Location): Fix = Fix(
            at = location.elapsedRealtimeNanos.nanoseconds,
            latitude = location.latitude,
            longitude = location.longitude,
            speed = location.speed.toDouble().takeIf { location.hasSpeed() },
            altitude = location.altitude.takeIf { location.hasAltitude() },
            bearing = location.bearing.toDouble().takeIf { location.hasBearing() },
            accuracy = location.accuracy.toDouble().takeIf { location.hasAccuracy() },
        )
    }
}
