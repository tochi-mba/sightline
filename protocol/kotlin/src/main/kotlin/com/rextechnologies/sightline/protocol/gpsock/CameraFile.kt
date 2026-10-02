package com.rextechnologies.sightline.protocol.gpsock

import com.rextechnologies.sightline.protocol.readUInt16LittleEndian
import com.rextechnologies.sightline.protocol.readUInt32LittleEndian
import com.rextechnologies.sightline.protocol.unsignedAt
import java.time.DateTimeException
import java.time.LocalDateTime

/** What kind of file the camera says a card entry is. */
enum class CameraFileKind {
    /** A photograph. */
    Photo,

    /** A video. */
    Video,

    /** A video the camera has protected from loop recording. */
    ProtectedVideo,

    /** A video the camera saved after a jolt or an emergency press. */
    EmergencyVideo,

    /** Something this project does not recognise; listed rather than hidden. */
    Other,
}

/**
 * One file on the camera's card, as [GpSockCommand.PlaybackGetFileList] describes it.
 *
 * Each entry on the wire is at least 13 bytes: a type letter, a 16-bit little-endian index, the
 * time it was taken as six bytes (year since 2000, month, day, hour, minute, second), and a 32-bit
 * little-endian size in kilobytes. The index is what every other playback command takes.
 *
 * The time is the camera's own clock, which is only as right as somebody last set it — the
 * reference camera's was about two years out — and an impossible date is reported as unknown
 * rather than invented.
 *
 * @property code The type letter the camera sent, such as `J` for a photograph.
 * @property index The camera's index for the file, used by every other playback command.
 * @property taken When the camera's clock says it was taken, or null when that is not a real date.
 * @property sizeKilobytes The size the camera reports, in kilobytes.
 */
data class CameraFile(val code: Char, val index: Int, val taken: LocalDateTime?, val sizeKilobytes: Long) {
    /** What kind of file this is. */
    val kind: CameraFileKind
        get() = when (code) {
            'J' -> CameraFileKind.Photo
            'A', 'V' -> CameraFileKind.Video
            'L', 'K' -> CameraFileKind.ProtectedVideo
            'S', 'O' -> CameraFileKind.EmergencyVideo
            else -> CameraFileKind.Other
        }

    /** Whether this is a photograph. */
    val isPhoto: Boolean
        get() = kind == CameraFileKind.Photo

    /** Whether this is a video of any kind. */
    val isVideo: Boolean
        get() = kind == CameraFileKind.Video ||
            kind == CameraFileKind.ProtectedVideo ||
            kind == CameraFileKind.EmergencyVideo

    /** The size in bytes, as near as the camera's kilobyte figure allows. */
    val approximateBytes: Long
        get() = sizeKilobytes * 1024

    /**
     * A name for the file, in the camera's own style.
     *
     * The list carries no name, so this is built from the kind and the index. The extension is
     * settled when the file is downloaded, from what its bytes actually are.
     */
    val displayName: String
        get() {
            val prefix = when (kind) {
                CameraFileKind.Photo -> "PICT"
                CameraFileKind.ProtectedVideo -> "LOCK"
                CameraFileKind.EmergencyVideo -> "SOS"
                CameraFileKind.Video -> "MOVI"
                CameraFileKind.Other -> "FILE"
            }
            // Padded by hand rather than formatted, which would write the digits of the phone's language.
            return prefix + index.toString().padStart(4, '0')
        }

    companion object {
        /** The fewest bytes a list entry can have. */
        const val MINIMUM_ENTRY_LENGTH = 13

        /**
         * Reads one page of the file list.
         *
         * @param payload The answer: a count, then that many equal-sized entries.
         * @throws GpSockProtocolException The page does not divide into whole entries.
         */
        fun parsePage(payload: ByteArray): List<CameraFile> {
            if (payload.isEmpty() || payload[0].toInt() == 0) {
                return emptyList()
            }

            val count = payload.unsignedAt(0)
            val bytes = payload.size - 1
            if (bytes % count != 0 || bytes / count < MINIMUM_ENTRY_LENGTH) {
                throw GpSockProtocolException("A file-list page of $count entries cannot be $bytes bytes long.")
            }

            // Each entry is the page divided by the count, so a firmware that appends a field of its
            // own still has the fields before it read where they are.
            val size = bytes / count
            return List(count) { entry(payload, 1 + it * size) }
        }

        private fun entry(page: ByteArray, at: Int) = CameraFile(
            page.unsignedAt(at).toChar(),
            page.readUInt16LittleEndian(at + 1),
            takenAt(page, at + 3),
            page.readUInt32LittleEndian(at + 9),
        )

        private fun takenAt(page: ByteArray, at: Int): LocalDateTime? = try {
            LocalDateTime.of(
                2000 + page.unsignedAt(at),
                page.unsignedAt(at + 1),
                page.unsignedAt(at + 2),
                page.unsignedAt(at + 3),
                page.unsignedAt(at + 4),
                page.unsignedAt(at + 5),
            )
        } catch (impossible: DateTimeException) {
            null
        }
    }
}
