package com.rextechnologies.sightline.core.playback

import com.rextechnologies.sightline.core.library.saving
import java.io.OutputStream

/**
 * Where a clip's download goes: into the file it is kept in, and then to the reader that times it, so every byte
 * the reader knows of is already in the file for a player to read. The storage's failures become save failures,
 * told apart from the camera's.
 */
internal class ClipTee(private val file: OutputStream, private val reader: ClipReader) : OutputStream() {
    override fun write(b: Int) {
        write(byteArrayOf(b.toByte()), 0, 1)
    }

    override fun write(b: ByteArray, off: Int, len: Int) {
        saving { file.write(b, off, len) }
        reader.push(b, off, len)
    }

    override fun flush() {
        file.flush()
    }
}
