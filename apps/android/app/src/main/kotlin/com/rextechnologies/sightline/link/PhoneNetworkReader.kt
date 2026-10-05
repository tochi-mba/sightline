package com.rextechnologies.sightline.link

import android.annotation.TargetApi
import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.os.Build
import android.telephony.TelephonyManager
import com.rextechnologies.sightline.core.link.PhoneNetworks
import java.net.Inet4Address
import java.net.NetworkInterface
import java.util.Collections

/** One of the phone's network interfaces, as far as telling a hotspot apart needs. */
data class InterfaceFacts(val name: String, val up: Boolean, val hasIpv4: Boolean)

/**
 * Reads what the phone is doing with its networks, for the sentence shown before connecting.
 *
 * Read-only: nothing here changes a setting, which is the promise the whole app is built on.
 *
 * @param interfaces The phone's network interfaces; a seam because a test cannot stand up a hotspot.
 * @param localOnlyBesideWifi Whether the Wi-Fi chip can hold a local-only network beside another; a seam
 *     because no test double can claim a chip that does.
 */
class PhoneNetworkReader(
    context: Context,
    private val interfaces: () -> List<InterfaceFacts> = ::liveInterfaces,
    private val localOnlyBesideWifi: (WifiManager) -> Boolean = ::canHoldLocalOnlyBesideWifi,
) {
    private val connectivity: ConnectivityManager = context.getSystemService(ConnectivityManager::class.java)
    private val wifi: WifiManager = context.getSystemService(WifiManager::class.java)
    private val telephony: TelephonyManager = context.getSystemService(TelephonyManager::class.java)

    fun read(): PhoneNetworks {
        val active = connectivity.getNetworkCapabilities(connectivity.activeNetwork)
        return PhoneNetworks(
            wifiEnabled = wifi.isWifiEnabled,
            onWifi = active?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true,
            // On Wi-Fi, mobile data is not the active network; whether it is switched on is what says it
            // will take over when the Wi-Fi pauses.
            mobileData = active?.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) == true || mobileDataSwitchedOn(),
            sideBySideWifi = Build.VERSION.SDK_INT >= Build.VERSION_CODES.S && localOnlyBesideWifi(wifi),
            hotspotOn = hotspotOn(interfaces()),
            hotspotAlongsideWifi = Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && wifi.isStaApConcurrencySupported,
            vpnOn = active?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true,
        )
    }

    /** Whether mobile data is switched on; false for a phone with no SIM, or one that will not say. */
    private fun mobileDataSwitchedOn(): Boolean = try {
        telephony.isDataEnabled
    } catch (refused: SecurityException) {
        false
    }

    companion object {
        /**
         * What Android and Samsung name the hotspot's interface. There is no public way to ask whether
         * the phone is sharing its connection, so, as Flint does, the interface being up is the sign.
         */
        private val HOTSPOT_INTERFACES = listOf("ap", "swlan", "softap")

        /** Whether [interfaces] include a hotspot's, up and handing out addresses. */
        fun hotspotOn(interfaces: List<InterfaceFacts>): Boolean = interfaces.any { facts ->
            facts.up && facts.hasIpv4 && HOTSPOT_INTERFACES.any { facts.name.startsWith(it) }
        }

        /** The phone's interfaces as they are now. */
        fun liveInterfaces(): List<InterfaceFacts> = Collections.list(
            NetworkInterface.getNetworkInterfaces(),
        ).map { found ->
            InterfaceFacts(found.name, found.isUp, Collections.list(found.inetAddresses).any { it is Inet4Address })
        }
    }
}

/** Whether [wifi]'s chip can hold a local-only network beside another. Asked only on Android 12 and newer. */
@TargetApi(Build.VERSION_CODES.S)
private fun canHoldLocalOnlyBesideWifi(
    wifi: WifiManager,
): Boolean = wifi.isStaConcurrencyForLocalOnlyConnectionsSupported
