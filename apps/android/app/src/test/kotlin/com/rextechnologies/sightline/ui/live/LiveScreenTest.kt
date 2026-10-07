package com.rextechnologies.sightline.ui.live

import android.graphics.Bitmap
import android.graphics.Canvas
import androidx.activity.ComponentActivity
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.hasAnyAncestor
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.isDialog
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.TestGraph
import com.rextechnologies.sightline.core.camera.CameraState
import com.rextechnologies.sightline.core.camera.CameraStatus
import com.rextechnologies.sightline.core.camera.CaptureMode
import com.rextechnologies.sightline.core.camera.Connection
import com.rextechnologies.sightline.core.camera.LiveView
import com.rextechnologies.sightline.core.camera.Problem
import com.rextechnologies.sightline.core.camera.ProblemKind
import com.rextechnologies.sightline.core.camera.Task
import com.rextechnologies.sightline.core.link.AdviceLevel
import com.rextechnologies.sightline.core.link.LinkFailure
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.core.settings.GridOverlay
import com.rextechnologies.sightline.core.settings.HudLayout
import com.rextechnologies.sightline.core.settings.PictureFit
import com.rextechnologies.sightline.design.RexTheme
import com.rextechnologies.sightline.design.Tone
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import com.rextechnologies.sightline.protocol.gpsock.CameraMode
import com.rextechnologies.sightline.ui.FakePlatform
import com.rextechnologies.sightline.ui.SightlineApp
import org.junit.Assert.assertNotEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import java.time.Duration
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class LiveScreenTest {
    @get:Rule
    val compose = createAndroidComposeRule<ComponentActivity>()

    private val platform = FakePlatform()
    private val test = TestGraph().also { it.graph.settings[AppSettings.OnboardingDone] = true }

    private fun show() {
        compose.setContent { RexTheme { SightlineApp(test.graph, platform) } }
    }

    /**
     * Draws the whole screen into a bitmap, so what is only drawn, never laid out, runs. Compose's own
     * captureToImage waits on the frame clock for a redraw that Robolectric never makes; drawing the decor
     * view needs no clock.
     */
    private fun drawScreen() {
        val view = compose.activity.window.decorView
        view.draw(Canvas(Bitmap.createBitmap(view.width, view.height, Bitmap.Config.ARGB_8888)))
    }

    /** Lets the controller's main-thread work run, then the screen catch up with it. */
    private fun settle(duration: Duration = Duration.ZERO) {
        test.settle(duration)
        compose.waitForIdle()
    }

    @Test
    fun `before connecting it says what happens to the internet, and connecting asks first`() {
        show()

        compose.onNodeWithText("Mobile data keeps you online while the camera is connected.").assertExists()
        compose.onNodeWithText("CONNECT TO THE CAMERA").performClick()
        settle(Duration.ofSeconds(1))

        assertEquals(listOf("nearby"), platform.asked)
        assertEquals(1, platform.servicesStarted)
        compose.onNodeWithContentDescription("Start recording").assertExists()
        assertTrue(platform.screenOn)
    }

    @Test
    fun `while android is asking, it says so and the request can be given up`() {
        test.link.waits = true
        show()

        compose.onNodeWithText("CONNECT TO THE CAMERA").performClick()
        settle()
        compose.onNodeWithText("Waiting for Android").assertExists()
        compose.onNodeWithText("Android lists the cameras it can see. Tap yours, then Connect.").assertExists()
        compose.onNodeWithText("CANCEL").performClick()
        settle()

        compose.onNodeWithText("CONNECT TO THE CAMERA").assertExists()
    }

    @Test
    fun `a camera remembered by name is asked for exactly, and can be forgotten`() {
        test.graph.settings[AppSettings.CameraName] = "ActionCam_1"
        test.graph.settings[AppSettings.AutoConnect] = false
        test.link.waits = true
        show()

        compose.onNodeWithText("ActionCam_1").assertExists()
        compose.onNodeWithText("CONNECT TO THE CAMERA").performClick()
        settle()
        compose.onNodeWithText(
            "Joining ActionCam_1. Android remembers this camera, so it should not need to ask.",
        ).assertExists()
        compose.onNodeWithText("CANCEL").performClick()
        settle()
        compose.onNodeWithText("USE A DIFFERENT CAMERA").performClick()
        settle()

        compose.onNodeWithText("Your camera").assertExists()
    }

    @Test
    fun `while the camera is being read, it says so`() {
        test.camera.goesQuiet = true
        show()

        compose.onNodeWithText("CONNECT TO THE CAMERA").performClick()
        settle()

        compose.onNodeWithText("Talking to the camera").assertExists()
        compose.onNodeWithText("CANCEL").performClick()
        settle()
    }

    @Test
    fun `each failure offers what fixes it`() {
        test.link.failures += listOf(LinkFailure.WifiOff, LinkFailure.PermissionDenied, LinkFailure.Unavailable)
        show()

        compose.onNodeWithText("CONNECT TO THE CAMERA").performClick()
        settle()
        compose.onNodeWithText(ProblemKind.WifiOff.explanation).assertExists()
        compose.onNodeWithText("OPEN WI-FI SETTINGS").performClick()
        compose.onNodeWithText("CONNECT TO THE CAMERA").performClick()
        settle()
        compose.onNodeWithText("OPEN PERMISSIONS").performClick()
        compose.onNodeWithText("CONNECT TO THE CAMERA").performClick()
        settle()
        compose.onNodeWithText("Not joined (Unavailable).").assertExists()
        compose.onNodeWithText("TRY AGAIN").performClick()
        settle(Duration.ofSeconds(1))

        assertTrue("wifi settings" in platform.asked && "app settings" in platform.asked)
        compose.onNodeWithContentDescription("Start recording").assertExists()
    }

    @Test
    fun `with wifi off the advice blocks and points at the setting`() {
        test.networks = test.networks.copy(wifiEnabled = false)
        show()

        compose.onNodeWithText(
            "Wi-Fi is off. Turn it on to reach the camera; mobile data stays on either way.",
        ).assertExists()
        compose.onNodeWithText("OPEN WI-FI SETTINGS").performClick()

        assertEquals(listOf("wifi settings"), platform.asked)
    }

    @Test
    fun `the shutter records, and leaving during a recording asks first`() {
        test.connected()
        show()
        settle()

        compose.onNodeWithContentDescription("Start recording").performClick()
        settle(Duration.ofSeconds(1))
        compose.onNodeWithContentDescription("Stop recording").assertExists()
        compose.onNodeWithText("Recording to the camera's card", substring = true).assertExists()
        // The clip's timer counts each second between the camera's reports.
        val clip = compose.onNodeWithText("REC", substring = true)
        val before = clip.fetchSemanticsNode().config[SemanticsProperties.Text].single().text
        compose.mainClock.advanceTimeBy(1_100)
        assertNotEquals(before, clip.fetchSemanticsNode().config[SemanticsProperties.Text].single().text)
        compose.onNodeWithText("LEAVE").performClick()
        compose.onNodeWithText("Stop recording and leave?").assertExists()
        compose.onNodeWithText("CANCEL").performClick()
        compose.onNodeWithText("Stop recording and leave?").assertDoesNotExist()
        compose.onNodeWithText("LEAVE").performClick()
        compose.onNode(hasText("LEAVE") and hasAnyAncestor(isDialog())).performClick()
        settle()

        assertEquals(Connection.Idle, test.graph.controller.state.value.connection)
        assertFalse(test.camera.isRecording)
    }

    @Test
    fun `photo mode takes photos, and leaving when not recording just leaves`() {
        test.connected()
        show()
        settle()

        compose.onNodeWithText("PHOTO").performClick()
        settle(Duration.ofSeconds(1))
        compose.onNodeWithContentDescription("Take a photo").performClick()
        settle(Duration.ofSeconds(1))
        compose.onNodeWithText("LEAVE").performClick()
        settle()

        assertEquals(1, test.camera.picturesTaken)
        assertEquals(CameraMode.Capture, test.camera.mode)
        assertEquals(Connection.Idle, test.graph.controller.state.value.connection)
    }

    @Test
    fun `the hud opens once location is allowed, in each layout, and closes`() {
        test.connected()
        show()
        settle()

        platform.grants = false
        compose.onNodeWithText("HUD").performClick()
        compose.onNodeWithText("Waiting for GPS").assertDoesNotExist()
        platform.grants = true
        compose.onNodeWithText("HUD").performClick()
        compose.onNodeWithText("Waiting for GPS").assertExists()
        compose.onNodeWithText("HEADING").assertExists()
        test.graph.settings[AppSettings.Layout] = HudLayout.Cockpit
        settle()
        compose.onNodeWithText("TOP SPEED").assertExists()
        test.graph.settings[AppSettings.Layout] = HudLayout.Minimal
        test.graph.settings[AppSettings.HudMirror] = true
        settle()
        compose.onNodeWithText("HEADING").assertDoesNotExist()
        compose.onNodeWithText("HIDE HUD").performClick()

        compose.onNodeWithText("Waiting for GPS").assertDoesNotExist()
    }

    @Test
    fun `the picture's settings are drawn, and a lost camera says it is reconnecting`() {
        test.link.stream = {
            frames += List(40) { TestGraph.picture() }
            pace = kotlin.time.Duration.parse("100ms")
        }
        with(test.graph.settings) {
            set(AppSettings.Grid, GridOverlay.Thirds)
            set(AppSettings.Fit, PictureFit.Fill)
            set(AppSettings.Flip, true)
            set(AppSettings.Mirror, true)
            set(AppSettings.ShowStreamStats, true)
        }
        test.connected()
        show()
        settle(Duration.ofSeconds(3))

        compose.onNodeWithText("FPS", substring = true).assertExists()
        // Drawn, not just laid out: the picture turned, mirrored and filling, under the thirds.
        drawScreen()
        test.graph.settings[AppSettings.Grid] = GridOverlay.Centre
        test.graph.settings[AppSettings.KeepScreenOn] = false
        settle()
        drawScreen()
        assertFalse(platform.screenOn)
        test.link.lose()
        test.until { it.connection is Connection.Reconnecting }
        compose.waitForIdle()

        compose.onNodeWithText("RECONNECTING TO THE CAMERA, ATTEMPT 1 OF 5").assertExists()
    }

    @Test
    @Config(qualifiers = "w853dp-h384dp")
    fun `a phone on its side gives the picture the height, and the hud keeps to its edges`() {
        // The reference phone turned on its side: wide, but too short for the controls under the picture. The
        // camera refuses every stream, so the picture stays stopped between its tries.
        test.link.stream = { setupStatus = 454 }
        test.connected()
        show()
        settle()
        compose.onNodeWithContentDescription("Start recording").assertExists()
        compose.onNodeWithText("LEAVE").assertExists()

        platform.grants = true
        compose.onNodeWithText("HUD").performClick()
        test.graph.settings[AppSettings.Layout] = HudLayout.Cockpit
        settle()
        compose.onNodeWithText("Waiting for GPS").assertExists()
        compose.onNodeWithText("TOP SPEED").assertExists()

        // A picture that has stopped still says why with the HUD on.
        test.until { it.live is LiveView.Interrupted }
        compose.waitForIdle()
        compose.onNodeWithText("The picture stopped. Starting it again.", ignoreCase = true).assertExists()
    }

    @Test
    @Config(qualifiers = "w900dp-h500dp")
    fun `a wide screen puts the controls beside the picture`() {
        test.connected()
        show()
        settle()

        compose.onNodeWithContentDescription("Start recording").assertExists()
    }

    @Test
    fun `the words under the picture say what the card holds and what the camera is doing`() {
        val status = CameraStatus(CameraMode.Record, false, false, null, Duration.ofSeconds(3725), 1234)
        val state = CameraState(connection = Connection.Connected, status = status)

        assertEquals("Room for 1:02:05 more video", statusLine(state))
        assertEquals("Room for 1234 more photos", statusLine(state.copy(mode = CaptureMode.Photo)))
        assertEquals("", statusLine(state.copy(status = status.copy(recordTimeLeft = null))))
        assertEquals(
            "Recording to the camera's card  ·  On USB power",
            statusLine(state.copy(status = status.copy(isRecording = true, onExternalPower = true))),
        )
        assertEquals("", statusLine(CameraState()))
        val tasks = Task.entries.map { statusLine(state.copy(task = it)) }
        assertEquals(Task.entries.size, tasks.toSet().size)
        assertEquals("5:09", clock(Duration.ofSeconds(309)))
    }

    @Test
    fun `the picture says why it is not moving`() {
        val connected = CameraState(connection = Connection.Connected)

        assertEquals("Starting the picture", pictureMessage(connected.copy(live = LiveView.Starting)))
        assertEquals("Paused while the card is read", pictureMessage(connected.copy(live = LiveView.Paused)))
        assertEquals(
            "The picture stopped. Starting it again.",
            pictureMessage(connected.copy(live = LiveView.Interrupted("x"))),
        )
        assertEquals(null, pictureMessage(connected.copy(live = LiveView.Playing(12.0))))
        assertEquals(
            "Reconnecting to the camera, attempt 2 of 4",
            pictureMessage(connected.copy(connection = Connection.Reconnecting(2, 4, Problem(ProblemKind.Lost, "x")))),
        )
    }

    @Test
    fun `advice and connections each have their framing and their word`() {
        assertEquals(Tone.Line, toneOf(AdviceLevel.Fine))
        assertEquals(Tone.Neutral, toneOf(AdviceLevel.Note))
        assertEquals(Tone.Live, toneOf(AdviceLevel.Warning))
        assertEquals(Tone.Live, toneOf(AdviceLevel.Blocked))
        assertEquals(null, connectionWord(Connection.Idle))
        assertEquals("Connecting", connectionWord(Connection.Opening))
        assertEquals("Connected", connectionWord(Connection.Connected))
        assertEquals("Reconnecting", connectionWord(Connection.Reconnecting(1, 5, Problem(ProblemKind.Lost, "x"))))
        assertEquals("Not connected", connectionWord(Connection.Failed(Problem(ProblemKind.Lost, "x"))))
        assertEquals("Start recording", shutterLabel(CameraState()))
    }
}
