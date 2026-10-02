package com.rextechnologies.sightline.protocol.rtp

import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.bytes
import kotlinx.coroutines.runBlocking
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * The RTSP client, against replies shaped like the reference camera's.
 *
 * The SDP here is the one the camera really sent on 2026-10-02, including the detail that makes
 * the naive reading wrong: `track0` is the video and `track1` is the audio, so taking the last
 * `a=control` line selects sound and no picture ever arrives.
 */
class RtspClientTest {
    @Test
    fun `the video track is the stream url with track0 after the query string`() {
        val client = RtspClient(ScriptedTransport(emptyList()), "192.168.100.1")

        assertEquals("rtsp://192.168.100.1:8080/?action=stream/track0", client.videoTrackUrl)
    }

    @Test
    fun `a describe reply body is read rather than left in the socket`(): Unit = runBlocking {
        // Leaving the SDP unread is what desynchronises every later reply; the next request then
        // appears to be answered with "v=0".
        val transport = ScriptedTransport(
            listOf(
                reply(200, REFERENCE_SDP),
                reply(
                    200,
                    "",
                    "Session" to "DEDEDEDEDEDEDEDEDEDEDEDEDEDEDE",
                    "Transport" to "RTP/AVP/TCP;unicast;interleaved=0-1",
                ),
            ),
        )
        val client = RtspClient(transport, "192.168.100.1")
        client.connect()

        val describe = client.describe()
        assertContains(describe.body, "m=video 0 RTP/AVP 26")

        val setup = client.setupVideo()
        assertTrue(setup.isSuccess)
        assertEquals("DEDEDEDEDEDEDEDEDEDEDEDEDEDEDE", client.session)
    }

    @Test
    fun `the session is taken from setup and sent on later requests`(): Unit = runBlocking {
        val transport = ScriptedTransport(
            listOf(
                reply(200, "", "Session" to "222222222222222222222222222222"),
                reply(200, ""),
            ),
        )
        val client = RtspClient(transport, "192.168.100.1")
        client.connect()

        client.setupVideo()
        client.play()

        assertTrue(transport.sent.any { it.contains("Session: 222222222222222222222222222222") })
    }

    @Test
    fun `the setup asks for the stream over the same connection`(): Unit = runBlocking {
        // Inbound UDP is what a firewall drops, so the TCP transport is requested first.
        val transport = ScriptedTransport(listOf(reply(200, "")))
        val client = RtspClient(transport, "192.168.100.1")
        client.connect()

        client.setupVideo()

        assertContains(transport.sent[0], "Transport: RTP/AVP/TCP")
        assertContains(transport.sent[0], "/?action=stream/track0")
    }

    @Test
    fun `a refusal is reported with its status rather than thrown away`(): Unit = runBlocking {
        val transport = ScriptedTransport(listOf(reply(404, "")))
        val client = RtspClient(transport, "192.168.100.1")
        client.connect()

        val reply = client.setupVideo()

        assertFalse(reply.isSuccess)
        assertEquals(404, reply.statusCode)
        assertNull(client.session)
    }

    @Test
    fun `stream bytes that arrived with the play reply are not lost`(): Unit = runBlocking {
        // The camera often packs the first RTP bytes into the same read as the PLAY reply. Dropping
        // them loses the start of the first picture.
        val withStream = reply(200, "") + bytes(0x80, 0x1A, 0x00, 0x01)
        val transport = ScriptedTransport(listOf(withStream))
        val client = RtspClient(transport, "192.168.100.1")
        client.connect()

        client.play()
        val stream = client.readStream()

        assertContentEquals(bytes(0x80, 0x1A, 0x00, 0x01), stream)
    }

    @Test
    fun `a reply split across reads is assembled`(): Unit = runBlocking {
        val transport = ScriptedTransport(listOf(reply(200, REFERENCE_SDP)), dribbleBytes = 5)
        val client = RtspClient(transport, "192.168.100.1")
        client.connect()

        val reply = client.describe()

        assertTrue(reply.isSuccess)
        assertContains(reply.body, "a=control:track0")
    }

    @Test
    fun `a camera that hangs up mid reply is reported`(): Unit = runBlocking {
        val transport = ScriptedTransport(listOf("RTSP/1.0 200 OK\r\n".toByteArray(Charsets.US_ASCII)))
        val client = RtspClient(transport, "192.168.100.1")
        client.connect()

        assertFailsWith<RtspException> { client.describe() }
    }

