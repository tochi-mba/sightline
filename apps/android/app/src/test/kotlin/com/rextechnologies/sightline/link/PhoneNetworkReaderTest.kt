package com.rextechnologies.sightline.link

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.telephony.TelephonyManager
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowNetworkCapabilities
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class PhoneNetworkReaderTest {
    private val context = ApplicationProvider.getApplicationContext<Context>()
    private val connectivity = context.getSystemService(ConnectivityManager::class.java)
    private val wifi = context.getSystemService(WifiManager::class.java)
    private val telephony = context.getSystemService(TelephonyManager::class.java)

    /** Makes the active network one carried over [transports]. */
    private fun activeOver(vararg transports: Int) {
        val capabilities = ShadowNetworkCapabilities.newInstance()
        transports.forEach { shadowOf(capabilities).addTransportType(it) }
        shadowOf(connectivity).setNetworkCapabilities(connectivity.activeNetwork, capabilities)
    }

    private fun reader(vararg interfaces: InterfaceFacts) = PhoneNetworkReader(
        context,
        interfaces = { interfaces.toList() },
    )

    @Test
    fun `on mobile data alone the phone is online without wifi`() {
        activeOver(NetworkCapabilities.TRANSPORT_CELLULAR)
        switchWifi(wifi, true)

        val phone = reader().read()

        assertTrue(phone.wifiEnabled)
        assertFalse(phone.onWifi)
        assertTrue(phone.mobileData)
        assertFalse(phone.vpnOn)
        assertFalse(phone.hotspotOn)
    }

    @Test
    fun `on wifi, mobile data switched on is what takes over`() {
        activeOver(NetworkCapabilities.TRANSPORT_WIFI, NetworkCapabilities.TRANSPORT_VPN)
        shadowOf(telephony).setDataEnabled(true)

        val phone = reader().read()

        assertTrue(phone.onWifi)
        assertTrue(phone.mobileData)
        assertTrue(phone.vpnOn)
        shadowOf(telephony).setDataEnabled(false)
        assertFalse(reader().read().mobileData)
    }

    @Test
    fun `a chip that can hold the camera beside other wifi, or beside a hotspot, says so`() {
        activeOver(NetworkCapabilities.TRANSPORT_WIFI)
        shadowOf(wifi).setStaApConcurrencySupported(true)

        val phone = PhoneNetworkReader(context, { emptyList() }, { true }).read()

        assertTrue(phone.sideBySideWifi)
        assertTrue(phone.hotspotAlongsideWifi)
    }

    @Test
    fun `a phone that will not say whether mobile data is on is taken as off`() {
        activeOver(NetworkCapabilities.TRANSPORT_WIFI)
        shadowOf(telephony).setDataEnabled(true)
        shadowOf(telephony).setReadPhoneStatePermission(false)

        assertFalse(reader().read().mobileData)
    }

    @Test
    fun `with no active network nothing is assumed`() {
        shadowOf(connectivity).setActiveNetworkInfo(null)
        shadowOf(connectivity).setDefaultNetworkActive(false)
        shadowOf(connectivity).setNetworkCapabilities(connectivity.activeNetwork, null)

        val phone = reader().read()

        assertFalse(phone.onWifi)
        assertFalse(phone.vpnOn)
    }

    @Test
    @Config(sdk = [29])
    fun `before android 11 neither concurrency is claimed`() {
        activeOver(NetworkCapabilities.TRANSPORT_WIFI)

        val phone = reader().read()

        assertFalse(phone.sideBySideWifi)
        assertFalse(phone.hotspotAlongsideWifi)
    }

    @Test
    fun `a hotspot is an interface named for one, up and handing out addresses`() {
        assertTrue(PhoneNetworkReader.hotspotOn(listOf(InterfaceFacts("swlan0", up = true, hasIpv4 = true))))
        assertTrue(PhoneNetworkReader.hotspotOn(listOf(InterfaceFacts("ap0", up = true, hasIpv4 = true))))
        assertFalse(PhoneNetworkReader.hotspotOn(listOf(InterfaceFacts("swlan0", up = false, hasIpv4 = true))))
        assertFalse(PhoneNetworkReader.hotspotOn(listOf(InterfaceFacts("swlan0", up = true, hasIpv4 = false))))
        assertFalse(PhoneNetworkReader.hotspotOn(listOf(InterfaceFacts("wlan0", up = true, hasIpv4 = true))))
        assertTrue(reader(InterfaceFacts("softap0", up = true, hasIpv4 = true)).read().hotspotOn)
    }

    @Test
    fun `the phone's own interfaces can be read`() {
        val found = PhoneNetworkReader.liveInterfaces()

        assertTrue(found.isNotEmpty())
        assertEquals(found.size, found.map { it.name }.toSet().size)
        PhoneNetworkReader(context).read()
    }

    /** Switches the phone's Wi-Fi, as the person would; apps may no longer, which is why it is deprecated. */
    @Suppress("DEPRECATION")
    private fun switchWifi(wifi: WifiManager, on: Boolean) {
        wifi.isWifiEnabled = on
    }
}
