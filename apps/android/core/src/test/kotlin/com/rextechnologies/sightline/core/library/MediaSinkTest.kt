package com.rextechnologies.sightline.core.library

import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.OutputStream
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertSame

class MediaSinkTest {
    private fun bytes(vararg values: Int) = ByteArray(values.size) { values[it].toByte() }

    private fun ascii(text: String) = text.toByteArray(Charsets.US_ASCII)

    @Test
    fun `a jpeg, an avi and an mp4 are each recognised from their first bytes`() {
        assertEquals(MediaKind.Jpeg, MediaKind.sniff(bytes(0xFF, 0xD8, 0xFF, 0xE0)))
        assertEquals(MediaKind.Avi, MediaKind.sniff(ascii("RIFF\u0000\u0000\u0000\u0000AVI ")))
        assertEquals(MediaKind.Mp4, MediaKind.sniff(ascii("\u0000\u0000\u0000\u0018ftypmp42")))
    }

    @Test
    fun `anything else, or too little to tell, is unknown rather than guessed`() {
        assertEquals(MediaKind.Unknown, MediaKind.sniff(ByteArray(0)))
        assertEquals(MediaKind.Unknown, MediaKind.sniff(bytes(0xFF, 0xD8)))
        assertEquals(MediaKind.Unknown, MediaKind.sniff(ascii("RIFF\u0000\u0000\u0000\u0000WAVE")))
        assertEquals(MediaKind.Unknown, MediaKind.sniff(ascii("RIFF")))
        assertEquals(MediaKind.Unknown, MediaKind.sniff(ascii("\u0000\u0000\u0000\u0018fty")))
        assertEquals(".bin", MediaKind.Unknown.extension)
        assertEquals("video/x-msvideo", MediaKind.Avi.mimeType)
    }

    @Test
    fun `nothing is opened until the bytes say what they are, then everything passes through`() {
        val opened = mutableListOf<MediaKind>()
        val target = ByteArrayOutputStream()
        val output = SniffingOutput { kind -> target.also { opened += kind } }
        val file = bytes(0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 0xFF, 0xD9)

        output.write(file, 0, 5)
        assertNull(output.kind)
        output.write(file[5].toInt())
        output.write(file, 6, file.size - 6)
        output.finish()

        assertEquals(listOf(MediaKind.Jpeg), opened)
        assertEquals(MediaKind.Jpeg, output.kind)
        assertContentEquals(file, target.toByteArray())
    }

    @Test
    fun `a file shorter than a header is still opened and written when finished`() {
        val target = ByteArrayOutputStream()
        val output = SniffingOutput { target }

        output.write(bytes(0xFF, 0xD8, 0xFF), 0, 3)
        output.finish()
        output.finish()

        assertEquals(MediaKind.Jpeg, output.kind)
        assertContentEquals(bytes(0xFF, 0xD8, 0xFF), target.toByteArray())
    }

    @Test
    fun `finishing before any bytes opens nothing`() {
        val output = SniffingOutput { error("Nothing should be opened.") }

        output.finish()

        assertNull(output.kind)
    }

    @Test
    fun `the phone failing to open or write is a save failure, not an io error`() {
        val opening = SniffingOutput { throw IOException("No space left on the device.") }
        val failedOpen = assertFailsWith<SaveFailure> { opening.write(ByteArray(12), 0, 12) }
        assertEquals("No space left on the device.", failedOpen.message)

        val writing = SniffingOutput { FailsAfterFirstWrite() }
        writing.write(ByteArray(12), 0, 12)
        val failedWrite = assertFailsWith<SaveFailure> { writing.write(ByteArray(4), 0, 4) }
        assertEquals("The disk went away.", failedWrite.message)
        assertEquals("its storage refused the file.", SaveFailure(IOException()).message)
    }

    @Test
    fun `the rest of a first write longer than a header failing is a save failure too`() {
        // Found by this test: the bytes after the header went to the gallery outside the guard, so a
        // full phone at that moment looked like a lost camera.
        val writing = SniffingOutput { FailsAfterFirstWrite() }

        assertFailsWith<SaveFailure> { writing.write(ByteArray(16), 0, 16) }
    }

    /** A destination that takes the header and then fails, as a phone that just filled up does. */
    private class FailsAfterFirstWrite : OutputStream() {
        private var calls = 0

        override fun write(b: Int) = throw IOException("The disk went away.")

        override fun write(b: ByteArray, off: Int, len: Int) {
            if (calls++ > 0) throw IOException("The disk went away.")
        }
    }

    @Test
    fun `saving passes a value through and leaves other failures alone`() {
        val failure = IllegalStateException("not storage")

        assertEquals(4, saving { 4 })
        assertSame(failure, assertFailsWith<IllegalStateException> { saving { throw failure } })
    }
}
