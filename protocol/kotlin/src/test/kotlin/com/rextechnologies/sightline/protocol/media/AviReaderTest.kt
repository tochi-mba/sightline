package com.rextechnologies.sightline.protocol.media

import com.rextechnologies.sightline.protocol.FakeClip
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.time.Duration
import kotlin.time.Duration.Companion.seconds

/**
 * Reading a clip as it comes off the card, against AVIs built here in the reference camera's shape: Motion JPEG
 * in `00dc` chunks and 16 kHz mono PCM in `01wb`, a stream header each, an index at the end.
 *
 * Synthetic, like every fixture here: a real clip shows somebody's room. The same cases as the .NET tests.
 */
class AviReaderTest {
    private val first = jpeg(301, 0x11)
    private val second = jpeg(240, 0x22)
    private val noise = byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)

    private fun AviReader.pushAll(bytes: ByteArray): List<AviChunk> = push(bytes, 0, bytes.size)

    @Test
    fun `a clip read whole gives its headers then its pictures and sound in order`() {
        val reader = AviReader()

        val chunks = reader.pushAll(FakeClip.reference(listOf(first, second), listOf(noise)).build())

        assertEquals(AviClip(1920, 1080, 30.0, 2, AviSound(16_000, 1, 16)), reader.clip)
        val sound = reader.clip!!.sound!!
        assertEquals(listOf(16_000, 1, 16), listOf(sound.sampleRate, sound.channels, sound.bitsPerSample))
        assertEquals(3, chunks.size)
        assertContentEquals(first, assertIs<AviChunk.Picture>(chunks[0]).jpeg)
        assertContentEquals(noise, assertIs<AviChunk.Sound>(chunks[1]).pcm)
        val next = assertIs<AviChunk.Picture>(chunks[2])
        assertEquals(1, next.number)
        assertContentEquals(second, next.jpeg)
    }

    @Test
    fun `each piece says where in the file its bytes lie`() {
        val file = FakeClip.reference(listOf(first, second), listOf(noise)).build()

        val chunks = AviReader().pushAll(file)

        val picture = assertIs<AviChunk.Picture>(chunks[0])
        assertContentEquals(first, file.copyOfRange(picture.offset.toInt(), picture.offset.toInt() + picture.jpeg.size))
        val sound = assertIs<AviChunk.Sound>(chunks[1])
        assertContentEquals(noise, file.copyOfRange(sound.offset.toInt(), sound.offset.toInt() + sound.pcm.size))
        val next = assertIs<AviChunk.Picture>(chunks[2])
        assertContentEquals(second, file.copyOfRange(next.offset.toInt(), next.offset.toInt() + next.jpeg.size))
    }

    @Test
    fun `a clip arriving a byte at a time reads the same`() {
        val file = FakeClip.reference(listOf(first, second), listOf(noise)).build()
        val reader = AviReader()
        val chunks = ArrayList<AviChunk>()

        for (at in file.indices) {
            chunks += reader.push(file, at, 1)
        }

        assertEquals(1920, reader.clip!!.width)
        assertEquals(listOf(301, 240), chunks.filterIsInstance<AviChunk.Picture>().map { it.jpeg.size })
        assertContentEquals(noise, chunks.filterIsInstance<AviChunk.Sound>().single().pcm)
    }

    @Test
    fun `pieces of every size read the same as the whole`() {
        val file = FakeClip.reference(listOf(first, second, first), listOf(noise, noise)).build()
        for (size in listOf(2, 7, 13, 64, 500)) {
            val reader = AviReader()
            val chunks = ArrayList<AviChunk>()
            var at = 0
            while (at < file.size) {
                chunks += reader.push(file, at, minOf(size, file.size - at))
                at += size
            }

            assertEquals(5, chunks.size, "in pieces of $size")
        }
    }

    @Test
    fun `how long a clip plays and when each picture shows come from its rate`() {
        val clip = AviClip(1920, 1080, 30.0, 150, null)

        assertEquals(1080, clip.height)
        assertEquals(150, clip.totalFrames)
        assertEquals(5.seconds, clip.duration)
        assertEquals(1.5.seconds, clip.timeOf(45))
        assertEquals(Duration.ZERO, AviClip(640, 360, 0.0, 10, null).duration)
        assertEquals(Duration.ZERO, AviClip(640, 360, 0.0, 10, null).timeOf(3))
    }

    @Test
    fun `the video streams own rate wins over the main headers`() {
        // 29.97 frames a second is 30000/1001 in the stream header; the main header rounds it to 33367 us.
        val reader = AviReader()

        reader.pushAll(
            FakeClip.reference(
                listOf(first),
                emptyList(),
            ).copy(microsPerFrame = 33_367, scale = 1001, rate = 30_000).build(),
        )

        assertEquals(30_000.0 / 1001, reader.clip!!.framesPerSecond)
    }

    @Test
    fun `a stream header with no rate leaves the main headers and no rate at all reads as zero`() {
        val reader = AviReader()
        reader.pushAll(FakeClip.reference(listOf(first), emptyList()).copy(scale = 0).build())
        assertEquals(30.0, reader.clip!!.framesPerSecond, 0.001)

        val none = AviReader()
        none.pushAll(FakeClip.reference(listOf(first), emptyList()).copy(microsPerFrame = 0, rate = 0).build())
        assertEquals(0.0, none.clip!!.framesPerSecond)
    }

    @Test
    fun `an odd length picture is read without its pad byte`() {
        val odd = jpeg(301, 0x33)

        val chunks = AviReader().pushAll(FakeClip.reference(listOf(odd, second), emptyList()).build())

        assertContentEquals(odd, assertIs<AviChunk.Picture>(chunks[0]).jpeg)
        assertContentEquals(second, assertIs<AviChunk.Picture>(chunks[1]).jpeg)
    }

    @Test
    fun `a picture comes out without the zeros the camera pads it with`() {
        // As in its stream: up to seven zeros after FF D9, to a multiple of eight bytes.
        val chunks = AviReader().pushAll(FakeClip.reference(listOf(first + ByteArray(3)), emptyList()).build())

        assertContentEquals(first, assertIs<AviChunk.Picture>(chunks.single()).jpeg)
    }

    @Test
    fun `grouped chunks inside movi are read as if they were not grouped`() {
        val chunks = AviReader().pushAll(
            FakeClip.reference(listOf(first, second), listOf(noise)).copy(grouped = true).build(),
        )

        assertEquals(3, chunks.size)
    }

    @Test
    fun `chunks of streams it does not know and lists it does not read are passed over`() {
        val extra = listOf(
            "02dc" to jpeg(50, 0x44),
            "00wb" to noise,
            "01dc" to jpeg(60, 0x55),
            "JUNK" to ByteArray(100_000),
        )
        val file = FakeClip.reference(listOf(first), listOf(noise)).copy(extra = extra).build()
        val reader = AviReader()
        val chunks = ArrayList<AviChunk>()

        var at = 0
        while (at < file.size) {
            chunks += reader.push(file, at, minOf(4096, file.size - at))
            at += 4096
        }

        assertContentEquals(first, chunks.filterIsInstance<AviChunk.Picture>().single().jpeg)
        assertContentEquals(noise, chunks.filterIsInstance<AviChunk.Sound>().single().pcm)
    }

    @Test
    fun `a picture stored as a 00db chunk is a picture too`() {
        val chunks = AviReader().pushAll(
            FakeClip.reference(listOf(first), emptyList()).copy(extra = listOf("00db" to second)).build(),
        )

        assertEquals(listOf(301, 240), chunks.filterIsInstance<AviChunk.Picture>().map { it.jpeg.size })
    }

    @Test
    fun `lists in the header that are not a streams are passed over`() {
        // The camera writes an odml list there; a list too short to say what it is says nothing.
        val odml = list("odml", "dmlh".toByteArray(Charsets.US_ASCII) + le(4) + ByteArray(4))
        val tooShort = "LIST".toByteArray(Charsets.US_ASCII) + le(2) + "od".toByteArray(Charsets.US_ASCII)
        val reader = AviReader()

        reader.pushAll(
            FakeClip.reference(listOf(first), listOf(noise)).copy(headerExtra = listOf(odml, tooShort)).build(),
        )

        assertEquals(AviClip(1920, 1080, 30.0, 1, AviSound(16_000, 1, 16)), reader.clip)
    }

    @Test
    fun `sound that is not plain pcm is not offered and its chunks are passed over`() {
        val reader = AviReader()

        val chunks = reader.pushAll(FakeClip.reference(listOf(first), listOf(noise)).copy(soundFormat = 0x55).build())

        assertNull(reader.clip!!.sound)
        assertIs<AviChunk.Picture>(chunks.single())
    }

    @Test
    fun `headers too short to hold what they should are ignored rather than misread`() {
        val reader = AviReader()

        val chunks = reader.pushAll(FakeClip.reference(listOf(first), listOf(noise)).copy(truncated = true).build())

        assertEquals(AviClip(0, 0, 0.0, 0, null), reader.clip)
        assertTrue(chunks.isEmpty())
    }

    @Test
    fun `a header chunk claiming more than its list is cut off at the list`() {
        val reader = AviReader()

        reader.pushAll(FakeClip.reference(listOf(first), emptyList()).copy(overlong = true).build())

        assertEquals(1920, reader.clip!!.width)
    }

    @Test
    fun `a file that is not an avi is refused at its first twelve bytes`() {
        val reader = AviReader()
        assertTrue(reader.pushAll("RIFF".toByteArray(Charsets.US_ASCII)).isEmpty())

        assertFailsWith<AviFormatException> {
            reader.pushAll(byteArrayOf(0, 0, 0, 0) + "WAVE".toByteArray(Charsets.US_ASCII))
        }
        assertFailsWith<AviFormatException> { AviReader().pushAll(jpeg(40, 0)) }
    }

    @Test
    fun `a picture claiming more than a clip holds is refused`() {
        val file = FakeClip.reference(emptyList(), emptyList()).build() +
            "00dc".toByteArray(Charsets.US_ASCII) + byteArrayOf(0, 0, 0, 4)

        val refused = assertFailsWith<AviFormatException> { AviReader().pushAll(file) }

        assertTrue(refused.message!!.contains("more than a clip of this camera holds"))
    }

    private fun le(value: Int): ByteArray = byteArrayOf(
        value.toByte(),
        (value shr 8).toByte(),
        (value shr 16).toByte(),
        (
            value shr
                24
            ).toByte(),
    )

    private fun list(type: String, body: ByteArray): ByteArray =
        "LIST".toByteArray(Charsets.US_ASCII) + le(4 + body.size) + type.toByteArray(Charsets.US_ASCII) + body

    private fun jpeg(length: Int, fill: Int): ByteArray {
        val bytes = ByteArray(length) { fill.toByte() }
        bytes[0] = 0xFF.toByte()
        bytes[1] = 0xD8.toByte()
        bytes[length - 2] = 0xFF.toByte()
        bytes[length - 1] = 0xD9.toByte()
        return bytes
    }
}
