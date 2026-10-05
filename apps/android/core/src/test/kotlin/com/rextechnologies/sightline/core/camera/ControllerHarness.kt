package com.rextechnologies.sightline.core.camera

import com.rextechnologies.sightline.core.library.MediaKind
import com.rextechnologies.sightline.core.library.MediaSink
import com.rextechnologies.sightline.core.library.PendingMedia
import com.rextechnologies.sightline.core.link.CameraLink
import com.rextechnologies.sightline.core.link.CameraLinkException
import com.rextechnologies.sightline.core.link.CameraLinkLease
import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.link.LinkFailure
import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.FakeCamera
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import com.rextechnologies.sightline.protocol.gpsock.GpSockConnection
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.withTimeout
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.OutputStream
import kotlin.time.Duration.Companion.minutes

/**
 * A controller driving a fake camera over a fake network, on the test's virtual clock.
 *
 * The camera describes itself with the reference camera's own menu, so every test runs against the
 * twenty-one settings a real one has. Each live view gets a fresh fake stream, as each gets a fresh
 * connection on the wire.
 */
class ControllerHarness(test: TestScope) {
    val control = FakeCamera().apply { menuXml = referenceMenu() }
    val streams = mutableListOf<FakeRtspCamera>()

    /** How each new stream is set up before the controller opens it. */
    var stream: FakeRtspCamera.() -> Unit = { frames += FakeRtspCamera.jpeg(600) }

    val link = FakeLink()
    val controller = CameraController(test.backgroundScope, link, timeSource = test.testScheduler.timeSource)

    /** Every state the controller has been in, in order. */
    val seen = mutableListOf<CameraState>()

    init {
        test.backgroundScope.launch(UnconfinedTestDispatcher(test.testScheduler)) {
            controller.state.collect { seen += it }
        }
    }

    val state: CameraState get() = controller.state.value

    /** Waits, on virtual time, for the controller to reach a state [predicate] accepts. */
    suspend fun until(predicate: (CameraState) -> Boolean): CameraState =
        withTimeout(30.minutes) { controller.state.first(predicate) }

    /** Connects with the defaults and waits until connected. */
    suspend fun connected(): CameraState {
        controller.connect(CameraNetwork())
        return until { it.isConnected }
    }

    /** The network every join is answered with, in order; each failure listed is used once. */
    inner class FakeLink : CameraLink {
        val requests = mutableListOf<CameraNetwork>()
        val failures = ArrayDeque<LinkFailure>()
        val leases = mutableListOf<FakeLease>()

        /** When set, a join waits until it is given up, as one waiting on a person does. */
        var waits = false

        /** When set, the camera's control port accepts nothing, as a camera that is not there. */
        var controlTransport: (() -> CameraTransport)? = null

        override suspend fun join(network: CameraNetwork): CameraLinkLease {
            requests += network
            if (waits) {
                awaitCancellation()
            }

            failures.removeFirstOrNull()?.let { throw CameraLinkException(it, "Not joined ($it).") }
            return FakeLease().also { leases += it }
        }

        /** The lease currently held. */
        val lease: FakeLease get() = leases.last()
    }

    inner class FakeLease : CameraLinkLease {
        private val loss = CompletableDeferred<Unit>()
        var closed = false

        /** Has the system lose the camera's network. */
        fun lose() {
            loss.complete(Unit)
        }

        override fun transport(port: Int): CameraTransport = if (port == GpSockConnection.PORT) {
            link.controlTransport?.invoke() ?: control
        } else {
            FakeRtspCamera().apply {
                streamStarted = { control.isStreaming }
                stream()
                streams += this
            }
        }

        override suspend fun awaitLoss() = loss.await()

        override fun close() {
            closed = true
        }
    }

    companion object {
        fun referenceMenu(): String =
            checkNotNull(ControllerHarness::class.java.getResource("/menu/reference-camera.xml")).readText()
    }
}

/** A gallery that keeps everything in memory, and can be told to fail. */
class FakeSink : MediaSink {
    val created = mutableListOf<Pair<CameraFile, MediaKind>>()
    val published = mutableMapOf<String, ByteArray>()
    val discarded = mutableListOf<CameraFile>()

    /** When set, creating a file fails the way a full phone does. */
    var full = false

    /** When set, writing fails after this many bytes. */
    var failsAfterBytes: Int? = null

    /** When set, throwing the file away fails too. */
    var discardFails = false

    override fun create(file: CameraFile, kind: MediaKind): PendingMedia {
        if (full) {
            throw IOException("No space left on the device.")
        }

        created += file to kind
        val bytes = LimitedOutput(failsAfterBytes)
        return object : PendingMedia {
            override val output: OutputStream = bytes

            override fun publish(): String {
                val name = "Gallery/${file.displayName}${kind.extension}"
                published[name] = bytes.toByteArray()
                return name
            }

            override fun discard() {
                discarded += file
                if (discardFails) {
                    throw IOException("The pending file was already gone.")
                }
            }
        }
    }

    private class LimitedOutput(private val limit: Int?) : ByteArrayOutputStream() {
        override fun write(b: ByteArray, off: Int, len: Int) {
            if (limit != null && size() + len > limit) {
                throw IOException("No space left on the device.")
            }

            super.write(b, off, len)
        }
    }
}
