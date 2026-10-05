package com.rextechnologies.sightline.protocol

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.asExecutor
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.io.Closeable
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException

/**
 * A byte pipe to the camera.
 *
 * Every socket in this project goes through this seam for two reasons. Tests get a camera that
 * needs no hardware, and the real implementation is the single place that must bind to the
 * adapter the camera is on — which on a phone or a laptop with another network is the difference
 * between reaching the camera and quietly leaving over the wrong interface.
 *
 * Where the .NET seam takes a cancellation token, this one is cancelled the Kotlin way: cancelling the
 * coroutine that is waiting on a call gives up waiting.
 */
interface CameraTransport : Closeable {
    /** Whether the pipe is open. */
    val isConnected: Boolean

    /** Opens the pipe. */
    suspend fun connect()

    /** Sends every byte of [bytes]. */
    suspend fun send(bytes: ByteArray)

    /** Reads what has arrived into [into], returning how many bytes that was, or 0 when the camera closed the pipe. */
    suspend fun receive(into: ByteArray): Int
}

/**
 * A TCP pipe to the camera, optionally bound to one local address.
 *
 * @param endpoint The camera's address and port.
 * @param bindTo The local address to send from. Supplying it is what keeps the traffic on the adapter
 *   the camera is actually on when the machine has another network; leaving it null lets the routing
 *   table decide, which is right only when nothing else could be chosen.
 * @param newSocket Makes the unconnected socket. On a phone this is the camera network's own socket
 *   factory, whose sockets travel over the camera's Wi-Fi whatever the default route is: that is how
 *   the phone reaches the camera while mobile data carries everything else.
 */
class TcpCameraTransport(
    private val endpoint: InetSocketAddress,
    private val bindTo: InetAddress? = null,
    private val newSocket: () -> Socket = ::Socket,
) : CameraTransport {
    @Volatile
    private var socket: Socket? = null

    override val isConnected: Boolean
        get() = socket != null

    override suspend fun connect() {
        val created = newSocket()
        try {
            if (bindTo != null) {
                created.bind(InetSocketAddress(bindTo, 0))
            }

            // Nagle batches small writes, and every control command here is small and latency
            // sensitive: a shutter press should not wait for a second packet to keep it company.
            created.tcpNoDelay = true
            // How often a read with nothing arriving wakes to see whether its caller has given up.
            created.soTimeout = CANCELLATION_CHECK_MILLIS
            // A connect blocks in the kernel and ignores interruption, so it runs on its own thread and
            // the caller can give up on it; the socket is then closed below, which is what releases it.
            suspendCancellableCoroutine<Unit> { waiting ->
                Dispatchers.IO.asExecutor().execute {
                    waiting.resumeWith(runCatching { created.connect(endpoint) })
                }
            }
        } catch (failure: Throwable) {
            created.close()
            throw failure
        }

        socket = created
    }

    override suspend fun send(bytes: ByteArray) {
        val output = connected().getOutputStream()
        withContext(Dispatchers.IO) {
            // The stream hands every byte to the kernel before it returns. A request is a few bytes
            // that go straight into the socket's buffer, so there is no wait here worth cancelling.
            output.write(bytes)
        }
    }

    override suspend fun receive(into: ByteArray): Int {
        val input = connected().getInputStream()
        return withContext(Dispatchers.IO) {
            var read: Int
            while (true) {
                try {
                    read = input.read(into)
                    break
                } catch (idle: SocketTimeoutException) {
                    // Nothing yet. A read blocked in the kernel cannot see that its caller gave up, so
                    // it wakes to look. Giving up leaves the connection open, because closing the
                    // control channel is what stops the camera recording.
                    ensureActive()
                }
            }
            if (read < 0) 0 else read
        }
    }

    override fun close() {
        socket?.close()
        socket = null
    }

    private fun connected(): Socket = socket ?: throw IllegalStateException("The transport is not connected.")

    private companion object {
        const val CANCELLATION_CHECK_MILLIS = 100
    }
}
