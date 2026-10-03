package com.rextechnologies.sightline.network

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiNetworkSpecifier
import android.os.PatternMatcher
import kotlinx.coroutines.suspendCancellableCoroutine
import java.io.Closeable
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/** A local-only Wi-Fi request. It never changes the default network or disables mobile data. */
class LocalOnlyCameraNetwork(context: Context) {
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)

    suspend fun request(networkName: String, password: String): CameraNetworkLease {
        require(networkName.isNotBlank()) { "Enter the camera network name." }
        require(password.length in 8..63) { "The camera password must be 8 to 63 characters." }

        val specifier = WifiNetworkSpecifier.Builder().apply {
            if (networkName.endsWith('*')) {
                setSsidPattern(PatternMatcher(networkName.dropLast(1), PatternMatcher.PATTERN_PREFIX))
            } else {
                setSsid(networkName)
            }
            setWpa2Passphrase(password)
        }.build()
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .setNetworkSpecifier(specifier)
            .build()

        return suspendCancellableCoroutine { waiting ->
            var delivered = false
            val callback = object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) {
                    if (!delivered) {
                        delivered = true
                        waiting.resume(CameraNetworkLease(connectivity, this, network))
                    }
                }

                override fun onUnavailable() {
                    if (!delivered) {
                        delivered = true
                        waiting.resumeWithException(CameraNetworkUnavailableException())
                    }
                }
            }
            waiting.invokeOnCancellation {
                if (!delivered) runCatching { connectivity.unregisterNetworkCallback(callback) }
            }
            connectivity.requestNetwork(request, callback)
        }
    }
}

class CameraNetworkLease internal constructor(
    private val connectivity: ConnectivityManager,
    private val callback: ConnectivityManager.NetworkCallback,
    val network: Network,
) : Closeable {
    override fun close() {
        connectivity.unregisterNetworkCallback(callback)
    }
}

class CameraNetworkUnavailableException : Exception(
    "Android could not join the camera. Keep mobile data and hotspot on, wake the camera, and approve the Wi-Fi prompt.",
)