    @Test
    fun `a reply that is not rtsp is reported rather than guessed at`(): Unit = runBlocking {
        val transport = ScriptedTransport(listOf("hello there\r\n\r\n".toByteArray(Charsets.US_ASCII)))
        val client = RtspClient(transport, "192.168.100.1")
        client.connect()

        assertFailsWith<RtspException> { client.options() }
    }

    @Test
    fun `a status line with no status in it is reported`(): Unit = runBlocking {
        val transport = ScriptedTransport(listOf("RTSP/1.0\r\n\r\n".toByteArray(Charsets.US_ASCII)))
        val client = RtspClient(transport, "192.168.100.1")

        val refused = assertFailsWith<RtspException> { client.options() }

        assertEquals("The camera's reply did not start with a status: 'RTSP/1.0'.", refused.message)
    }

    @Test
    fun `an empty reply is reported rather than read as nothing`(): Unit = runBlocking {
        val transport = ScriptedTransport(listOf("\r\n\r\n".toByteArray(Charsets.US_ASCII)))
        val client = RtspClient(transport, "192.168.100.1")

        val refused = assertFailsWith<RtspException> { client.options() }

        assertEquals("The camera sent an empty reply.", refused.message)
    }

    @Test
    fun `each request names its method, the next sequence number and the client`(): Unit = runBlocking {
        val transport = ScriptedTransport(listOf(reply(200, ""), reply(200, ""), reply(200, "")))
        val client = RtspClient(transport, "192.168.100.1")

        client.options()
        client.describe()
        client.teardown()

        assertEquals(
            "OPTIONS rtsp://192.168.100.1:8080/?action=stream RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: Sightline\r\n\r\n",
            transport.sent[0],
        )
        assertEquals(
            "DESCRIBE rtsp://192.168.100.1:8080/?action=stream RTSP/1.0\r\nCSeq: 2\r\nUser-Agent: Sightline\r\n" +
                "Accept: application/sdp\r\n\r\n",
            transport.sent[1],
        )
        assertEquals(
            "TEARDOWN rtsp://192.168.100.1:8080/?action=stream RTSP/1.0\r\nCSeq: 3\r\nUser-Agent: Sightline\r\n\r\n",
            transport.sent[2],
        )
    }

    @Test
    fun `the session the camera gives is kept without its timeout`(): Unit = runBlocking {
        val transport = ScriptedTransport(listOf(reply(200, "", "Session" to "ABCDEF;timeout=60"), reply(200, "")))
        val client = RtspClient(transport, "192.168.100.1")

        client.setupVideo()
        client.teardown()

        assertEquals("ABCDEF", client.session)
        assertContains(transport.sent[1], "Session: ABCDEF\r\n")
    }

    @Test
    fun `a setup that succeeds without a session leaves none`(): Unit = runBlocking {
        val client = RtspClient(ScriptedTransport(listOf(reply(200, ""))), "192.168.100.1")

        val reply = client.setupVideo()

        assertTrue(reply.isSuccess)
        assertNull(client.session)
    }

    @Test
    fun `headers are found whatever their case, and a line with no name is passed over`(): Unit = runBlocking {
        val text = "RTSP/1.0 200 OK\r\nCSeq: 1\r\n: orphan\r\nno colon here\r\ncontent-type: text/plain\r\n\r\n"
        val client = RtspClient(ScriptedTransport(listOf(text.toByteArray(Charsets.US_ASCII))), "192.168.100.1")

        val reply = client.options()

        assertEquals("text/plain", reply.header("Content-Type"))
        assertEquals(setOf("CSeq", "content-type"), reply.headers.keys)
        assertNull(reply.header("Session"))
    }

    @Test
    fun `stray carriage returns do not end the headers early`(): Unit = runBlocking {
        val text = "RTSP/1.0 200 OK\r\nCSeq: 1\r\nX-Odd: a\rb\r\n\rX-Later: 2\r\n\r\n"
        val client = RtspClient(ScriptedTransport(listOf(text.toByteArray(Charsets.US_ASCII))), "192.168.100.1")

        val reply = client.options()

        assertEquals("a\rb", reply.header("X-Odd"))
        assertEquals("2", reply.header("X-Later"))
    }

    @Test
    fun `a camera that hangs up part-way through a body gives what arrived`(): Unit = runBlocking {
        val text = "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 50\r\n\r\nv=0\r\n"
        val client = RtspClient(ScriptedTransport(listOf(text.toByteArray(Charsets.US_ASCII))), "192.168.100.1")

        val reply = client.describe()

        assertEquals("v=0\r\n", reply.body)
    }

