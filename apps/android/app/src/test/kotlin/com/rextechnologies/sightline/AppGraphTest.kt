package com.rextechnologies.sightline

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.playback.PlayerPhase
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.protocol.FakeClip
import com.rextechnologies.sightline.protocol.gpsock.CameraFile
import org.junit.Test
import org.junit.runner.RunWith
import java.time.Duration
import java.time.LocalDateTime
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNotSame
import kotlin.test.assertNull
import kotlin.test.assertSame

@RunWith(AndroidJUnit4::class)
class AppGraphTest {
    /** Four of the phone's own pictures at four a second, with half a second of sound after every two. */
    private fun clip() = FakeClip.reference(List(4) { TestGraph.picture() }, List(2) { ByteArray(16_000) })
        .copy(microsPerFrame = 250_000, rate = 4, picturesPerSound = 2)
        .build()

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
    fun `a clip from the card plays, pauses out of sight, and stops when closed`() {
        val test = TestGraph()
        test.camera.addFile('A', LocalDateTime.of(2026, 10, 4, 18, 35, 56), clip())
        test.connected()
        test.graph.controller.refreshLibrary()
        test.until { it.library.files != null && it.task == null }
        val video = test.graph.controller.state.value.library.files!!.single()

        test.graph.play(video)
        val first = assertNotNull(test.graph.playing.value)
        repeat(50) { if (first.view.value.picture == null) test.settle(Duration.ofMillis(100)) }
        assertNotNull(first.view.value.picture)
        test.until { it.task == null }

        // Played again while it plays, from the phone this time: the first is closed for the second.
        test.graph.play(video)
        val second = assertNotNull(test.graph.playing.value)
        assertNotSame(first, second)
        repeat(50) { if (second.view.value.phase != PlayerPhase.Playing) test.settle(Duration.ofMillis(100)) }
        test.graph.pausePlaying()
        assertEquals(PlayerPhase.Paused, second.view.value.phase)

        test.graph.stopPlaying()
        assertNull(test.graph.playing.value)
    }

    @Test
    fun `with no camera a clip never played before does not play, and there is nothing to pause or stop`() {
        val test = TestGraph()

        test.graph.play(CameraFile('A', 1, null, 40))
        test.graph.pausePlaying()
        test.graph.stopPlaying()

        assertNull(test.graph.playing.value)
        assertEquals("Connect to the camera first.", test.graph.controller.state.value.notice?.text)
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
