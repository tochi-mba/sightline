package com.rextechnologies.sightline.ui.settings

import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.isDialog
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollToNode
import androidx.compose.ui.test.performTextReplacement
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.TestGraph
import com.rextechnologies.sightline.core.link.CameraNetwork
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.core.settings.GridOverlay
import com.rextechnologies.sightline.design.RexTheme
import com.rextechnologies.sightline.protocol.gpsock.MenuIds
import com.rextechnologies.sightline.ui.FakePlatform
import com.rextechnologies.sightline.ui.SightlineApp
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.time.Duration
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class SettingsScreenTest {
    @get:Rule
    val compose = createComposeRule()

    private val platform = FakePlatform()
    private val test = TestGraph().also { it.graph.settings[AppSettings.OnboardingDone] = true }

    private fun openSettings() {
        compose.setContent { RexTheme { SightlineApp(test.graph, platform) } }
        compose.onNodeWithText("SETTINGS").performClick()
        compose.waitForIdle()
    }

    private fun scrollTo(text: String) {
        compose.onNode(hasScrollAction()).performScrollToNode(hasText(text))
    }

    private fun hasScrollAction() = androidx.compose.ui.test.hasScrollAction()

    @Test
    fun `without a camera its settings wait for one`() {
        openSettings()

        compose.onNodeWithText("Connect a camera to change its own settings.").assertExists()
    }

    @Test
    fun `a camera setting is chosen from the camera's own list`() {
        test.connected()
        openSettings()

        compose.onNodeWithText("Record").assertExists()
        compose.onAllNodes(hasText("Resolution"))[0].performClick()
        val second = test.graph.controller.state.value.settings.single {
            it.menu.id == MenuIds.RECORD_RESOLUTION
        }.menu.choices[1]
        compose.onNode(hasText(second.label) and hasAnyAncestor(isDialog())).performClick()
        test.settle(Duration.ofSeconds(1))
        compose.waitForIdle()

        assertEquals(listOf(MenuIds.RECORD_RESOLUTION to second.value), test.camera.settingsWritten)
        scrollTo("Format")
        compose.onAllNodes(hasText("On the camera's own menu.")).assertCountEquals(2)
        scrollTo("WifiName")
        compose.onNodeWithText("ActionCam_000000000000").assertExists()
    }

    @Test
    fun `the camera's settings are locked while it records`() {
        test.camera.isRecording = true
        test.connected()
        openSettings()

        scrollTo("The camera's settings are locked while it records.")
    }

    @Test
    fun `switches, choices and steppers change the app's settings`() {
        openSettings()

        scrollTo("Connect when Sightline opens")
        compose.onNodeWithText("Connect when Sightline opens").performClick()
        assertFalse(test.graph.settings[AppSettings.AutoConnect])
        compose.onNodeWithContentDescription("Reconnect automatically").performClick()
        assertFalse(test.graph.settings[AppSettings.Reconnect])

        scrollTo("Framing guide")
        compose.onNodeWithText("Framing guide").performClick()
        compose.onNode(hasText("Rule of thirds") and hasAnyAncestor(isDialog())).performClick()
        assertEquals(GridOverlay.Thirds, test.graph.settings[AppSettings.Grid])

        scrollTo("Arming delay")
        compose.onAllNodes(hasContentDescription("More"))[0].performClick()
        assertEquals(15, test.graph.settings[AppSettings.SentryArmDelay])
        compose.onAllNodes(hasContentDescription("Less"))[0].performClick()
        assertEquals(10, test.graph.settings[AppSettings.SentryArmDelay])
    }

    @Test
    fun `the camera password is checked before it is saved, and cancelling keeps the old one`() {
        openSettings()
        val field = compose.onNodeWithContentDescription("Camera Wi-Fi password")

        scrollTo("Camera Wi-Fi password")
        compose.onNodeWithText("Camera Wi-Fi password").performClick()
        field.performTextReplacement("short")
        compose.onNodeWithText("8 to 63 letters, numbers or symbols.").assertExists()
        compose.onNodeWithText("SAVE").assertIsNotEnabled()
        compose.onNodeWithText("CANCEL").performClick()
        field.assertDoesNotExist()
        assertEquals(CameraNetwork.DEFAULT_PASSWORD, test.graph.settings[AppSettings.CameraPassword])

        compose.onNodeWithText("Camera Wi-Fi password").performClick()
        field.performTextReplacement("short-but-longer")
        compose.onNodeWithText("8 to 63 letters, numbers or symbols.").assertDoesNotExist()
        compose.onNodeWithText("SAVE").performClick()

        assertEquals("short-but-longer", test.graph.settings[AppSettings.CameraPassword])
        field.assertDoesNotExist()
    }

    @Test
    fun `about shows the version, the source and the introduction again`() {
        openSettings()

        scrollTo("Source code")
        compose.onNodeWithText("0.1.0").assertExists()
        compose.onNodeWithText("Source code").performClick()
        assertTrue(SOURCE_URL in platform.asked)
        compose.onNodeWithText("Show the introduction again").performClick()
        compose.waitForIdle()

        compose.onNodeWithText("See what your camera sees.").assertExists()
    }

    @Test
    fun `a choice dialog can be dismissed without changing anything`() {
        openSettings()

        scrollTo("Units")
        compose.onNodeWithText("Units").performClick()
        compose.onNodeWithText("CANCEL").performClick()

        compose.onNodeWithText("Kilometres and metres, or miles and feet.").assertExists()
    }

    @Test
    fun `a newer release is offered in about, and opens its page`() {
        val newer = TestGraph(answer = {
            "{\"html_url\":\"https://github.com/tochi-mba/sightline/releases/tag/v0.2.0\",\"tag_name\":\"v0.2.0\"}"
        }).also {
            it.graph.settings[AppSettings.OnboardingDone] =
                true
        }
        newer.graph.checkForUpdate()
        newer.settle()
        compose.setContent { RexTheme { SightlineApp(newer.graph, platform) } }
        compose.onNodeWithText("SETTINGS").performClick()

        scrollTo("Version 0.2.0 is available")
        compose.onNodeWithText("Version 0.2.0 is available").performClick()

        assertTrue("https://github.com/tochi-mba/sightline/releases/tag/v0.2.0" in platform.asked)
    }
}
