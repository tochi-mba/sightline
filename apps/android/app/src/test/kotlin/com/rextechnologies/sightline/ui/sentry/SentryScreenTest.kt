package com.rextechnologies.sightline.ui.sentry

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.rextechnologies.sightline.TestGraph
import com.rextechnologies.sightline.core.sentry.SentryState
import com.rextechnologies.sightline.core.sentry.SentryStatus
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.design.RexTheme
import com.rextechnologies.sightline.ui.FakePlatform
import com.rextechnologies.sightline.ui.SightlineApp
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream
import java.time.Duration
import kotlin.test.assertEquals
import kotlin.test.assertTrue

@RunWith(AndroidJUnit4::class)
class SentryScreenTest {
    @get:Rule
    val compose = createComposeRule()

    private val platform = FakePlatform()
    private val test = TestGraph().also {
        it.graph.settings[AppSettings.OnboardingDone] = true
        it.graph.settings[AppSettings.SentryArmDelay] = 0
        it.graph.settings[AppSettings.SentryRecords] = false
    }

    /** A real 640 by 360 JPEG: grey, with a white square at [square]'s left edge when given. */
    private fun frame(square: Int? = null): ByteArray {
        val bitmap = Bitmap.createBitmap(640, 360, Bitmap.Config.ARGB_8888).apply { eraseColor(Color.GRAY) }
        square?.let {
            Canvas(bitmap).drawRect(it.toFloat(), 0f, it + 200f, 200f, Paint().apply { color = Color.WHITE })
        }
        return ByteArrayOutputStream().also { bitmap.compress(Bitmap.CompressFormat.JPEG, 90, it) }.toByteArray()
    }

    private fun openSentry() {
        compose.setContent { RexTheme { SightlineApp(test.graph, platform) } }
        compose.onNodeWithText("SENTRY").performClick()
        compose.waitForIdle()
    }

    private fun settle(duration: Duration) {
        test.settle(duration)
        compose.waitForIdle()
    }

    @Test
    fun `arming without a camera waits for one, and disarming stands down`() {
        openSentry()

        compose.onNodeWithText("Not armed").assertExists()
        compose.onNodeWithText(
            "Sensitivity medium, starts after 0 seconds, and does not record",
            substring = true,
        ).assertExists()
        compose.onNodeWithText("ARM SENTRY").performClick()
        compose.waitForIdle()
        compose.onNodeWithText("Waiting for the camera").assertExists()
        compose.onNodeWithText("Watching").assertExists()
        compose.onNodeWithText("DISARM").performClick()
        compose.waitForIdle()

        assertTrue("notifications" in platform.asked)
        assertEquals(1, platform.servicesStarted)
        compose.onNodeWithText("Not armed").assertExists()
    }

    @Test
    fun `movement on the live view raises an alarm, listed with its picture`() {
        val still = frame()
        val moving = listOf(frame(0), frame(400))
        test.link.stream = {
            frames += List(10) { still } + List(20) { moving[it % 2] } + List(10) { still }
            pace = kotlin.time.Duration.parse("100ms")
        }
        test.graph.settings[AppSettings.SentryRecords] = true
        test.connected()
        openSentry()

        compose.onNodeWithText("ARM SENTRY").performClick()
        settle(Duration.ofSeconds(3))

        assertEquals(1, test.actions.raised.size)
        compose.onNodeWithText("ALARMS").assertExists()
        compose.onNodeWithText("Movement at", substring = true).assertExists()
    }

    @Test
    fun `each state has its heading and its word`() {
        assertEquals("Movement", headline(SentryStatus(armed = true, watch = SentryState.Alarm(0.1))))
        assertEquals("Watching", headline(SentryStatus(armed = true, watch = SentryState.Arming)))
        assertEquals("Not armed", headline(SentryStatus()))
        assertEquals(
            listOf("Off", "Arming", "Watching", "Alarm", "Settling"),
            listOf(
                SentryState.Disarmed,
                SentryState.Arming,
                SentryState.Watching,
                SentryState.Alarm(0.2),
                SentryState.Cooldown,
            ).map(::stateWord),
        )
    }
}
