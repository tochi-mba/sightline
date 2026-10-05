package com.rextechnologies.sightline

import android.graphics.Bitmap
import android.graphics.Color
import android.os.Looper
import androidx.test.core.app.ApplicationProvider
import com.rextechnologies.sightline.core.camera.CameraState
import com.rextechnologies.sightline.core.library.MediaKind
import com.rextechnologies.sightline.core.library.MediaSink
import com.rextechnologies.sightline.core.library.PendingMedia
import com.rextechnologies.sightline.core.link.CameraLink
import com.rextechnologies.sightline.core.link.CameraLinkException
import com.rextechnologies.sightline.core.link.CameraLinkLease
import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.link.LinkFailure
import com.rextechnologies.sightline.core.link.PhoneNetworks
import com.rextechnologies.sightline.core.sentry.SentryActions
import com.rextechnologies.sightline.core.settings.SettingsStore
import com.rextechnologies.sightline.core.updates.WhatsNewEntry
import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.FakeCamera
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import com.rextechnologies.sightline.protocol.gpsock.GpSockConnection
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.awaitCancellation
import org.robolectric.Shadows.shadowOf
import java.io.ByteArrayOutputStream
import java.io.OutputStream
import java.time.Duration
import java.time.Instant

/** Settings in a map. */
class MapStore : SettingsStore {
    val saved = mutableMapOf<String, String>()

    override fun read(key: String): String? = saved[key]

    override fun write(key: String, value: String?) {
        if (value == null) saved.remove(key) else saved[key] = value
    }
}

/** A gallery in memory. */
class MemorySink : MediaSink {
    val published = mutableMapOf<String, ByteArray>()

    override fun create(file: CameraFile, kind: MediaKind): PendingMedia {
        val bytes = ByteArrayOutputStream()
        return object : PendingMedia {
            override val output: OutputStream = bytes

            override fun publish(): String = "Gallery/${file.displayName}${kind.extension}".also {
                published[it] =
                    bytes.toByteArray()
            }

            override fun discard() = Unit
        }
    }
}

/** What Sentry asked the phone to do. */
class RecordedSentryActions : SentryActions {
    val raised = mutableListOf<Instant>()
    var ended = 0

    override fun alarmRaised(at: Instant, snapshot: ByteArray, saveSnapshot: Boolean) {
        raised += at
    }

    override fun alarmEnded() {
        ended++
    }
}

/**
 * The shared fake camera behind a fake network: every join succeeds unless told otherwise, and every live
 * view gets a fresh fake stream.
 */
class FakeLink(private val control: FakeCamera) : CameraLink {
    val requests = mutableListOf<CameraNetwork>()
    val failures = ArrayDeque<LinkFailure>()
    var waits = false
    var stream: FakeRtspCamera.() -> Unit = { frames += TestGraph.picture() }
    private val leases = mutableListOf<Lease>()

    override suspend fun join(network: CameraNetwork): CameraLinkLease {
        requests += network
        if (waits) awaitCancellation()
        failures.removeFirstOrNull()?.let { throw CameraLinkException(it, "Not joined ($it).") }
        return Lease().also { leases += it }
    }

    /** Has the system lose the camera's network. */
    fun lose() = leases.last().loss.complete(Unit)

    inner class Lease : CameraLinkLease {
        val loss = CompletableDeferred<Unit>()

        override fun transport(port: Int): CameraTransport = if (port == GpSockConnection.PORT) {
            control
        } else {
            FakeRtspCamera().apply {
                streamStarted = { control.isStreaming }
                stream()
            }
        }

        override suspend fun awaitLoss() = loss.await()

        override fun close() = Unit
    }
}

/** A graph for tests: the fake camera, settings in a map, a memory gallery, and the phone on mobile data. */
class TestGraph(
    val camera: FakeCamera = FakeCamera().apply { menuXml = referenceMenu() },
    val store: MapStore = MapStore(),
    val gallery: MemorySink = MemorySink(),
    val actions: RecordedSentryActions = RecordedSentryActions(),
    var networks: PhoneNetworks = PhoneNetworks(
        wifiEnabled = true,
        onWifi = false,
        mobileData = true,
        sideBySideWifi = false,
        hotspotOn = false,
        hotspotAlongsideWifi = true,
        vpnOn = false,
    ),
    version: String = "0.1.0",
    releaseNotes: List<WhatsNewEntry> = emptyList(),
    /** What the update check is answered with; offline unless a test says otherwise. */
    var answer: () -> String = { throw java.io.IOException("offline") },
) {
    val link = FakeLink(camera)
    val graph = AppGraph(
        context = ApplicationProvider.getApplicationContext(),
        scope = MainScope(),
        store = store,
        link = link,
        gallery = gallery,
        phoneNetworks = { networks },
        sentryActions = actions,
        version = version,
        releaseNotes = releaseNotes,
        fetch = { answer() },
        io = kotlinx.coroutines.Dispatchers.Unconfined,
        timeSource = LooperTime,
    )

    /** Runs the main thread's queue, then [duration] of its clock: the controller lives there. */
    fun settle(duration: Duration = Duration.ZERO) {
        shadowOf(Looper.getMainLooper()).idleFor(duration)
        shadowOf(Looper.getMainLooper()).idle()
    }

    /**
     * Runs the main thread a tenth of a second at a time until [condition] holds of the camera's state.
     *
     * Sockets to the fake camera run on real threads, so how much main-thread time a step takes is not
     * fixed; waiting for the state, rather than for a guessed duration, keeps a busy machine from failing.
     */
    fun until(condition: (CameraState) -> Boolean) {
        repeat(50) { if (!condition(graph.controller.state.value)) settle(Duration.ofMillis(100)) }
        check(condition(graph.controller.state.value)) { "Never happened: ${graph.controller.state.value}" }
    }

    /** Connects and runs the main thread until connected. */
    fun connected(): TestGraph {
        graph.controller.connect(graph.cameraNetwork())
        until { it.isConnected }
        return this
    }

    companion object {
        /**
         * A small JPEG the phone's own decoder accepts, as the camera's frames are; the protocol
         * fixtures' placeholder bytes only look like one to the protocol.
         */
        fun picture(): ByteArray {
            val bitmap = Bitmap.createBitmap(64, 36, Bitmap.Config.ARGB_8888).apply {
                eraseColor(Color.rgb(40, 90, 60))
            }
            return ByteArrayOutputStream().also { bitmap.compress(Bitmap.CompressFormat.JPEG, 80, it) }.toByteArray()
        }

        fun referenceMenu(): String =
            checkNotNull(TestGraph::class.java.getResource("/menu/reference-camera.xml")).readText()
    }
}
