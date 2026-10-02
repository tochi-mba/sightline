package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.unsignedAt

/**
 * What the camera says about itself, from [GpSockCommand.GetDeviceStatus].
 *
 * Only the fields this project has actually pinned down are exposed as values. The reference
 * camera (firmware 20240708 V1.3) answers with **16** bytes, while the vendor documentation
 * for a later build describes **20**, and the two do not agree past the first few fields.
 * Decoding the rest from that documentation would mean showing somebody a battery percentage or
 * a free-space figure that is simply a different field misread — worse than showing nothing.
 *
 * So the undecoded bytes stay in [raw], every uncertain field is nullable and returns null, and the
 * UI shows only what is known. Each field is promoted out of "unknown" by an experiment that moves
 * it on purpose — charge the camera, pull the card, fill the storage — and the test that pins it
 * down carries the observation that justified it.
 *
 * @param payload The bytes the camera sent. Never copied defensively; treated as owned.
 * @throws GpSockProtocolException The payload is too short to hold even the mode.
 */
class DeviceStatus(private val payload: ByteArray) {
    init {
        if (payload.size < 4) {
            throw GpSockProtocolException("A device status needs at least 4 bytes; the camera sent ${payload.size}.")
        }
    }

    /** Every byte the camera sent, so diagnostics can show what is not yet understood. A copy, so it stays as sent. */
    val raw: ByteArray
        get() = payload.copyOf()

    /** How many bytes the camera sent. 16 on the reference camera. */
    val length: Int
        get() = payload.size

    /**
     * Which mode the camera is in. Confirmed: the camera reported record while recording.
     *
     * Null for a mode this library does not know, which [describe] still shows by number.
     */
    val mode: CameraMode?
        get() = CameraMode.fromCode(payload.unsignedAt(0))

    /** Whether the camera is busy recording or playing back. */
    val isBusy: Boolean
        get() = (payload[1].toInt() and 0x01) != 0

    /** Whether the camera will record its own audio. */
    val recordsAudio: Boolean
        get() = (payload[1].toInt() and 0x02) != 0

    /** Whether the camera is on external power. Confirmed: set while the reference camera was on USB. */
    val isCharging: Boolean
        get() = payload[3].toInt() != 0

    /**
     * The battery level, or null because it is not yet decoded for this firmware.
     *
     * The documented 20-byte layout puts a level in byte 2, but the reference camera reports `0x80`
     * there while on mains power, which reads as either "128" on a 0-100 scale or as a flag bit
     * rather than a level. Until the byte has been watched across a real discharge, this stays
     * unknown and the UI shows no battery figure.
     */
    val batteryPercent: Int?
        get() = null

    /**
     * Free space on the card, or null because it is not yet decoded.
     *
     * The 20-byte layout puts a 32-bit count at offset 16, which this 16-byte payload does not
     * reach. The figure must come from a build whose layout is known, or from an experiment that
     * fills the card by a known amount.
     */
    val freeSpaceBytes: Long?
        get() = null

    /** A line for the diagnostics page: what is known, and the bytes that are not. */
    fun describe(): String = "mode=${mode ?: payload.unsignedAt(0)} busy=$isBusy audio=$recordsAudio " +
        "charging=$isCharging raw=${payload.toHexString(HexFormat.UpperCase)}"
}
