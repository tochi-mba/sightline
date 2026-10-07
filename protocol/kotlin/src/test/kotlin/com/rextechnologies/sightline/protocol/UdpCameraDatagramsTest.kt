package com.rextechnologies.sightline.protocol

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketException
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/**
 * The real datagram socket, against sockets standing in for the camera on this machine's loopback addresses.
 *
 * Loopback only: these tests never touch a Wi-Fi adapter or any network a person depends on.
 */
class UdpCameraDatagramsTest {
    private val loopback: InetAddress = InetAddress.getByName("127.0.0.1")

    private fun socket(address: InetAddress = loopback) = DatagramSocket(InetSocketAddress(address, 0))

    @Test
    fun `datagrams from the camera arrive and datagrams sent reach the port named`(): Unit = runBlocking {
        socket().use { camera ->
            UdpCameraDatagrams(loopback, socket()).use { datagrams ->
                assertTrue(datagrams.port > 0)

                datagrams.send(byteArrayOf(0), camera.localPort)
                val heard = DatagramPacket(ByteArray(16), 16)
                camera.soTimeout = 5_000
                camera.receive(heard)
                assertEquals(1, heard.length)
                assertEquals(datagrams.port, heard.port)

                camera.send(DatagramPacket(bytes(0x80, 0x9A, 7), 3, loopback, datagrams.port))
                val into = ByteArray(16)
                val length = withTimeout(5_000) { datagrams.receive(into) }
                assertContentEquals(bytes(0x80, 0x9A, 7), into.copyOf(length))
            }
        }
    }

    @Test
    fun `a caller already on the io dispatcher is served in place`(): Unit = runBlocking(Dispatchers.IO) {
        socket().use { camera ->
            UdpCameraDatagrams(loopback, socket()).use { datagrams ->
                datagrams.send(byteArrayOf(9), camera.localPort)
                camera.send(DatagramPacket(bytes(0x80), 1, loopback, datagrams.port))

                assertEquals(1, withTimeout(5_000) { datagrams.receive(ByteArray(16)) })
            }
        }
    }

    @Test
    fun `datagrams from anybody but the camera are not its picture`(): Unit = runBlocking {
        // Another phone on the camera's network could send anything to this port.
        socket().use { camera ->
            socket(InetAddress.getByName("127.0.0.2")).use { stranger ->
                UdpCameraDatagrams(loopback, socket()).use { datagrams ->
                    stranger.send(DatagramPacket(bytes(0x66), 1, loopback, datagrams.port))
                    camera.send(DatagramPacket(bytes(0x80), 1, loopback, datagrams.port))
                    val into = ByteArray(16)

                    val length = withTimeout(5_000) { datagrams.receive(into) }

                    assertContentEquals(bytes(0x80), into.copyOf(length))
                }
            }
        }
    }

    @Test
    fun `waiting for a datagram can be given up`(): Unit = runBlocking {
        UdpCameraDatagrams(loopback, socket()).use { datagrams ->
            assertFailsWith<TimeoutCancellationException> { withTimeout(250) { datagrams.receive(ByteArray(16)) } }
        }
    }

    @Test
    fun `a closed socket receives nothing more`(): Unit = runBlocking {
        val datagrams = UdpCameraDatagrams(loopback, socket())

        datagrams.close()

        assertFailsWith<SocketException> { datagrams.receive(ByteArray(16)) }
    }
}
