package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.protocol.FakeClip
import com.rextechnologies.sightline.protocol.media.AviFormatException
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration
import kotlin.time.Duration.Companion.microseconds
import kotlin.time.Duration.Companion.seconds

/** A clip read and timed as it arrives, which a player asks about from another thread. */
class ClipReaderTest {
    private val picture = jpeg(500)

    // Half a second of 16 kHz mono 16-bit sound.
    private val halfSecond = ByteArray(16_000)

    @Test
    fun `before its headers arrive a clip has nothing to play`() {
        val reader = ClipReader()

        assertNull(reader.clip)
        assertEquals(Duration.ZERO, reader.ready)
        assertFalse(reader.isComplete)
        assertNull(reader.pictureAt(Duration.ZERO))
        assertTrue(reader.soundFrom(Duration.ZERO).slices.isEmpty())
        assertEquals(0, reader.soundAfter(0).runs)
    }

    @Test
    fun `a clip in pieces is timed as it arrives and complete once it has`() {
        val file = FakeClip.reference(List(4) { picture }, List(2) { halfSecond }).copy(picturesPerSound = 2).build()
        val reader = ClipReader()

        file.toList().chunked(700).forEach { piece -> reader.push(piece.toByteArray(), 0, piece.size) }

        assertEquals(file.size.toLong(), reader.bytesRead)
        assertEquals(1920, reader.clip!!.width)
        assertEquals(1.seconds, reader.ready)
        assertFalse(reader.isComplete)
        assertEquals(2, reader.pictureAt(0.6.seconds)!!.number)
        val first = reader.pictureAt(Duration.ZERO)!!.picture
        assertContentEquals(picture, file.copyOfRange(first.offset.toInt(), first.offset.toInt() + picture.size))

        reader.finish()

        assertTrue(reader.isComplete)
    }

    @Test
    fun `a file that ends before its headers is not a clip`() {
        val reader = ClipReader()
        reader.push(FakeClip.reference(listOf(picture), emptyList()).build(), 0, 40)

        assertFailsWith<AviFormatException> { reader.finish() }
    }

    @Test
    fun `before its first picture is due there is none to show`() {
        // A clip with sound times its pictures by it, or at its end when no sound came.
        val file = FakeClip.reference(listOf(picture), emptyList()).build()
        val reader = ClipReader()
        reader.push(file, 0, file.size)
        assertNull(reader.pictureAt(Duration.ZERO))
        reader.finish()

        assertEquals(0, reader.pictureAt(Duration.ZERO)!!.number)
        assertNull(reader.pictureAt((-1).seconds))
    }

    @Test
    fun `sound from a moment part way through a run starts at a whole sample`() {
        val file = FakeClip.reference(List(2) { picture }, List(2) { halfSecond }).build()
        val reader = ClipReader()
        reader.push(file, 0, file.size)

        // A quarter of a second and a tenth of a millisecond in: 8,003.2 bytes, which is half-way through a sample.
        val (slices, runs) = reader.soundFrom(0.25.seconds + 100.microseconds)

        assertEquals(2, runs)
        assertEquals(2, slices.size)
        assertEquals(16_000 - 8_002, slices[0].length)
        assertEquals((8_002 / 32_000.0).seconds, slices[0].at)
        assertEquals(0.5.seconds, slices[1].at)
        assertEquals(16_000, slices[1].length)
    }

    @Test
    fun `sound already played is not given again`() {
        val file = FakeClip.reference(List(2) { picture }, List(2) { halfSecond }).build()
        val reader = ClipReader()
        reader.push(file, 0, file.size)

        assertEquals(0.5.seconds, reader.soundFrom(0.5.seconds).slices.single().at)
        assertTrue(reader.soundFrom(1.seconds).slices.isEmpty())
        assertEquals(0.5.seconds, reader.soundAfter(1).slices.single().at)
        assertTrue(reader.soundAfter(2).slices.isEmpty())
    }

    @Test
    fun `a clip with no sound offers none`() {
        val file = FakeClip.reference(listOf(picture), listOf(halfSecond)).copy(soundFormat = 0x55).build()
        val reader = ClipReader()
        reader.push(file, 0, file.size)

        assertTrue(reader.soundFrom(Duration.ZERO).slices.isEmpty())
        assertEquals(0, reader.soundFrom(Duration.ZERO).runs)
    }

    private fun jpeg(length: Int): ByteArray {
        val bytes = ByteArray(length) { 0x5A }
        bytes[0] = 0xFF.toByte()
        bytes[1] = 0xD8.toByte()
        bytes[length - 2] = 0xFF.toByte()
        bytes[length - 1] = 0xD9.toByte()
        return bytes
    }
}
