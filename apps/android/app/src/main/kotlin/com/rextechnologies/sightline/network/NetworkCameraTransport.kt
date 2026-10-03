package com.rextechnologies.sightline.network

import android.net.Network
import com.rextechnologies.sightline.protocol.CameraTransport
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException

/** A camera socket created by the local-only Network, leaving the process default route untouched. */
class NetworkCameraTransport(
    private val network: Network,
    private val endpoint: InetSocketAddress,
) : CameraTransport {
    @Volatile
    private var socket: Socket? = null

    override val isConnected: Boolean get() = socket != null

    override suspend fun connect() {
        val created = network.socketFactory.createSocket()
        try {
            created.tcpNoDelay = true
            created.soTimeout = CANCELLATION_CHECK_MILLIS
            withContext(Dispatchers.IO) { created.connect(endpoint, CONNECT_TIMEOUT_MILLIS) }
            socket = created
        } catch (failure: Throwable) {
            created.close()
            throw failure
        }
    }

    override suspend fun send(bytes: ByteArray) {
        val output = connected().getOutputStream()
        withContext(Dispatchers.IO) { output.write(bytes) }
    }

    override suspend fun receive(into: ByteArray): Int {
        val input = connected().getInputStream()
        return withContext(Dispatchers.IO) {
            while (true) {
                try {
                    val read = input.read(into)
                    return@withContext if (read < 0) 0 else read
                } catch (_: SocketTimeoutException) {
                    ensureActive()
                }
            }
            @Suppress("UNREACHABLE_CODE")
            0
        }
    }

    override fun close() {
        socket?.close()
        socket = null
    }

    private fun connected(): Socket = socket ?: error("The camera transport is not connected.")

    private companion object {
        const val CONNECT_TIMEOUT_MILLIS = 10_000
        const val CANCELLATION_CHECK_MILLIS = 200
    }
}
