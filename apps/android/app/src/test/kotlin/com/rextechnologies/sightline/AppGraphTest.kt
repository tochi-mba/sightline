package com.rextechnologies.sightline

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.settings.AppSettings
import org.junit.Test
import org.junit.runner.RunWith
import kotlin.test.assertEquals
import kotlin.test.assertSame

@RunWith(AndroidJUnit4::class)
class AppGraphTest {
    @Test
    fun `the camera asked for is any of the family until one has said its name, then exactly that one`() {
        val test = TestGraph()
        assertEquals(CameraNetwork(), test.graph.cameraNetwork())

        test.connected()
        test.settle()

        assertEquals("ActionCam_000000000000", test.graph.settings[AppSettings.CameraName])
        assertEquals(CameraNetwork(name = "ActionCam_000000000000"), test.graph.cameraNetwork())
    }

    @Test
    fun `a password saved in settings is the one used, and resetting it goes back to the default`() {
        val test = TestGraph()

        test.graph.settings[AppSettings.CameraPassword] = "my-camera-pass"
        assertEquals("my-camera-pass", test.graph.cameraNetwork().password)
        test.graph.settings.reset(AppSettings.CameraPassword)
        assertEquals(CameraNetwork.DEFAULT_PASSWORD, test.graph.cameraNetwork().password)
    }

    @Test
    fun `the phone's networks are read when asked`() {
        val test = TestGraph()

        assertSame(test.networks, test.graph.phoneNetworks())
    }

    @Test
    fun `the real graph wires itself from the phone`() {
        val graph = AppGraph(ApplicationProvider.getApplicationContext<Context>())

        assertEquals(CameraNetwork(), graph.cameraNetwork())
        graph.phoneNetworks()
    }

    @Test
    fun `the update check finds a newer release, and says nothing when it cannot or may not look`() {
        val test =
            TestGraph(answer = {
                "{\"html_url\":\"https://github.com/tochi-mba/sightline/releases/tag/v0.2.0\",\"tag_name\":\"v0.2.0\"}"
            })
        test.graph.checkForUpdate()
        test.settle()
        assertEquals("0.2.0", test.graph.update.value?.version)

        val offline = TestGraph()
        offline.graph.checkForUpdate()
        offline.settle()
        assertEquals(null, offline.graph.update.value)

        val asked = mutableListOf<String>()
        val refused =
            TestGraph(answer = {
                asked += "asked"
                "{\"html_url\":\"https://github.com/tochi-mba/sightline/releases/tag/v0.2.0\",\"tag_name\":\"v0.2.0\"}"
            })
        refused.graph.settings[AppSettings.CheckForUpdates] = false
        refused.graph.checkForUpdate()
        refused.settle()
        assertEquals(emptyList(), asked)
    }

    @Test
    fun `with reconnecting switched off a lost camera is not tried again`() {
        val test = TestGraph().connected()
        test.graph.settings[AppSettings.Reconnect] = false

        test.link.lose()
        test.settle()

        kotlin.test.assertTrue(
            test.graph.controller.state.value.connection is com.rextechnologies.sightline.core.camera.Connection.Failed,
        )
    }

    @Test
    fun `a web address is fetched as text`() {
        val server = com.sun.net.httpserver.HttpServer.create(
            java.net.InetSocketAddress(java.net.InetAddress.getLoopbackAddress(), 0),
            0,
        )
        server.createContext("/latest") { exchange ->
            val body = "{\"tag_name\":\"v1.0.0\"}".toByteArray()
            exchange.sendResponseHeaders(200, body.size.toLong())
            exchange.responseBody.use { it.write(body) }
        }
        server.start()
        try {
            assertEquals("{\"tag_name\":\"v1.0.0\"}", httpGet("http://127.0.0.1:${server.address.port}/latest"))
        } finally {
            server.stop(0)
        }
    }
}