    @Test
    fun `a body length that is not a number reads as no body`(): Unit = runBlocking {
        val text = "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: lots\r\n\r\n"
        val client = RtspClient(ScriptedTransport(listOf(text.toByteArray(Charsets.US_ASCII) + bytes(1, 2))), "h")

        val reply = client.describe()

        assertEquals("", reply.body)
        assertContentEquals(bytes(1, 2), client.readStream())
    }

    @Test
    fun `a negative body length reads as no body rather than failing`(): Unit = runBlocking {
        val text = "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: -5\r\n\r\n"
        val client = RtspClient(ScriptedTransport(listOf(text.toByteArray(Charsets.US_ASCII))), "h")

        assertEquals("", client.describe().body)
    }

    @Test
    fun `once carried bytes are used up the stream is read from the connection`(): Unit = runBlocking {
        val transport = ScriptedTransport(listOf(reply(200, "") + bytes(1)))
        val client = RtspClient(transport, "192.168.100.1")
        client.play()
        transport.arrive(bytes(2, 3))

        assertContentEquals(bytes(1), client.readStream())
        assertContentEquals(bytes(2, 3), client.readStream())
        assertContentEquals(ByteArray(0), client.readStream())
    }

    @Test
    fun `a blank host is refused`() {
        assertFailsWith<IllegalArgumentException> { RtspClient(ScriptedTransport(emptyList()), " ") }
    }

    @Test
    fun `connecting and closing the client connect and close its transport`(): Unit = runBlocking {
        val transport = ScriptedTransport(emptyList())
        val client = RtspClient(transport, "192.168.100.1")

        client.connect()
        assertTrue(transport.isConnected)

        client.close()
        assertFalse(transport.isConnected)
    }

    @Test
    fun `the camera serves its stream where the family does`() {
        assertEquals(8080, RtspClient.PORT)
        assertEquals("/?action=stream", RtspClient.STREAM_PATH)
    }

    private fun reply(status: Int, body: String, vararg headers: Pair<String, String>): ByteArray {
        val text = StringBuilder("RTSP/1.0 $status OK\r\nCSeq: 1\r\n")
        for ((name, value) in headers) {
            text.append(name).append(": ").append(value).append("\r\n")
        }

        if (body.isNotEmpty()) {
            text.append("Content-Type: application/sdp\r\n")
                .append("Content-Length: ").append(body.toByteArray(Charsets.UTF_8).size).append("\r\n")
        }

        text.append("\r\n").append(body)
        return text.toString().toByteArray(Charsets.UTF_8)
    }

    /** A transport that hands back prepared replies, one per request. */
    private class ScriptedTransport(
        private val replies: List<ByteArray>,
        private val dribbleBytes: Int = 0,
    ) : CameraTransport {
        private val queued = ArrayDeque<ByteArray>()
        private var next = 0

        val sent = mutableListOf<String>()

        override var isConnected = false
            private set

        /** Bytes the camera sends without being asked, as a stream does. */
        fun arrive(bytes: ByteArray) {
            queued.addLast(bytes)
        }

        override suspend fun connect() {
            isConnected = true
        }

        override suspend fun send(bytes: ByteArray) {
            sent += bytes.toString(Charsets.US_ASCII)
            if (next < replies.size) {
                queued.addLast(replies[next++])
            }
        }

        override suspend fun receive(into: ByteArray): Int {
            val reply = queued.removeFirstOrNull() ?: return 0
            var take = if (dribbleBytes > 0) minOf(dribbleBytes, reply.size) else reply.size
            take = minOf(take, into.size)
            reply.copyInto(into, 0, 0, take)
            if (take < reply.size) {
                queued.addFirst(reply.copyOfRange(take, reply.size))
            }

            return take
        }

        override fun close() {
            isConnected = false
        }
    }

    private companion object {
        const val REFERENCE_SDP =
            "v=0\r\n" +
                "o=- 1 1 IN IP4 127.0.0.1\r\n" +
                "s=Test\r\n" +
                "a=type:broadcast\r\n" +
                "t=0 0\r\n" +
                "c=IN IP4 0.0.0.0\r\n" +
                "m=video 0 RTP/AVP 26\r\n" +
                "a=control:track0\r\n" +
                "m=audio 0 RTP/AVP 97\r\n" +
                "a=rtpmap:97 L16/16000/1\r\n" +
                "a=control:track1\r\n"
    }
}
