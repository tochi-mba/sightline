package com.rextechnologies.sightline.protocol

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.delay
import java.nio.ByteBuffer
import kotlin.time.Duration

/**
 * The camera's RTSP server on port 8080, needing no hardware.
 *
 * It behaves as the reference camera was measured to: it answers DESCRIBE with the real SDP and a
 * body length, SETUP with a session made of one byte repeated, and after PLAY it sends bare RTP with no
 * interleaved framing — but only once the control channel has started the stream, which is what
 * [streamStarted] asks. Until then, and after the last frame unless told to close, it simply goes
 * quiet, as the real one does. The .NET tests stand the same camera behind their session.
 *
 * Like the real one it cannot be stopped: TEARDOWN answers 501 and PAUSE answers 200, and the pictures
 * keep coming either way. Only the camera can end it, which [hangUp] does as browse mode does on the real
 * camera. That the camera answers one connection per power-on is the business of whoever hands these
 * out; [neverAnswers] is how a refused one looks.
 */
class FakeRtspCamera : CameraTransport {
    private val outbox = ArrayDeque<ByteArray>()
    private val hungUp = CompletableDeferred<Unit>()
    private var sequence = 0

    /** Whether the control channel has started the media flow; no packets flow until it has. */
    var streamStarted: () -> Boolean = { true }

    /** The pictures to send after PLAY, in order. Each goes as one marked RTP packet. */
    val frames = mutableListOf<ByteArray>()

    /** Bytes sent after PLAY before any packet: noise that is not RTP at all. */
    var noiseAfterPlay: ByteArray? = null

    /** When set, the connection closes after the last frame instead of going quiet. */
    var closesAfterFrames = false

    /** The status SETUP answers with. */
    var setupStatus = 200

    /** The status PLAY answers with. */
    var playStatus = 200

    /** A verb the camera never answers, to stand for a wedged server. */
    var neverAnswers: String? = null

    /** When set, reading after the last frame fails with this, as a connection that drops does. */
    var breaksWith: Exception? = null

    /** How long each read after PLAY waits before handing anything over, as a real frame rate spaces them. */
    var pace: Duration = Duration.ZERO

    /** The verbs received, in order. */
    val verbs = mutableListOf<String>()

    override var isConnected = false
        private set

    /** Whether the connection was closed, which every way out of a stream must do. */
    var wasClosed = false
        private set

    override suspend fun connect() {
        isConnected = true
    }

    override suspend fun send(bytes: ByteArray) {
        val verb = bytes.toString(Charsets.US_ASCII).substringBefore(' ')
        verbs += verb
        if (verb == neverAnswers) {
            return
        }

        when (verb) {
            "DESCRIBE" -> reply(200, REFERENCE_SDP)
            "SETUP" -> {
                val session = if (setupStatus == 200) "Session" to "636363636363636363636363636363" else null
                reply(setupStatus, "", session)
            }

            "PLAY" -> {
                reply(playStatus, "")
                if (playStatus == 200 && streamStarted()) {
                    noiseAfterPlay?.let(outbox::addLast)
                    frames.forEach { outbox.addLast(packet(it)) }
                    // A picture is only known to be over when the next packet starts, as on the wire.
                    outbox.addLast(packet(byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 0xFF.toByte(), 0xD9.toByte())))
                }
            }

            // Listed by OPTIONS, and not implemented: the stream carries on regardless.
            "TEARDOWN" -> reply(501, "")

            // Answered, and ignored.
            "PAUSE" -> reply(200, "")
            else -> reply(200, "", "Public" to "DESCRIBE, SETUP, TEARDOWN, PLAY, PAUSE")
        }
    }

    override suspend fun receive(into: ByteArray): Int {
        if (pace.isPositive() && "PLAY" in verbs) {
            delay(pace)
        }

        val next = outbox.removeFirstOrNull()
        if (next == null) {
            breaksWith?.let { if ("PLAY" in verbs) throw it }
            if ((closesAfterFrames && "PLAY" in verbs) || hungUp.isCompleted) {
                return 0
            }

            // Quiet: nothing comes until whoever is reading gives up, or the camera hangs up.
            hungUp.await()
            return 0
        }

        // At most the reader's buffer, like a real socket; the remainder waits its turn.
        val take = minOf(next.size, into.size)
        next.copyInto(into, 0, 0, take)
        if (take < next.size) {
            outbox.addFirst(next.copyOfRange(take, next.size))
        }

        return take
    }

    /** The camera ends the connection, as entering browse mode does: what is in flight is dropped. */
    fun hangUp() {
        outbox.clear()
        hungUp.complete(Unit)
    }

    override fun close() {
        isConnected = false
        wasClosed = true
    }

    private fun reply(status: Int, body: String, header: Pair<String, String>? = null) {
        val reason = when (status) {
            200 -> "OK"
            501 -> "Not Implemented"
            else -> "Error"
        }
        val text = StringBuilder("RTSP/1.0 $status $reason\r\nCSeq: 1\r\n")
        header?.let { (name, value) -> text.append(name).append(": ").append(value).append("\r\n") }
        if (body.isNotEmpty()) {
            text.append("Content-Length: ").append(body.toByteArray(Charsets.UTF_8).size).append("\r\n")
        }

        outbox.addLast(text.append("\r\n").append(body).toString().toByteArray(Charsets.UTF_8))
    }

    /** One RTP/JPEG packet carrying a whole picture: 640 by 360, the marker set. */
    private fun packet(jpeg: ByteArray): ByteArray {
        val packet = ByteArray(20 + jpeg.size)
        val header = ByteBuffer.wrap(packet)
        header.put(0x80.toByte())
        header.put((0x80 or 26).toByte())
        header.putShort(sequence.toShort())
        sequence++
        header.putInt(sequence * 7380)
        header.putInt(0x63636363)
        packet[16] = 1
        packet[17] = 1
        packet[18] = (640 / 8).toByte()
        packet[19] = (360 / 8).toByte()
        jpeg.copyInto(packet, 20)
        return packet
    }

    companion object {
        /** The SDP the reference camera sent on 2026-10-02. */
        const val REFERENCE_SDP =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=Test\r\na=type:broadcast\r\nt=0 0\r\nc=IN IP4 0.0.0.0\r\n" +
                "m=video 0 RTP/AVP 26\r\na=control:track0\r\n" +
                "m=audio 0 RTP/AVP 97\r\na=rtpmap:97 L16/16000/1\r\na=control:track1\r\n"

        /** A block shaped like a JPEG, which is all reassembly and a session need. */
        fun jpeg(length: Int, fill: Byte = 0x5A): ByteArray {
            val bytes = ByteArray(length) { fill }
            bytes[0] = 0xFF.toByte()
            bytes[1] = 0xD8.toByte()
            bytes[2] = 0xFF.toByte()
            bytes[3] = 0xE0.toByte()
            bytes[length - 2] = 0xFF.toByte()
            bytes[length - 1] = 0xD9.toByte()
            return bytes
        }
    }
}
