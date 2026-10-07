package com.rextechnologies.sightline.protocol

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * An AVI clip in the reference camera's shape, with every field the tests vary under their control: Motion JPEG
 * in `00dc` chunks and 16 kHz mono PCM in `01wb`, a stream header each, an index at the end. The same shape as
 * the .NET tests' fake clip, with the stream name the camera's own clips carry, and [headerExtra] for whatever
 * else a header list may hold, such as the `odml` list the camera writes.
 *
 * @property picturesPerSound How many pictures come before each run of sound, as the camera writes a run after the
 *   pictures taken during it.
 */
data class FakeClip(
    val pictures: List<ByteArray>,
    val sounds: List<ByteArray>,
    val microsPerFrame: Int = 33_333,
    val scale: Int = 1,
    val rate: Int = 30,
    val soundFormat: Int = 1,
    val grouped: Boolean = false,
    val truncated: Boolean = false,
    val overlong: Boolean = false,
    val extra: List<Pair<String, ByteArray>> = emptyList(),
    val picturesPerSound: Int = 1,
    val headerExtra: List<ByteArray> = emptyList(),
) {
    /** The clip's bytes. */
    fun build(): ByteArray {
        val avih = ByteArray(if (truncated) 20 else 56)
        if (!truncated) {
            avih.putInt32(0, microsPerFrame)
            avih.putInt32(16, pictures.size)
            avih.putInt32(32, 1920)
            avih.putInt32(36, 1080)
        }

        val videoHeader = streamHeader("vids", scale, rate)
        val soundHeader = streamHeader("auds", 1, 16_000)
        val format = ByteArray(if (truncated) 10 else 18)
        if (!truncated) {
            format.putInt16(0, soundFormat)
            format.putInt16(2, 1)
            format.putInt32(4, 16_000)
            format.putInt16(14, 16)
        }

        val hdrl = list(
            "hdrl",
            chunk("avih", avih),
            list(
                "strl",
                chunk("strh", if (truncated) videoHeader.copyOf(20) else videoHeader),
                chunk("strf", ByteArray(40)),
                chunk("strn", "video".toByteArray(Charsets.US_ASCII)),
            ),
            list("strl", chunk("strh", soundHeader), chunk("strf", format)),
            *headerExtra.toTypedArray(),
            if (overlong) "JUNK".toByteArray(Charsets.US_ASCII) + byteArrayOf(-1, -1, 0, 0) else ByteArray(0),
        )

        val movi = ArrayList<ByteArray>()
        var picture = 0
        for (sound in sounds) {
            repeat(picturesPerSound) {
                if (picture < pictures.size) {
                    movi += chunk("00dc", pictures[picture++])
                }
            }
            movi += chunk("01wb", sound)
        }
        while (picture < pictures.size) {
            movi += chunk("00dc", pictures[picture++])
        }
        extra.forEach { (code, body) -> movi += chunk(code, body) }

        val moviList = if (grouped) {
            list(
                "movi",
                list("rec ", *movi.toTypedArray()),
            )
        } else {
            list("movi", *movi.toTypedArray())
        }
        val body = ByteArrayOutputStream()
        body.write("AVI ".toByteArray(Charsets.US_ASCII))
        body.write(hdrl)
        body.write(list("INFO", chunk("ISFT", "camera\u0000".toByteArray(Charsets.US_ASCII))))
        body.write(chunk("JUNK", ByteArray(12)))
        body.write(moviList)
        body.write(chunk("idx1", ByteArray(16 * (pictures.size + sounds.size))))
        return "RIFF".toByteArray(Charsets.US_ASCII) + le(body.size()) + body.toByteArray()
    }

    companion object {
        /** A clip shaped like the reference camera's, holding these pictures and runs of sound. */
        fun reference(pictures: List<ByteArray>, sounds: List<ByteArray>): FakeClip = FakeClip(pictures, sounds)

        private fun streamHeader(type: String, scale: Int, rate: Int): ByteArray {
            val header = ByteArray(56)
            type.toByteArray(Charsets.US_ASCII).copyInto(header)
            header.putInt32(20, scale)
            header.putInt32(24, rate)
            return header
        }

        private fun chunk(code: String, body: ByteArray): ByteArray =
            code.toByteArray(Charsets.US_ASCII) + le(body.size) + body +
                (if (body.size % 2 == 1) ByteArray(1) else ByteArray(0))

        private fun list(type: String, vararg children: ByteArray): ByteArray {
            val body = ByteArrayOutputStream()
            body.write(type.toByteArray(Charsets.US_ASCII))
            children.forEach { body.write(it) }
            return "LIST".toByteArray(Charsets.US_ASCII) + le(body.size()) + body.toByteArray()
        }

        private fun le(value: Int): ByteArray = ByteArray(4).also { it.putInt32(0, value) }

        private fun ByteArray.putInt32(at: Int, value: Int) {
            ByteBuffer.wrap(this).order(ByteOrder.LITTLE_ENDIAN).putInt(at, value)
        }

        private fun ByteArray.putInt16(at: Int, value: Int) {
            ByteBuffer.wrap(this).order(ByteOrder.LITTLE_ENDIAN).putShort(at, value.toShort())
        }
    }
}
