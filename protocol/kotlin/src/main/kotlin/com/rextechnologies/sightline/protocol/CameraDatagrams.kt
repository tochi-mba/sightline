package com.rextechnologies.sightline.protocol

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import java.io.Closeable
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

/**
 * The datagrams the camera's stream arrives in.
 *
 * The camera's pictures come over UDP. It will also send them down its RTSP connection, but in that mode
 * it answers one stream each time it is switched on, and once that stream ends its own buttons stop
 * working until its battery comes out; see PROTOCOL.md, "The stream goes over UDP".
 *
 * A seam for the same two reasons as [CameraTransport]: tests stand a fake camera behind it, and the real
 * one travels over the camera's network and nothing else.
 */
interface CameraDatagrams : Closeable {
    /** The local port the datagrams arrive at, which SETUP tells the camera. */
    val port: Int

    /** Sends one datagram to [port] on the camera. */
    suspend fun send(datagram: ByteArray, port: Int)

    /** Waits for the next datagram from the camera, puts it in [into], and returns its length. */
    suspend fun receive(into: ByteArray): Int
}

/**
 * The camera's network as this device reaches it: a connection to any of the camera's ports, and a
 * socket for its stream's datagrams.
 *
 * One seam for both, so whatever put this device on the camera's network is also what decides how every
 * socket gets there.
 */
interface CameraSockets {
    /** A connection to [port] on the camera, not yet opened. */
    fun transport(port: Int): CameraTransport

    /** A socket for the camera's datagrams, bound and ready. */
    fun datagrams(): CameraDatagrams
}

/**
 * A UDP socket on the camera's network that hears only the camera.
 *
 * @param camera The camera's address: datagrams from anywhere else are not its stream.
 * @param socket The socket, already bound. On a phone it is bound to the camera's network as well, so it
 *   travels over the camera's Wi-Fi whatever the default route is.
 */
class UdpCameraDatagrams(private val camera: InetAddress, private val socket: DatagramSocket) : CameraDatagrams {
    init {
        // How often a receive with nothing arriving wakes to see whether its caller has given up.
        socket.soTimeout = CANCELLATION_CHECK_MILLIS
    }

    override val port: Int
        get() = socket.localPort

    override suspend fun send(datagram: ByteArray, port: Int) {
        withContext(Dispatchers.IO) {
            socket.send(DatagramPacket(datagram, datagram.size, camera, port))
        }
    }

    override suspend fun receive(into: ByteArray): Int = withContext(Dispatchers.IO) {
        val packet = DatagramPacket(into, into.size)
        var length = -1
        while (length < 0) {
            try {
                packet.setLength(into.size)
                socket.receive(packet)
                // Anybody else on the camera's network: not the camera's picture, whatever it looks like.
                length = if (packet.address == camera) packet.length else -1
            } catch (idle: SocketTimeoutException) {
                // Nothing yet. A receive blocked in the kernel cannot see that its caller gave up, so it
                // wakes to look.
                ensureActive()
            }
        }

        length
    }

    override fun close() {
        socket.close()
    }

    private companion object {
        const val CANCELLATION_CHECK_MILLIS = 100
    }
}
