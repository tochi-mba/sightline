package com.rextechnologies.sightline.protocol.rtp

import com.rextechnologies.sightline.protocol.ByteQueue
import com.rextechnologies.sightline.protocol.CameraTransport
import java.io.Closeable
import java.util.TreeMap

/**
 * What the camera said in reply to one RTSP request.
 *
 * @property statusCode The numeric status, 200 when it worked.
 * @property headers The response headers, keyed case-insensitively.
 * @property body The body, which is the SDP for a DESCRIBE and empty otherwise.
 */
data class RtspReply(val statusCode: Int, val headers: Map<String, String>, val body: String) {
    /** Whether the camera accepted the request. */
    val isSuccess: Boolean
        get() = statusCode == 200

    /** A header, or null when the camera did not send it. */
    fun header(name: String): String? = headers[name]
}

/**
 * The camera's RTSP server, on port 8080.
 *
 * Three things about this server differ from what a standards-compliant client expects, and all
 * three cost a working picture if they are not handled.
 *
 * 1. The video track is addressed as the stream URL with `/track0` appended **after** the
 *    query string, giving `rtsp://host:8080/?action=stream/track0`. Reading the last
 *    `a=control` line of the SDP instead selects the audio track, and the picture never arrives.
 * 2. A DESCRIBE reply has a body. Reading only as far as the blank line leaves the SDP in the socket
 *    and every later read is misaligned, which looks like the camera talking nonsense.
 * 3. After PLAY the camera sends bare RTP with no interleaved framing, whatever transport it agreed
 *    to. [RtpJpegReassembler] is what makes sense of that.
 *
 * The stream also stays silent until the control channel has been told to start it — see
 * `GpSockConnection.startStreaming`. A session that negotiates perfectly and delivers nothing is
 * almost always that.
 *
 * @param transport A pipe to the camera's RTSP port.
 * @param host The camera's address, used to build request URLs.
 */
class RtspClient(private val transport: CameraTransport, host: String) : Closeable {
    private val baseUrl: String
    private val buffer = ByteArray(16 * 1024)
    private val pending = ByteQueue()
    private var sequence = 0

    init {
        require(host.isNotBlank()) { "The camera's host must not be blank." }
        baseUrl = "rtsp://$host:$PORT$STREAM_PATH"
    }

    /** The session the camera gave us, once SETUP has succeeded. */
    var session: String? = null
        private set

    /** The URL of the video track, which is the base URL plus the track name. */
    val videoTrackUrl: String
        get() = "$baseUrl/track0"

    /** Opens the connection. */
    suspend fun connect() {
        transport.connect()
    }

    /** Asks what the camera supports. */
    suspend fun options(): RtspReply = send("OPTIONS", baseUrl, emptyMap())

    /** Asks for the stream description, whose body is SDP. */
    suspend fun describe(): RtspReply = send("DESCRIBE", baseUrl, mapOf("Accept" to "application/sdp"))

    /** Sets up the video track, asking for the stream over this same connection. */
    suspend fun setupVideo(): RtspReply {
        val reply = send("SETUP", videoTrackUrl, mapOf("Transport" to "RTP/AVP/TCP;unicast;interleaved=0-1"))

        val granted = reply.header("Session")
        if (reply.isSuccess && granted != null) {
            session = granted.split(';')[0].trim()
        }

        return reply
    }

    /** Starts the stream. */
    suspend fun play(): RtspReply = send("PLAY", baseUrl, mapOf("Range" to "npt=0.000-"))

    /** Ends the session. */
    suspend fun teardown(): RtspReply = send("TEARDOWN", baseUrl, emptyMap())

    /**
     * Reads whatever stream bytes have arrived, for feeding to a reassembler.
     *
     * @return The bytes read, which is empty when the camera has stopped sending.
     */
    suspend fun readStream(): ByteArray {
        if (pending.size > 0) {
            // Bytes that arrived in the same read as the PLAY reply are stream data already.
            val carried = pending.toByteArray()
            pending.clear()
            return carried
        }

        val read = transport.receive(buffer)
        return buffer.copyOf(read)
    }

    private suspend fun send(verb: String, url: String, extra: Map<String, String>): RtspReply {
        sequence++
        val request = StringBuilder()
            .append(verb).append(' ').append(url).append(" RTSP/1.0\r\n")
            .append("CSeq: ").append(sequence).append("\r\n")
            .append("User-Agent: Sightline\r\n")
        val current = session
        if (current != null) {
            request.append("Session: ").append(current).append("\r\n")
        }

        for ((name, value) in extra) {
            request.append(name).append(": ").append(value).append("\r\n")
        }

        request.append("\r\n")
        transport.send(request.toString().toByteArray(Charsets.US_ASCII))
        return readReply()
    }

    private suspend fun readReply(): RtspReply {
        // Headers first.
        var headerEnd = pending.indexOf(BLANK_LINE)
        while (headerEnd < 0) {
            if (!fill()) {
                throw RtspException("The camera closed the connection part-way through a reply.")
            }
            headerEnd = pending.indexOf(BLANK_LINE)
        }

        val headerText = String(pending.array, 0, headerEnd, Charsets.US_ASCII)
        pending.removeFirst(headerEnd + BLANK_LINE.size)

        val lines = headerText.split("\r\n").filter { it.isNotEmpty() }
        if (lines.isEmpty()) {
            throw RtspException("The camera sent an empty reply.")
        }

        val status = lines[0].split(' ', limit = 3).getOrNull(1)?.toIntOrNull()
            ?: throw RtspException("The camera's reply did not start with a status: '${lines[0]}'.")

        val headers = TreeMap<String, String>(String.CASE_INSENSITIVE_ORDER)
        for (line in lines.drop(1)) {
            val colon = line.indexOf(':')
            if (colon > 0) {
                headers[line.substring(0, colon).trim()] = line.substring(colon + 1).trim()
            }
        }

        // Then exactly as much body as the camera said there would be. Skipping this is what
        // desynchronises every later read.
        val length = (headers["Content-Length"]?.toIntOrNull() ?: 0).coerceAtLeast(0)
        while (pending.size < length) {
            if (!fill()) {
                break
            }
        }

        val take = minOf(length, pending.size)
        val body = String(pending.array, 0, take, Charsets.UTF_8)
        pending.removeFirst(take)
        return RtspReply(status, headers, body)
    }

    private suspend fun fill(): Boolean {
        val read = transport.receive(buffer)
        if (read == 0) {
            return false
        }

        pending.append(buffer, 0, read)
        return true
    }

    /** Closes the connection. */
    override fun close() {
        transport.close()
    }

    companion object {
        /** The port the camera serves RTSP on. */
        const val PORT = 8080

        /** The path the camera publishes its stream at. */
        const val STREAM_PATH = "/?action=stream"

        private val BLANK_LINE = "\r\n\r\n".toByteArray(Charsets.US_ASCII)
    }
}

/** The camera's RTSP server did something this client cannot make sense of. */
class RtspException(message: String) : Exception(message)
