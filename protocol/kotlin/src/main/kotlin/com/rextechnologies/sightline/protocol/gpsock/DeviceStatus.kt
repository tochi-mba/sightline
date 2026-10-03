package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.readInt32LittleEndian
import com.rextechnologies.sightline.protocol.unsignedAt
import java.time.Duration

/**
 * What the camera says about itself, from [GpSockCommand.GetDeviceStatus].
 *
 * Only the fields this project has actually pinned down are exposed as values. The reference
 * camera (firmware 20240708 V1.3) answers with **16** bytes, while the vendor source for a
 * different build writes **20**. Each field below is decoded because a sequence of real payloads
 * moved it on purpose — a clip recorded, a photo taken — and
 * `protocol/golden/gpsock/device-status-sequence.txt` holds those payloads.
 *
 * The rest stays in [raw], every uncertain field returns null, and the UI shows only what is known:
 * showing somebody a battery percentage that is a different field misread is worse than showing
 * nothing.
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

    /** Which mode the camera is in, or null for a mode this library does not know. */
    val mode: CameraMode?
        get() = CameraMode.fromCode(payload.unsignedAt(0))

    /** Whether the camera is busy: recording in record mode, playing back in browse mode. */
    val isBusy: Boolean
        get() = (payload[1].toInt() and 0x01) != 0

    /** Whether the camera is recording to its card. */
    val isRecording: Boolean
        get() = mode == CameraMode.Record && isBusy

    /** Whether the camera will record its own audio. */
    val recordsAudio: Boolean
        get() = (payload[1].toInt() and 0x02) != 0

    /** Whether the camera is on external power. Set while the reference camera was on USB. */
    val onExternalPower: Boolean
        get() = payload[3].toInt() != 0

    /** The Record Resolution setting's value id, or null when the payload is too short. */
    val recordResolution: Int?
        get() = payload.getOrNull(4)?.toInt()?.and(0xFF)

    /** The Capture Resolution setting's value id, or null when the payload is too short. */
    val photoResolution: Int?
        get() = payload.getOrNull(9)?.toInt()?.and(0xFF)

    /** How long the clip being recorded has run, or null when not recording. */
    val clipLength: Duration?
        get() = if (isRecording) seconds(5) else null

    /** How much more video the card holds at the current resolution, or null while recording. */
    val recordTimeLeft: Duration?
        get() = if (isRecording) null else seconds(5)

    /** How many more photos the card holds at the current resolution, or null when not reported. */
    val photosLeft: Int?
        get() = count(10)

    /** The battery level, or null because it is not yet decoded for this firmware. */
    val batteryPercent: Int?
        get() = null

    /** A line for the diagnostics page: what is known, and the bytes that are not. */
    fun describe(): String = "mode=${mode ?: payload.unsignedAt(0)} recording=$isRecording busy=$isBusy " +
        "audio=$recordsAudio external-power=$onExternalPower raw=${payload.toHexString(HexFormat.UpperCase)}"

    private fun seconds(offset: Int): Duration? = count(offset)?.let { Duration.ofSeconds(it.toLong()) }

    /** A little-endian count, or null when the payload stops short of it or it is negative. */
    private fun count(offset: Int): Int? {
        if (payload.size < offset + 4) {
            return null
        }

        return payload.readInt32LittleEndian(offset).takeIf { it >= 0 }
    }
}
