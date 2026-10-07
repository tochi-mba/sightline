package com.rextechnologies.sightline.protocol

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlin.time.Duration

/**
 * The camera's network, needing no hardware: connections from a function, and datagram sockets that the
 * fake RTSP camera finds by the port SETUP names, as the real camera finds the phone's.
 */
class FakeCameraSockets(private val transports: (port: Int) -> CameraTransport) : CameraSockets {
    private val opened = mutableListOf<FakeCameraDatagrams>()
    private var nextPort = 50100

    /** Every datagram socket opened, in order. */
    val openedSockets: List<FakeCameraDatagrams>
        get() = synchronized(opened) { opened.toList() }

    override fun transport(port: Int): CameraTransport = transports(port).also {
        if (it is FakeRtspCamera) {
            it.sockets = this
        }
    }

    // Odd ports as well as even: the real camera takes whichever it is told.
    override fun datagrams(): CameraDatagrams = synchronized(opened) {
        FakeCameraDatagrams(nextPort++).also(opened::add)
    }

    /** The open socket at [port], or null if nothing is listening there. */
    fun at(port: Int): FakeCameraDatagrams? = synchronized(opened) {
        opened.lastOrNull { it.port == port && !it.isClosed }
    }
}

/**
 * One datagram socket on the fake network. The camera delivers into it; whoever opened it receives, each
 * datagram after the delay it was delivered with, as the real camera's dozen pictures a second arrive.
 */
class FakeCameraDatagrams(override val port: Int) : CameraDatagrams {
    private val inbox = Channel<Pair<ByteArray, Duration>>(Channel.UNLIMITED)
    private val lock = Any()
    private var inFlight = 0
    private var idle: CompletableDeferred<Unit>? = null

    /** What was sent from this socket, and to which of the camera's ports. */
    val sent = mutableListOf<Pair<Int, ByteArray>>()

    /** Whether it has been closed. */
    var isClosed = false
        private set

    /** Returns once every datagram delivered so far has been received, or dropped. */
    suspend fun awaitIdle() {
        val waiting = synchronized(lock) {
            if (inFlight == 0) null else idle ?: CompletableDeferred<Unit>().also { idle = it }
        }
        waiting?.await()
    }

    /** Delivers one datagram, to be received [delay] after the one before. */
    fun deliver(datagram: ByteArray, delay: Duration = Duration.ZERO) {
        synchronized(lock) {
            if (isClosed) {
                return
            }

            inFlight++
        }

        inbox.trySend(datagram to delay)
    }

    /** Drops everything delivered and not yet received, as datagrams in flight are when a stream is cut. */
    fun drop() {
        while (inbox.tryReceive().getOrNull() != null) {
            taken()
        }
    }

    override suspend fun send(datagram: ByteArray, port: Int) {
        synchronized(sent) { sent += port to datagram.copyOf() }
    }

    override suspend fun receive(into: ByteArray): Int {
        val (datagram, wait) = inbox.receive()
        try {
            if (wait.isPositive()) {
                delay(wait)
            }
        } finally {
            taken()
        }

        // At most the reader's buffer, as a real socket truncates a datagram too long for it.
        val take = minOf(datagram.size, into.size)
        datagram.copyInto(into, 0, 0, take)
        return take
    }

    override fun close() {
        synchronized(lock) { isClosed = true }
        drop()
    }

    private fun taken() {
        val settled = synchronized(lock) {
            inFlight--
            if (inFlight == 0) idle.also { idle = null } else null
        }
        settled?.complete(Unit)
    }
}
