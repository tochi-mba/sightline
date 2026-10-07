package com.rextechnologies.sightline.ui.library

import androidx.activity.ComponentActivity
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.TestGraph
import com.rextechnologies.sightline.core.playback.ClipView
import com.rextechnologies.sightline.core.playback.PlayerPhase
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.design.RexTheme
import com.rextechnologies.sightline.protocol.FakeClip
import com.rextechnologies.sightline.protocol.FakeRtspCamera
import com.rextechnologies.sightline.ui.FakePlatform
import com.rextechnologies.sightline.ui.SightlineApp
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.time.Duration
import java.time.LocalDateTime
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TimeMark
import kotlin.time.TimeSource

/** A video from the card, playing over the card's files, as a person drives it. */
@RunWith(AndroidJUnit4::class)
class PlayerScreenTest {
    @get:Rule
    val compose = createAndroidComposeRule<ComponentActivity>()

    // A clock that stands still, so a clip that is playing stays where it is: the test clock moves on by itself
    // while anything on screen keeps changing, and a moving clip would be played to its end at the first wait.
    private val test = TestGraph(timeSource = Stopped).also { it.graph.settings[AppSettings.OnboardingDone] = true }
    private val taken = LocalDateTime.of(2026, 10, 4, 18, 35, 56)

    /**
     * Forty of the phone's own pictures at four a second, with half a second of sound after every two: ten seconds,
     * long enough to still be playing when the test reaches for its controls.
     */
    private val clip = FakeClip.reference(List(40) { TestGraph.picture() }, List(20) { ByteArray(16_000) })
        .copy(microsPerFrame = 250_000, rate = 4, picturesPerSound = 2)
        .build()

    private fun settle() {
        test.settle(Duration.ofMillis(100))
        compose.waitForIdle()
    }

    /** Runs the main thread until [condition] holds: the clip comes over sockets on real threads. */
    private fun until(condition: () -> Boolean) {
        repeat(100) { if (!condition()) settle() }
        check(condition()) { "Never happened." }
    }

    /** The card open on a camera holding a photo and the clip, with the clip picked and played. */
    private fun playing() {
        test.camera.thumbnailOf = { TestGraph.picture() }
        test.camera.addFile('J', taken, FakeRtspCamera.jpeg(3000))
        test.camera.addFile('A', taken.plusDays(1), clip)
        // As the real camera sends a file: in frames of about 60 KB.
        test.camera.downloadChunk = 60_000
        test.connected()
        test.camera.isStreaming = false
        compose.setContent { RexTheme { SightlineApp(test.graph, FakePlatform()) } }
        compose.onNodeWithText("CARD").performClick()
        until { test.graph.controller.state.value.let { it.library.files != null && it.task == null } }

        // A photo has nothing to play; a video picked alone does.
        compose.onNodeWithContentDescription("Photo, 4 October 2026, 18:35, 3 KB").performClick()
        compose.onNodeWithText("PLAY").assertDoesNotExist()
        compose.onNodeWithContentDescription("Photo, 4 October 2026, 18:35, 3 KB").performClick()
        compose.onNode(hasContentDescription("Video,", substring = true)).performClick()
        compose.onNodeWithText("PLAY").performClick()
        until { test.graph.playing.value?.view?.value?.picture != null }
    }

    @Test
    fun `a video picked alone plays over the card and closes back to it`() {
        playing()

        compose.onNodeWithText("MOVI0002").assertExists()
        compose.onNodeWithText("1920×1080 · 4 fps").assertExists()
        compose.onNodeWithContentDescription("The clip").assertExists()
        compose.onNodeWithText("PAUSE").performClick()
        settle()
        compose.onNodeWithText("PLAY").assertExists()
        assertEquals(PlayerPhase.Paused, test.graph.playing.value?.view?.value?.phase)

        compose.onNodeWithContentDescription("Where the clip is").assertExists()
        compose.onNodeWithText("CLOSE").performClick()
        settle()

        assertNull(test.graph.playing.value)
        compose.onNodeWithText("1 video, 1 photo").assertExists()
    }

    @Test
    fun `going back or to another place ends the clip`() {
        playing()

        compose.runOnUiThread { compose.activity.onBackPressedDispatcher.onBackPressed() }
        settle()
        assertNull(test.graph.playing.value)

        compose.onNode(hasContentDescription("Video,", substring = true)).performClick()
        compose.onNodeWithText("PLAY").performClick()
        until { test.graph.playing.value != null }
        compose.onNodeWithText("LIVE").performClick()
        settle()

        assertNull(test.graph.playing.value)
    }

    /** A clock that never moves. */
    private object Stopped : TimeSource {
        override fun markNow(): TimeMark = object : TimeMark {
            override fun elapsedNow(): kotlin.time.Duration = kotlin.time.Duration.ZERO
        }
    }

    @Test
    fun `the player says what the clip is and why it waits`() {
        assertNull(formatWords(ClipView<Any>()))
        assertEquals(
            "1920×1080 · 29.97 fps",
            formatWords(
                ClipView<Any>(
                    width = 1920,
                    height = 1080,
                    framesPerSecond =
                    30_000.0 / 1001,
                ),
            ),
        )
        assertEquals(
            "1280×720 · 30 fps",
            formatWords(ClipView<Any>(width = 1280, height = 720, framesPerSecond = 30.0)),
        )

        assertEquals("Getting the clip ready.", statusWords(ClipView<Any>()))
        assertEquals(
            "Fetching the clip from the card. It plays in 0:12.",
            statusWords(ClipView<Any>(startsIn = 12.seconds)),
        )
        assertNull(statusWords(ClipView<Any>(phase = PlayerPhase.Playing)))
        assertEquals(
            "The camera said no.",
            statusWords(ClipView<Any>(phase = PlayerPhase.Playing, failure = "The camera said no.")),
        )
    }
}
