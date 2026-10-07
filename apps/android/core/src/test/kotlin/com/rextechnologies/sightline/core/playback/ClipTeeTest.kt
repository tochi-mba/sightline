package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.core.library.SaveFailure
import com.rextechnologies.sightline.protocol.FakeClip
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.OutputStream
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/** A clip's download, written to the phone's storage and then read, in that order. */
class ClipTeeTest {
    private val clip = FakeClip.reference(listOf(FakeRtspCamera.jpeg(500)), emptyList()).build()

    @Test
    fun `each piece is in storage before the reader hears of it`() {
        val reader = ClipReader()
        val file = Watching(reader)
        val tee = ClipTee(file, reader)

        tee.write(clip[0].toInt())
        tee.write(clip, 1, 99)
        tee.write(clip, 100, clip.size - 100)
        tee.flush()

        assertEquals(listOf(0L, 1L, 100L), file.readerHadHeard)
        assertContentEquals(clip, file.toByteArray())
        assertEquals(clip.size.toLong(), reader.bytesRead)
        assertNotNull(reader.clip)
        assertTrue(file.flushed)
    }

    @Test
    fun `storage failing is a save failure and the reader never hears of the piece`() {
        val reader = ClipReader()
        val full = ClipTee(Failing(), reader)

        val failure = assertFailsWith<SaveFailure> { full.write(clip, 0, clip.size) }

        assertEquals("No space left on device", failure.message)
        assertEquals(0L, reader.bytesRead)
    }

    /** Storage that notes how much the reader had heard of each time it is written to. */
    private class Watching(private val reader: ClipReader) : ByteArrayOutputStream() {
        val readerHadHeard = mutableListOf<Long>()
        var flushed = false

        override fun write(b: ByteArray, off: Int, len: Int) {
            readerHadHeard += reader.bytesRead
            super.write(b, off, len)
        }

        override fun flush() {
            flushed = true
        }
    }

    private class Failing : OutputStream() {
        override fun write(b: Int) = throw IOException("No space left on device")

        override fun write(b: ByteArray, off: Int, len: Int) = throw IOException("No space left on device")
    }
}
