package com.rextechnologies.sightline.link

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSpecifier
import android.os.PatternMatcher
import com.rextechnologies.sightline.core.link.CameraLink
import com.rextechnologies.sightline.core.link.CameraLinkException
import com.rextechnologies.sightline.core.link.CameraLinkLease
import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.link.LinkFailure
import com.rextechnologies.sightline.core.session.CameraSession
import com.rextechnologies.sightline.protocol.CameraTransport
import com.rextechnologies.sightline.protocol.TcpCameraTransport
import kotlinx.coroutines.CompletableDeferred
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicBoolean

/**
 * The few calls on the phone's network service that joining the camera needs.
 *
 * A seam so the lease's logic, which is where a mistake strands the phone on the camera's Wi-Fi, is
 * tested without a radio.
 */
interface NetworkSystem {
    /** Whether the phone's Wi-Fi is switched on. */
    val wifiEnabled: Boolean

    /**
     * Asks for a network matching [request], reported to [callback], giving up after [timeoutMillis].
     *
     * @throws SecurityException The app lacks a permission the request needs.
     */
    fun request(request: NetworkRequest, callback: ConnectivityManager.NetworkCallback, timeoutMillis: Int)

    /** Withdraws the request [callback] made; the phone leaves that network if nothing else wants it. */
    fun release(callback: ConnectivityManager.NetworkCallback)
}

/** The real [NetworkSystem]. */
class AndroidNetworkSystem(context: Context) : NetworkSystem {
    private val connectivity: ConnectivityManager = context.getSystemService(ConnectivityManager::class.java)
    private val wifi: WifiManager = context.getSystemService(WifiManager::class.java)

    override val wifiEnabled: Boolean get() = wifi.isWifiEnabled

    override fun request(request: NetworkRequest, callback: ConnectivityManager.NetworkCallback, timeoutMillis: Int) {
        connectivity.requestNetwork(request, callback, timeoutMillis)
    }

    override fun release(callback: ConnectivityManager.NetworkCallback) {
        connectivity.unregisterNetworkCallback(callback)
    }
}

/**
 * Joins the camera's Wi-Fi as a local-only network, the one way Android offers to reach a device on Wi-Fi
 * without giving up the phone's internet.
 *
 * The request asks for Wi-Fi without the internet capability, so the system never makes the camera the
 * default route: mobile data, or whatever else the phone was online through, carries on carrying
 * everything that is not for the camera. Only sockets made from the camera's [Network] reach it. Nothing
 * here binds the whole process to the camera, switches mobile data or Wi-Fi, or forgets a network.
 *
 * The first request names only the family's prefix, so Android shows its own list of matching cameras
 * and the person taps theirs. A later request for exactly that camera, by name, Android can grant with no
 * dialog at all.
 */
class AndroidCameraLink(
    private val system: NetworkSystem,
    private val timeoutMillis: Int = JOIN_TIMEOUT_MILLIS,
) : CameraLink {
    override suspend fun join(network: CameraNetwork): CameraLinkLease {
        if (!system.wifiEnabled) {
            throw CameraLinkException(LinkFailure.WifiOff, "Wi-Fi is switched off.")
        }

        val lease = AndroidCameraLease(system)
        try {
            system.request(requestFor(network), lease.callback, timeoutMillis)
        } catch (denied: SecurityException) {
            throw CameraLinkException(LinkFailure.PermissionDenied, denied.message ?: "A permission is missing.")
        }

        try {
            lease.awaitJoined()
        } catch (failure: Throwable) {
            // Given up, refused or timed out: either way the request is withdrawn, so the phone never
            // joins the camera after the person has moved on.
            lease.close()
            throw failure
        }

        return lease
    }

    companion object {
        /** How long Android gets to find the camera and the person to pick it. */
        const val JOIN_TIMEOUT_MILLIS = 60_000

        /** The request for [network]: Wi-Fi, local only, to the camera named or any with its prefix. */
        fun requestFor(network: CameraNetwork): NetworkRequest {
            val specifier = WifiNetworkSpecifier.Builder().apply {
                val name = network.name
                if (name != null) {
                    setSsid(name)
                } else {
                    setSsidPattern(PatternMatcher(network.prefix, PatternMatcher.PATTERN_PREFIX))
                }
                setWpa2Passphrase(network.password)
            }.build()

            return NetworkRequest.Builder()
                .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .setNetworkSpecifier(specifier)
                .build()
        }
    }
}

/**
 * The camera's network, while the app holds it.
 *
 * Every callback arrives on the system's own thread, so what they report is handed over through
 * deferreds, which any thread may complete once.
 */
internal class AndroidCameraLease(private val system: NetworkSystem) : CameraLinkLease {
    private val joined = CompletableDeferred<Network>()
    private val lost = CompletableDeferred<Unit>()
    private val released = AtomicBoolean(false)

    val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            joined.complete(network)
        }

        override fun onUnavailable() {
            joined.completeExceptionally(
                CameraLinkException(LinkFailure.Unavailable, "Android did not find or join the camera's Wi-Fi."),
            )
        }

        override fun onLost(network: Network) {
            lost.complete(Unit)
        }
    }

    suspend fun awaitJoined(): Network = joined.await()

    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    override fun transport(port: Int): CameraTransport {
        // Only called once joined, so the network is there to be had.
        val network = joined.getCompleted()
        return TcpCameraTransport(
            InetSocketAddress(CameraSession.CAMERA_HOST, port),
            newSocket = network.socketFactory::createSocket,
        )
    }

    override suspend fun awaitLoss() = lost.await()

    override fun close() {
        if (released.compareAndSet(false, true)) {
            // The system drops a request that timed out or was refused by itself, and says so by throwing
            // when it is released again; that is the outcome wanted, so it is not an error here.
            runCatching { system.release(callback) }
        }
    }
}
