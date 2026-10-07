package com.rextechnologies.sightline.link

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkAddress
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSpecifier
import android.os.PatternMatcher
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.core.link.CameraLinkException
import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.link.LinkFailure
import com.rextechnologies.sightline.protocol.TcpCameraTransport
import com.rextechnologies.sightline.protocol.UdpCameraDatagrams
import kotlinx.coroutines.async
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Shadows.shadowOf
import org.robolectric.shadows.ShadowNetwork
import org.robolectric.util.ReflectionHelpers
import org.robolectric.util.ReflectionHelpers.ClassParameter
import java.io.IOException
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketException
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertSame
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class AndroidCameraLinkTest {
    /** The system's side, played by each test. */
    private class FakeSystem : NetworkSystem {
        override var wifiEnabled = true
        var refuse: SecurityException? = null
        var releaseFails = false
        val requests = mutableListOf<Pair<NetworkRequest, Int>>()
        val released = mutableListOf<ConnectivityManager.NetworkCallback>()
        lateinit var callback: ConnectivityManager.NetworkCallback

        override fun request(
            request: NetworkRequest,
            callback: ConnectivityManager.NetworkCallback,
            timeoutMillis: Int,
        ) {
            refuse?.let { throw it }
            requests += request to timeoutMillis
            this.callback = callback
        }

        override fun release(callback: ConnectivityManager.NetworkCallback) {
            released += callback
            if (releaseFails) throw IllegalArgumentException("NetworkCallback was not registered")
        }

        val socketsFor = mutableListOf<Network>()

        override fun datagramSocket(network: Network): DatagramSocket {
            socketsFor += network
            return DatagramSocket(InetSocketAddress(InetAddress.getByName("127.0.0.1"), 0))
        }
    }

    private val system = FakeSystem()
    private val link = AndroidCameraLink(system)
    private val network: Network = ShadowNetwork.newInstance(7)

    @Test
    fun `a camera network the system joins is handed over as a lease`() = runTest {
        val joining = async { link.join(CameraNetwork()) }
        runCurrent()

        system.callback.onAvailable(network)
        val lease = joining.await()

        assertEquals(AndroidCameraLink.JOIN_TIMEOUT_MILLIS, system.requests.single().second)
        assertIs<TcpCameraTransport>(lease.transport(8081))
        assertTrue(system.released.isEmpty())
        lease.close()
        lease.close()
        assertEquals(1, system.released.size)
    }

    @Test
    fun `the lease's datagrams travel over the camera's network`() = runTest {
        val joining = async { link.join(CameraNetwork()) }
        runCurrent()
        system.callback.onAvailable(network)
        val lease = joining.await()

        lease.datagrams().use { datagrams ->
            assertIs<UdpCameraDatagrams>(datagrams)
            assertTrue(datagrams.port > 0)
        }

        assertEquals(listOf(network), system.socketsFor)
        lease.close()
    }

    @Test
    fun `the real system binds a datagram socket to the camera's network and the phone's address there`() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val connectivity = context.getSystemService(ConnectivityManager::class.java)
        shadowOf(connectivity).setLinkProperties(network, linkProperties("::1", "127.0.0.1"))

        AndroidNetworkSystem(context).datagramSocket(network).use { socket ->
            assertEquals(InetAddress.getByName("127.0.0.1"), socket.localAddress)
            assertTrue(socket.localPort > 0)
            assertTrue(shadowOf(network).isSocketBound(socket))
        }
    }

    @Test
    fun `the real system gives no datagram socket before the phone has an address on the camera's network`() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val connectivity = context.getSystemService(ConnectivityManager::class.java)
        val real = AndroidNetworkSystem(context)

        val unknown = assertFailsWith<IOException> { real.datagramSocket(network) }
        shadowOf(connectivity).setLinkProperties(network, linkProperties("::1"))
        assertFailsWith<IOException> { real.datagramSocket(network) }

        assertEquals("The phone has no address on the camera's network yet.", unknown.message)
    }

    @Test
    fun `a datagram socket that cannot be bound is closed and the reason passed on`() {
        // 192.0.2.1 is reserved for documentation, so no machine has it to bind to.
        val context = ApplicationProvider.getApplicationContext<Context>()
        val connectivity = context.getSystemService(ConnectivityManager::class.java)
        shadowOf(connectivity).setLinkProperties(network, linkProperties("192.0.2.1"))

        assertFailsWith<SocketException> { AndroidNetworkSystem(context).datagramSocket(network) }
    }

    /** Link properties with [addresses], made the only way an app's tests can: these constructors are hidden. */
    private fun linkProperties(vararg addresses: String): LinkProperties {
        val properties = LinkProperties()
        for (address in addresses) {
            val inet = InetAddress.getByName(address)
            val link = ReflectionHelpers.callConstructor(
                LinkAddress::class.java,
                ClassParameter.from(InetAddress::class.java, inet),
                ClassParameter.from(Int::class.java, if (inet.address.size == 4) 24 else 128),
            )
            ReflectionHelpers.callInstanceMethod<Boolean>(
                properties,
                "addLinkAddress",
                ClassParameter.from(LinkAddress::class.java, link),
            )
        }

        return properties
    }

    @Test
    fun `the lease says when the system loses the camera's network`() = runTest {
        val joining = async { link.join(CameraNetwork()) }
        runCurrent()
        system.callback.onAvailable(network)
        val lease = joining.await()

        val lost = async { lease.awaitLoss() }
        runCurrent()
        assertFalse(lost.isCompleted)
        system.callback.onLost(network)
        lost.await()
        // Asked again once lost, it answers at once.
        lease.awaitLoss()
    }

    @Test
    fun `a network the system could not find or the person refused is reported and withdrawn`() = runTest {
        val joining = async { runCatching { link.join(CameraNetwork()) } }
        runCurrent()

        system.callback.onUnavailable()
        val failure = joining.await().exceptionOrNull()

        assertIs<CameraLinkException>(failure)
        assertEquals(LinkFailure.Unavailable, failure.failure)
        assertEquals(listOf(system.callback), system.released)
    }

    @Test
    fun `giving up on a join withdraws the request, so the phone never joins after the person moved on`() = runTest {
        val joining = async { link.join(CameraNetwork()) }
        runCurrent()

        joining.cancel()
        runCurrent()

        assertEquals(listOf(system.callback), system.released)
    }

    @Test
    fun `a request the system already dropped is released without complaint`() = runTest {
        system.releaseFails = true
        val joining = async { runCatching { link.join(CameraNetwork()) } }
        runCurrent()

        system.callback.onUnavailable()

        assertIs<CameraLinkException>(joining.await().exceptionOrNull())
    }

    @Test
    fun `wifi switched off is reported before anything is asked`() = runTest {
        system.wifiEnabled = false

        val failure = assertFailsWith<CameraLinkException> { link.join(CameraNetwork()) }

        assertEquals(LinkFailure.WifiOff, failure.failure)
        assertTrue(system.requests.isEmpty())
    }

    @Test
    fun `a permission the request needs is reported as missing`() = runTest {
        system.refuse = SecurityException("NEARBY_WIFI_DEVICES")

        val failure = assertFailsWith<CameraLinkException> { link.join(CameraNetwork()) }

        assertEquals(LinkFailure.PermissionDenied, failure.failure)
        assertEquals("NEARBY_WIFI_DEVICES", failure.message)
        system.refuse = SecurityException()
        assertEquals(
            "A permission is missing.",
            assertFailsWith<CameraLinkException> {
                link.join(CameraNetwork())
            }.message,
        )
    }

    @Test
    fun `the request is local-only wifi, to any camera of the family or exactly the one named`() {
        val any = AndroidCameraLink.requestFor(CameraNetwork())
        val exact = AndroidCameraLink.requestFor(CameraNetwork(name = "ActionCam_1", password = "secret-pass"))

        for (request in listOf(any, exact)) {
            assertTrue(request.hasTransport(NetworkCapabilities.TRANSPORT_WIFI))
            assertFalse(request.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET))
        }
        assertEquals(
            WifiNetworkSpecifier.Builder()
                .setSsidPattern(PatternMatcher("ActionCam_", PatternMatcher.PATTERN_PREFIX))
                .setWpa2Passphrase("12345678")
                .build(),
            any.networkSpecifier,
        )
        assertEquals(
            WifiNetworkSpecifier.Builder().setSsid("ActionCam_1").setWpa2Passphrase("secret-pass").build(),
            exact.networkSpecifier,
        )
    }

    @Test
    fun `the real system asks the phone's network service`() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val real = AndroidNetworkSystem(context)
        val wifi = context.getSystemService(WifiManager::class.java)
        val connectivity = context.getSystemService(ConnectivityManager::class.java)
        val callback = object : ConnectivityManager.NetworkCallback() {}

        switchWifi(wifi, false)
        assertFalse(real.wifiEnabled)
        switchWifi(wifi, true)
        assertTrue(real.wifiEnabled)
        real.request(AndroidCameraLink.requestFor(CameraNetwork()), callback, 1_000)
        assertSame(callback, shadowOf(connectivity).networkCallbacks.single())
        real.release(callback)
        assertTrue(shadowOf(connectivity).networkCallbacks.isEmpty())
    }

    /** Switches the phone's Wi-Fi, as the person would; apps may no longer, which is why it is deprecated. */
    @Suppress("DEPRECATION")
    private fun switchWifi(wifi: WifiManager, on: Boolean) {
        wifi.isWifiEnabled = on
    }
}
