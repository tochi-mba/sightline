package com.rextechnologies.sightline.core.model

import com.rextechnologies.sightline.core.navigation.Destination
import com.rextechnologies.sightline.protocol.gpsock.MenuSetting

/** What the camera connection is doing, expressed without Android types. */
sealed interface ConnectionState {
    data object Disconnected : ConnectionState

    data class Connecting(val networkName: String) : ConnectionState

    data class Connected(val networkName: String) : ConnectionState

    data class Failed(val message: String) : ConnectionState
}

/** A file count is all the reference firmware exposes until the list protocol is fully decoded. */
data class CameraLibrary(val count: Int? = null, val loading: Boolean = false) {
    init {
        require(count == null || count >= 0) { "A camera cannot contain a negative number of files." }
    }
}

/** Values drawn over live video in HUD mode. Null means the sensor has not supplied the value. */
data class HudReading(
    val speedKilometresPerHour: Double? = null,
    val altitudeMetres: Double? = null,
    val headingDegrees: Float? = null,
) {
    val speedText: String get() = speedKilometresPerHour?.let { "%.0f".format(it) } ?: "--"
    val altitudeText: String get() = altitudeMetres?.let { "%.0f m".format(it) } ?: "-- m"
    val headingText: String get() = headingDegrees?.let { "%03.0f°".format(normaliseHeading(it)) } ?: "---°"

    companion object {
        fun metresPerSecondToKilometresPerHour(value: Float): Double = value * 3.6

        private fun normaliseHeading(value: Float): Float = ((value % 360f) + 360f) % 360f
    }
}

/** The complete screen-facing state. JPEG bytes stay encoded until the Android UI draws them. */
data class AppModel(
    val destination: Destination = Destination.Start,
    val connection: ConnectionState = ConnectionState.Disconnected,
    val jpeg: ByteArray? = null,
    val framesPerSecond: Double = 0.0,
    val recording: Boolean = false,
    val hudVisible: Boolean = false,
    val hud: HudReading = HudReading(),
    val sentryRunning: Boolean = false,
    val motionEvents: Int = 0,
    val settings: List<MenuSetting> = emptyList(),
    val library: CameraLibrary = CameraLibrary(),
    val message: String? = null,
) {
    val connected: Boolean get() = connection is ConnectionState.Connected

    /** Byte arrays are values here, so equality compares their contents rather than their identity. */
    override fun equals(other: Any?): Boolean =
        other is AppModel &&
            destination == other.destination &&
            connection == other.connection &&
            jpeg.contentEquals(other.jpeg) &&
            framesPerSecond == other.framesPerSecond &&
            recording == other.recording &&
            hudVisible == other.hudVisible &&
            hud == other.hud &&
            sentryRunning == other.sentryRunning &&
            motionEvents == other.motionEvents &&
            settings == other.settings &&
            library == other.library &&
            message == other.message

    override fun hashCode(): Int {
        var result = destination.hashCode()
        result = 31 * result + connection.hashCode()
        result = 31 * result + (jpeg?.contentHashCode() ?: 0)
        result = 31 * result + framesPerSecond.hashCode()
        result = 31 * result + recording.hashCode()
        result = 31 * result + hudVisible.hashCode()
        result = 31 * result + hud.hashCode()
        result = 31 * result + sentryRunning.hashCode()
        result = 31 * result + motionEvents
        result = 31 * result + settings.hashCode()
        result = 31 * result + library.hashCode()
        result = 31 * result + (message?.hashCode() ?: 0)
        return result
    }
}
