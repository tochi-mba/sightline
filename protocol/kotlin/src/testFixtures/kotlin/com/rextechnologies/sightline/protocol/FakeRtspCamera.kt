package com.rextechnologies.sightline.protocol

import kotlinx.coroutines.CompletableDeferred
import java.nio.ByteBuffer
import kotlin.time.Duration

/**
 * The camera's RTSP server on port 8080, needing no hardware.
 *
 * It behaves as the reference camera was measured to: it answers DESCRIBE with the real SDP and a body
 * length, SETUP with a session made of one byte repeated and the port it streams from, and after PLAY it
 * sends its pictures as datagrams to the port SETUP named on its [sockets] — but only once the control
 * channel has started the stream, which is what [streamStarted] asks. Until then, and after the last
 * picture unless told otherwise, it simply goes quiet, as the real one does. The .NET tests stand the
 * same camera behind their session.
 *
 * The stream ends when either side closes this connection: the client by closing it, or the camera,
 * which [hangUp] does as browse mode does on the real one, and which [closesAfterFrames] does once its
 * last picture has been taken.
 */
class FakeRtspCamera : CameraTransport {
    private val outbox = ArrayDeque<ByteArray>()
    private val hungUp = CompletableDeferred<Unit>()
    private var streamingTo: FakeCameraDatagrams? = null
    private var clientPort: Int? = null
    private var sequence = 0

    /** Where the datagrams go: the sockets of the network this connection was opened on. */
    var sockets: FakeCameraSockets? = null

    /** Whether the control channel has started the media flow; no packets flow until it has. */
    var streamStarted: () -> Boolean = { true }

    /** The pictures to send after PLAY, in order. Each goes as one marked RTP packet. */
    val frames = mutableListOf<ByteArray>()

    /** Datagrams sent after PLAY before any picture: noise that is not RTP at all. */
    val noiseAfterPlay = mutableListOf<ByteArray>()

    /** When set, the camera closes this connection once the last picture has been taken. */
    var closesAfterFrames = false

    /** The status SETUP answers with. */
    var setupStatus = 200

    /** The Transport header SETUP answers with, or null for the reference camera's. */
    var setupTransport: String? = null

    /** The status PLAY answers with. */
    var playStatus = 200

    /** A verb the camera never answers, to stand for a wedged server. */
    var neverAnswers: String? = null

    /** When set, this connection fails with it once the last picture has been taken. */
    var breaksWith: Exception? = null

    /** How long each picture takes to arrive after the one before, as the real camera's dozen a second do. */
    var pace: Duration = Duration.ZERO

    /** The verbs received, in order. */
    val verbs = mutableListOf<String>()

    /** The requests received, whole, in order. */
    val requests = mutableListOf<String>()

    override var isConnected = false
        private set

    /** Whether the client has closed this connection, which every way out of a stream must do. */
    var wasClosed = false
        private set

    override suspend fun connect() {
        isConnected = true
    }

    override suspend fun send(bytes: ByteArray) {
        val request = bytes.toString(Charsets.US_ASCII)
        val verb = request.substringBefore(' ')
        requests += request
        verbs += verb
        if (verb == neverAnswers) {
            return
        }

        when (verb) {
            "DESCRIBE" -> reply(200, REFERENCE_SDP)
            "SETUP" -> {
                clientPort = CLIENT_PORT.find(request)?.groupValues?.get(1)?.toInt()
                val transport = setupTransport
                    ?: "RTP/AVP;unicast;client_port=$clientPort-${clientPort?.plus(1)};" +
                    "server_port=$REFERENCE_SERVER_PORT-${REFERENCE_SERVER_PORT + 1}"
                val headers = if (setupStatus == 200) {
                    listOf("Transport" to transport, "Session" to "636363636363636363636363636363")
                } else {
                    emptyList()
                }
                reply(setupStatus, "", headers)
            }

            "PLAY" -> {
                reply(playStatus, "")
                val socket = clientPort?.let { sockets?.at(it) }
                if (playStatus == 200 && streamStarted() && socket != null) {
                    streamingTo = socket
                    noiseAfterPlay.forEach { socket.deliver(it) }
                    frames.forEach { socket.deliver(packet(it, sequence++), pace) }
                }
            }

            else -> reply(200, "", listOf("Public" to "DESCRIBE, SETUP, TEARDOWN, PLAY, PAUSE"))
        }
    }

    override suspend fun receive(into: ByteArray): Int {
        val next = outbox.removeFirstOrNull()
        if (next == null) {
            if (hungUp.isCompleted) {
                return 0
            }

            if ((closesAfterFrames || breaksWith != null) && "PLAY" in verbs) {
                // The camera ends the stream once the last picture has gone, not before.
                streamingTo?.awaitIdle()
                breaksWith?.let { throw it }
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

    /** The camera ends the stream, as entering browse mode does: what is in flight is dropped. */
    fun hangUp() {
        outbox.clear()
        streamingTo?.drop()
        hungUp.complete(Unit)
    }

    override fun close() {
        // Closing the connection is what stops the real camera sending.
        isConnected = false
        wasClosed = true
        streamingTo?.drop()
    }

    private fun reply(status: Int, body: String, headers: List<Pair<String, String>> = emptyList()) {
        val reason = if (status == 200) "OK" else "Error"
        val text = StringBuilder("RTSP/1.0 $status $reason\r\nCSeq: 1\r\n")
        headers.forEach { (name, value) -> text.append(name).append(": ").append(value).append("\r\n") }
        if (body.isNotEmpty()) {
            text.append("Content-Length: ").append(body.toByteArray(Charsets.UTF_8).size).append("\r\n")
        }

        outbox.addLast(text.append("\r\n").append(body).toString().toByteArray(Charsets.UTF_8))
    }

    companion object {
        /** The SDP the reference camera sent on 2026-10-02. */
        const val REFERENCE_SDP =
            "v=0\r\no=- 1 1 IN IP4 127.0.0.1\r\ns=Test\r\na=type:broadcast\r\nt=0 0\r\nc=IN IP4 0.0.0.0\r\n" +
                "m=video 0 RTP/AVP 26\r\na=control:track0\r\n" +
                "m=audio 0 RTP/AVP 97\r\na=rtpmap:97 L16/16000/1\r\na=control:track1\r\n"

        /** The port the reference camera streamed from on 2026-10-06. */
        const val REFERENCE_SERVER_PORT = 59728

        private val CLIENT_PORT = Regex("""client_port=(\d+)""")

        /** One RTP/JPEG packet carrying a whole picture: 640 by 360, the marker set, numbered [sequence]. */
        fun packet(jpeg: ByteArray, sequence: Int): ByteArray {
            val packet = ByteArray(20 + jpeg.size)
            val header = ByteBuffer.wrap(packet)
            header.put(0x80.toByte())
            header.put((0x80 or 26).toByte())
            header.putShort(sequence.toShort())
            header.putInt((sequence + 1) * 7380)
            header.putInt(0x63636363)
            packet[16] = 1
            packet[17] = 1
            packet[18] = (640 / 8).toByte()
            packet[19] = (360 / 8).toByte()
            jpeg.copyInto(packet, 20)
            return packet
        }

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
