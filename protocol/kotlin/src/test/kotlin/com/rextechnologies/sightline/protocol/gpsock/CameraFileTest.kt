package com.rextechnologies.sightline.protocol.gpsock

import java.time.LocalDateTime
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** A page of the camera's file list, read the way the firmware writes it. */
class CameraFileTest {
    @Test
    fun `the type letter says what kind of file it is`() {
        val cases = listOf(
            Kind('J', CameraFileKind.Photo, "PICT0007", photo = true, video = false),
            Kind('A', CameraFileKind.Video, "MOVI0007", photo = false, video = true),
            Kind('V', CameraFileKind.Video, "MOVI0007", photo = false, video = true),
            Kind('L', CameraFileKind.ProtectedVideo, "LOCK0007", photo = false, video = true),
            Kind('K', CameraFileKind.ProtectedVideo, "LOCK0007", photo = false, video = true),
            Kind('S', CameraFileKind.EmergencyVideo, "SOS0007", photo = false, video = true),
            Kind('O', CameraFileKind.EmergencyVideo, "SOS0007", photo = false, video = true),
            Kind('X', CameraFileKind.Other, "FILE0007", photo = false, video = false),
        )

        for (case in cases) {
            val file = CameraFile.parsePage(page(entry(case.code, 7, 24, 1, 31, 9, 15, 0, 2048)))[0]

            assertEquals(case.code, file.code)
            assertEquals(case.kind, file.kind, "the kind of '${case.code}'")
            assertEquals(case.name, file.displayName, "the name of '${case.code}'")
            assertEquals(case.photo, file.isPhoto, "whether '${case.code}' is a photograph")
            assertEquals(case.video, file.isVideo, "whether '${case.code}' is a video")
        }
    }

    @Test
    fun `an entry carries its index, the time it was taken and its size`() {
        val file = CameraFile.parsePage(page(entry('J', 513, 24, 1, 31, 9, 15, 42, 3100)))[0]

        assertEquals(513, file.index)
        assertEquals(LocalDateTime.of(2024, 1, 31, 9, 15, 42), file.taken)
        assertEquals(3100, file.sizeKilobytes)
        assertEquals(3100L * 1024, file.approximateBytes)
    }

    @Test
    fun `a size past two thousand million kilobytes is read as the unsigned number it is`() {
        val file = CameraFile.parsePage(page(entry('A', 1, 25, 1, 1, 0, 0, 0, 0xFFFF_FFFFL)))[0]

        assertEquals(4_294_967_295L, file.sizeKilobytes)
    }

    @Test
    fun `an impossible date is unknown rather than invented`() {
        // The camera's clock is only as right as somebody last set it, and a month of 13 is not a
        // date anyone should see.
        val file = CameraFile.parsePage(page(entry('J', 1, 24, 13, 1, 0, 0, 0, 1)))[0]

        assertNull(file.taken)
    }

    @Test
    fun `several entries on a page are read in order`() {
        val files = CameraFile.parsePage(
            page(
                entry('J', 1, 25, 6, 1, 10, 0, 0, 10),
                entry('A', 2, 25, 6, 1, 10, 5, 0, 90_000),
                entry('L', 3, 25, 6, 1, 10, 9, 0, 45_000),
            ),
        )

        assertEquals(listOf(1, 2, 3), files.map { it.index })
        assertEquals(
            listOf(CameraFileKind.Photo, CameraFileKind.Video, CameraFileKind.ProtectedVideo),
            files.map { it.kind },
        )
    }

    @Test
    fun `entries longer than the documented thirteen bytes are still read`() {
        // The entry size is the page length divided by the count, so a firmware that appends a
        // field of its own does not break the fields before it.
        val longer = entry('J', 4, 25, 2, 3, 4, 5, 6, 77) + ByteArray(3)
        val second = entry('A', 5, 25, 2, 3, 4, 5, 7, 88) + ByteArray(3)

        val files = CameraFile.parsePage(page(longer, second))

        assertEquals(2, files.size)
        assertEquals(5, files[1].index)
        assertEquals(88, files[1].sizeKilobytes)
    }

    @Test
    fun `an empty page has no files`() {
        assertTrue(CameraFile.parsePage(ByteArray(0)).isEmpty())
        assertTrue(CameraFile.parsePage(ByteArray(1)).isEmpty())
    }

    @Test
    fun `a page that does not divide into whole entries is reported`() {
        val ragged = ByteArray(1 + 25)
        ragged[0] = 2

        val refused = assertFailsWith<GpSockProtocolException> { CameraFile.parsePage(ragged) }

        assertEquals("A file-list page of 2 entries cannot be 25 bytes long.", refused.message)
    }

    @Test
    fun `a page whose entries are too short to hold the fields is reported`() {
        val shortEntries = ByteArray(1 + 12)
        shortEntries[0] = 1

        assertFailsWith<GpSockProtocolException> { CameraFile.parsePage(shortEntries) }
    }

    /** What one type letter should read as. */
    private class Kind(
        val code: Char,
        val kind: CameraFileKind,
        val name: String,
        val photo: Boolean,
        val video: Boolean,
    )

    private companion object {
        /** A page as the firmware writes it: the count, then the entries end to end. */
        fun page(vararg entries: ByteArray): ByteArray =
            entries.fold(byteArrayOf(entries.size.toByte())) { page, entry -> page + entry }

        /** One thirteen-byte entry, every field under the test's control. */
        fun entry(
            code: Char,
            index: Int,
            year: Int,
            month: Int,
            day: Int,
            hour: Int,
            minute: Int,
            second: Int,
            kilobytes: Long,
        ): ByteArray {
            val entry = ByteArray(CameraFile.MINIMUM_ENTRY_LENGTH)
            entry[0] = code.code.toByte()
            entry[1] = index.toByte()
            entry[2] = (index shr 8).toByte()
            entry[3] = year.toByte()
            entry[4] = month.toByte()
            entry[5] = day.toByte()
            entry[6] = hour.toByte()
            entry[7] = minute.toByte()
            entry[8] = second.toByte()
            for (i in 0 until 4) {
                entry[9 + i] = (kilobytes shr (8 * i)).toByte()
            }
            return entry
        }
    }
}
