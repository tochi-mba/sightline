package com.rextechnologies.sightline.protocol

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import java.net.BindException
import java.net.ConnectException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The real transport, against a listening socket on this machine's loopback interface standing where
 * the camera would. Nothing here leaves the machine.
 */
class TcpCameraTransportTest {
    private val loopback = InetAddress.getLoopbackAddress()
    private val camera = ServerSocket(0, 1, loopback)
    private val cameraAddress = InetSocketAddress(loopback, camera.localPort)

    @AfterTest
    fun stopCamera() {
        camera.close()
    }

    /** The camera's end of the connection the transport just made. */
    private fun answer(): Socket = camera.accept().apply { soTimeout = 5_000 }

    private suspend fun CameraTransport.receiveExactly(count: Int): ByteArray {
        val gathered = ByteQueue()
        val into = ByteArray(count)
        while (gathered.size < count) {
            gathered.append(into, 0, receive(into))
        }
        return gathered.toByteArray()
    }

    @Test
    fun `it carries bytes to the camera and back`(): Unit = runBlocking {
        val transport = TcpCameraTransport(cameraAddress)
        assertFalse(transport.isConnected)

        transport.connect()
        answer().use { peer ->
            assertTrue(transport.isConnected)

            transport.send(bytes(1, 2, 3))
            assertContentEquals(bytes(1, 2, 3), peer.getInputStream().readNBytes(3))

            peer.getOutputStream().write(bytes(4, 5))
            assertContentEquals(bytes(4, 5), transport.receiveExactly(2))
        }

        transport.close()
        assertFalse(transport.isConnected)
    }

    @Test
    fun `a caller already on the io dispatcher is served in place`(): Unit = runBlocking(Dispatchers.IO) {
        // An app's session loop usually runs on the IO dispatcher already; the transport then does
        // its blocking work in place rather than hopping to another thread.
        val transport = TcpCameraTransport(cameraAddress)
        transport.connect()
        answer().use { peer ->
            transport.send(bytes(7))
            assertContentEquals(bytes(7), peer.getInputStream().readNBytes(1))

            peer.getOutputStream().write(bytes(8))
            assertContentEquals(bytes(8), transport.receiveExactly(1))
        }

        transport.close()
    }

    @Test
    fun `it sends from the local address it is told to bind to`(): Unit = runBlocking {
        val transport = TcpCameraTransport(cameraAddress, bindTo = loopback)

        transport.connect()

        answer().use { peer -> assertEquals(loopback, peer.inetAddress) }
        transport.close()
    }

    @Test
    fun `it connects the socket its factory makes`(): Unit = runBlocking {
        // A phone's camera network hands out its own sockets, and a socket made any other way would
        // leave over mobile data instead of reaching the camera.
        val made = mutableListOf<Socket>()
        val transport = TcpCameraTransport(cameraAddress, newSocket = { Socket().also(made::add) })

        transport.connect()

        answer().use { assertTrue(made.single().isConnected) }
        transport.close()
        assertTrue(made.single().isClosed)
    }

    @Test
    fun `an address this machine does not have is refused rather than routed round`(): Unit = runBlocking {
        // 192.0.2.1 is reserved for documentation, so no interface has it: binding fails locally,
        // which is the failure a phone that has left the camera's Wi-Fi should see.
        val transport = TcpCameraTransport(cameraAddress, bindTo = InetAddress.getByName("192.0.2.1"))

        assertFailsWith<BindException> { transport.connect() }

        assertFalse(transport.isConnected)
    }

    @Test
    fun `a camera that is not listening is reported`(): Unit = runBlocking {
        val closedPort = ServerSocket(0, 1, loopback).use { it.localPort }
        val transport = TcpCameraTransport(InetSocketAddress(loopback, closedPort))

        assertFailsWith<ConnectException> { transport.connect() }

        assertFalse(transport.isConnected)
    }

    @Test
    fun `a camera that closes the connection reads as nothing`(): Unit = runBlocking {
        val transport = TcpCameraTransport(cameraAddress)
        transport.connect()

        answer().close()

        assertEquals(0, transport.receive(ByteArray(16)))
        transport.close()
    }

    @Test
    fun `giving up on a receive leaves the connection open`(): Unit = runBlocking {
        // Closing the control channel stops the camera recording, so a caller that stops waiting
        // for an answer must not take the connection down with it.
        val transport = TcpCameraTransport(cameraAddress)
        transport.connect()
        answer().use { peer ->
            val waiting = async { transport.receive(ByteArray(16)) }
            delay(300)

            waiting.cancelAndJoin()

            assertTrue(waiting.isCancelled)
            assertTrue(transport.isConnected)
            peer.getOutputStream().write(bytes(9))
            assertContentEquals(bytes(9), transport.receiveExactly(1))
        }

        transport.close()
    }

    @Test
    fun `giving up on a connect leaves nothing open`(): Unit = runBlocking {
        val transport = TcpCameraTransport(cameraAddress)

        val connecting = launch {
            // Given up before the connect has even begun, so the outcome cannot depend on timing.
            cancel()
            transport.connect()
        }
        connecting.join()

        assertTrue(connecting.isCancelled)
        assertFalse(transport.isConnected)
    }

    @Test
    fun `using it before it is connected is an error`(): Unit = runBlocking {
        val transport = TcpCameraTransport(cameraAddress)

        assertFailsWith<IllegalStateException> { transport.send(bytes(1)) }
        assertFailsWith<IllegalStateException> { transport.receive(ByteArray(1)) }
    }

    @Test
    fun `closing is harmless before connecting and when repeated`() {
        val transport = TcpCameraTransport(cameraAddress)

        transport.close()
        transport.close()

        assertFalse(transport.isConnected)
    }
}
