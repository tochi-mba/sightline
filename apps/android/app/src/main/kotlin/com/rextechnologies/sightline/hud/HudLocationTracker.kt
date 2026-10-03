package com.rextechnologies.sightline.hud

import android.annotation.SuppressLint
import android.content.Context
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.os.Bundle
import com.rextechnologies.sightline.core.model.HudReading
import java.io.Closeable

/** Reads GPS only while HUD is visible. Permission is requested by the activity before start. */
class HudLocationTracker(
    context: Context,
    private val onReading: (HudReading) -> Unit,
) : LocationListener, Closeable {
    private val locations = context.getSystemService(LocationManager::class.java)

    @SuppressLint("MissingPermission")
    fun start() {
        locations.requestLocationUpdates(LocationManager.GPS_PROVIDER, 500L, 0f, this)
    }

    override fun onLocationChanged(location: Location) {
        onReading(
            HudReading(
                speedKilometresPerHour = if (location.hasSpeed()) {
                    HudReading.metresPerSecondToKilometresPerHour(location.speed)
                } else {
                    null
                },
                altitudeMetres = if (location.hasAltitude()) location.altitude else null,
                headingDegrees = if (location.hasBearing()) location.bearing else null,
            ),
        )
    }

    @Deprecated("Deprecated by Android but still required by LocationListener below API 30.")
    override fun onStatusChanged(provider: String?, status: Int, extras: Bundle?) = Unit

    override fun onProviderEnabled(provider: String) = Unit

    override fun onProviderDisabled(provider: String) = Unit

    override fun close() {
        locations.removeUpdates(this)
    }
}
