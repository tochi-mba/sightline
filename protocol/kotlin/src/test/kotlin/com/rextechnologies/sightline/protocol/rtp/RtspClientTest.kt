package com.rextechnologies.sightline.protocol.rtp

import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.bytes
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
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
                    "Transport" to REFERENCE_TRANSPORT,
                ),
            ),
        )
        val client = RtspClient(transport, "192.168.100.1")
        client.connect()

        val describe = client.describe()
        assertContains(describe.body, "m=video 0 RTP/AVP 26")

        val setup = client.setupVideo(63721)
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

        client.setupVideo(50100)
        client.play()

        assertTrue(transport.sent.any { it.contains("Session: 222222222222222222222222222222") })
    }

    @Test
    fun `the setup asks for the stream as datagrams to the port given`(): Unit = runBlocking {
        // Over this connection instead, the camera streams once per power-on and then leaves its buttons stuck.
        val transport = ScriptedTransport(listOf(reply(200, "")))
        val client = RtspClient(transport, "192.168.100.1")
        client.connect()

        client.setupVideo(63721)

        assertTrue(transport.sent[0].startsWith("SETUP rtsp://192.168.100.1:8080/?action=stream/track0 RTSP/1.0"))
        assertContains(transport.sent[0], "Transport: RTP/AVP;unicast;client_port=63721-63722\r\n")
    }

    @Test
    fun `the port the camera streams from is read from its setup reply`(): Unit = runBlocking {
        val transport = ScriptedTransport(
            listOf(reply(200, "", "Transport" to REFERENCE_TRANSPORT, "Session" to "F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0")),
        )
        val client = RtspClient(transport, "192.168.100.1")

        client.setupVideo(63721)

        assertEquals(59728, client.serverPort)
        assertEquals("F0F0F0F0F0F0F0F0F0F0F0F0F0F0F0", client.session)
    }

    @Test
    fun `a setup reply with no usable server port leaves it unknown`(): Unit = runBlocking {
        val unusable = listOf(
            null,
            "RTP/AVP;unicast;client_port=63721-63722",
            "RTP/AVP;unicast;server_port",
            "RTP/AVP;unicast;server_port=many",
            "RTP/AVP;unicast;server_port=0-1",
            "RTP/AVP;unicast;server_port=70000-70001",
            "RTP/AVP;unicast;server_port=-5",
        )
        for (header in unusable) {
            val headers = if (header == null) emptyArray<Pair<String, String>>() else arrayOf("Transport" to header)
            val client = RtspClient(ScriptedTransport(listOf(reply(200, "", *headers))), "192.168.100.1")

            assertTrue(client.setupVideo(50100).isSuccess)

            assertNull(client.serverPort, header)
            assertNull(client.session, header)
        }
    }

    @Test
    fun `the server port is found whatever its case and wherever it comes`(): Unit = runBlocking {
        val transport = ScriptedTransport(
            listOf(reply(200, "", "Transport" to "RTP/AVP;unicast; Server_Port=6970;client_port=50100-50101")),
        )
        val client = RtspClient(transport, "192.168.100.1")

        client.setupVideo(50100)

        assertEquals(6970, client.serverPort)
    }

    @Test
    fun `a refusal is reported with its status rather than thrown away`(): Unit = runBlocking {
        val transport = ScriptedTransport(listOf(reply(404, "")))
        val client = RtspClient(transport, "192.168.100.1")
        client.connect()

        val reply = client.setupVideo(50100)

        assertFalse(reply.isSuccess)
        assertEquals(404, reply.statusCode)
        assertNull(client.session)
        assertNull(client.serverPort)
    }

    @Test
    fun `waiting for the close ends when the camera closes the connection whatever it sent first`(): Unit =
        runBlocking {
            // Browsing the card is the camera closing the stream's connection. Anything it sends before that is
            // not the stream, which comes as datagrams.
            val transport = ScriptedTransport(listOf(reply(200, "") + bytes(1, 2, 3)))
            val client = RtspClient(transport, "192.168.100.1")
            client.play()
            transport.arrive(bytes(4, 5, 6))

            client.waitForClose()

            assertEquals(3, transport.reads)
        }

    @Test
    fun `waiting for the close can be given up`(): Unit = runBlocking {
        val transport = ScriptedTransport(emptyList(), blocksWhenEmpty = true)
        val client = RtspClient(transport, "192.168.100.1")

        assertFailsWith<kotlinx.coroutines.TimeoutCancellationException> {
            withTimeout(50) { client.waitForClose() }
        }
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
        client.play()

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
            "PLAY rtsp://192.168.100.1:8080/?action=stream RTSP/1.0\r\nCSeq: 3\r\nUser-Agent: Sightline\r\n" +
                "Range: npt=0.000-\r\n\r\n",
            transport.sent[2],
        )
    }

    @Test
    fun `the session the camera gives is kept without its timeout`(): Unit = runBlocking {
        val transport = ScriptedTransport(listOf(reply(200, "", "Session" to "ABCDEF;timeout=60"), reply(200, "")))
        val client = RtspClient(transport, "192.168.100.1")

        client.setupVideo(50100)
        client.play()

        assertEquals("ABCDEF", client.session)
        assertContains(transport.sent[1], "Session: ABCDEF\r\n")
    }

    @Test
    fun `a setup that succeeds without a session leaves none`(): Unit = runBlocking {
        val client = RtspClient(ScriptedTransport(listOf(reply(200, ""))), "192.168.100.1")

        val reply = client.setupVideo(50100)

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
    }

    @Test
    fun `a negative body length reads as no body rather than failing`(): Unit = runBlocking {
        val text = "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: -5\r\n\r\n"
        val client = RtspClient(ScriptedTransport(listOf(text.toByteArray(Charsets.US_ASCII))), "h")

        assertEquals("", client.describe().body)
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
        private val blocksWhenEmpty: Boolean = false,
    ) : CameraTransport {
        private val queued = ArrayDeque<ByteArray>()
        private var next = 0

        val sent = mutableListOf<String>()

        /** How many reads there have been. */
        var reads = 0
            private set

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
            reads++
            val reply = queued.removeFirstOrNull() ?: if (blocksWhenEmpty) awaitCancellation() else return 0
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

        /** The Transport header the reference camera answered SETUP with on 2026-10-06. */
        const val REFERENCE_TRANSPORT = "RTP/AVP;unicast;client_port=63721-63722;server_port=59728-59729"
    }
}
