package com.rextechnologies.sightline.ui

import androidx.activity.ComponentActivity
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.TestGraph
import com.rextechnologies.sightline.core.navigation.Destination
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.core.updates.WhatsNewEntry
import com.rextechnologies.sightline.design.RexTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import kotlin.test.assertEquals
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class SightlineAppTest {
    @get:Rule
    val compose = createAndroidComposeRule<ComponentActivity>()

    private val platform = FakePlatform()

    private fun show(test: TestGraph) {
        compose.setContent { RexTheme { SightlineApp(test.graph, platform) } }
    }

    /** The navigation's own entry for [destination], not a heading that shares its name. */
    private fun tab(destination: Destination) =
        hasText(labelFor(destination).uppercase()) and SemanticsMatcher.expectValue(SemanticsProperties.Role, Role.Tab)

    @Test
    fun `a first run walks through the introduction, back included, and then opens live`() {
        val test = TestGraph()
        show(test)

        compose.onNodeWithText("See what your camera sees.").assertExists()
        compose.onNodeWithText("NEXT").performClick()
        compose.onNodeWithText("Your internet stays on.").assertExists()
        compose.onNodeWithText("BACK").performClick()
        compose.onNodeWithText("See what your camera sees.").assertExists()
        repeat(3) { compose.onNodeWithText("NEXT").performClick() }
        compose.onNodeWithText("Wake the camera's Wi-Fi.").assertExists()
        compose.onNodeWithText("START").performClick()

        compose.onNodeWithText("CONNECT TO THE CAMERA").assertExists()
        assertTrue(test.graph.settings[AppSettings.OnboardingDone])
        assertEquals("0.1.0", test.graph.settings[AppSettings.LastVersion])
    }

    @Test
    fun `the introduction can be skipped`() {
        val test = TestGraph()
        show(test)

        compose.onNodeWithText("SKIP THE INTRODUCTION").performClick()

        compose.onNodeWithText("CONNECT TO THE CAMERA").assertExists()
    }

    @Test
    fun `an update that earned a word says what is new, once`() {
        val test =
            TestGraph(
                version = "0.3.0",
                releaseNotes = listOf(WhatsNewEntry("0.2.0", listOf("Sentry records on the camera."))),
            )
        test.graph.settings[AppSettings.OnboardingDone] = true
        test.graph.settings[AppSettings.LastVersion] = "0.1.0"
        show(test)

        compose.onNodeWithText("What's new").assertExists()
        compose.onNodeWithText("• Sentry records on the camera.").assertExists()
        compose.onNodeWithText("CONTINUE").performClick()

        compose.onNodeWithText("CONNECT TO THE CAMERA").assertExists()
        assertEquals("0.3.0", test.graph.settings[AppSettings.LastVersion])
    }

    @Test
    fun `an update with nothing to say is recorded quietly`() {
        val test = TestGraph(version = "0.1.1")
        test.graph.settings[AppSettings.OnboardingDone] = true
        test.graph.settings[AppSettings.LastVersion] = "0.1.0"
        show(test)
        compose.waitForIdle()

        assertEquals("0.1.1", test.graph.settings[AppSettings.LastVersion])
    }

    @Test
    fun `the bar moves between the four places, and back returns to live`() {
        val test = TestGraph()
        test.graph.settings[AppSettings.OnboardingDone] = true
        show(test)

        for (destination in listOf(Destination.Library, Destination.Sentry, Destination.Settings)) {
            compose.onNode(tab(destination)).performClick()
            compose.onNode(tab(destination)).assertIsSelected()
        }
        compose.runOnUiThread { compose.activity.onBackPressedDispatcher.onBackPressed() }
        compose.onNode(tab(Destination.Live)).assertIsSelected()
    }

    @Test
    @Config(qualifiers = "w900dp-h500dp")
    fun `a wide screen has a rail down the side`() {
        val test = TestGraph()
        test.graph.settings[AppSettings.OnboardingDone] = true
        show(test)

        compose.onNodeWithText("CARD").performClick()

        compose.onNodeWithText("No camera connected").assertExists()
    }

    @Test
    fun `a camera used before is joined again as the app opens, when wanted`() {
        val test = TestGraph()
        test.graph.settings[AppSettings.OnboardingDone] = true
        test.graph.settings[AppSettings.CameraName] = "ActionCam_000000000000"
        show(test)
        compose.waitForIdle()

        assertEquals(listOf("nearby"), platform.asked)
        assertEquals(1, platform.servicesStarted)
    }

    @Test
    fun `nothing is joined at open when the person said not to, or no camera is remembered`() {
        val test = TestGraph()
        test.graph.settings[AppSettings.OnboardingDone] = true
        test.graph.settings[AppSettings.CameraName] = "ActionCam_000000000000"
        test.graph.settings[AppSettings.AutoConnect] = false
        show(test)
        compose.waitForIdle()

        assertEquals(emptyList(), platform.asked)
    }
}
