package com.rextechnologies.sightline.design

import androidx.compose.foundation.layout.width
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.assert
import androidx.compose.ui.test.click
import androidx.compose.ui.test.junit4.v2.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.percentOffset
import androidx.compose.ui.test.performSemanticsAction
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.swipeRight
import androidx.compose.ui.unit.dp
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

/** A clip's playhead: tapped, dragged, and adjusted by a screen reader, or left alone when it cannot move. */
@RunWith(RobolectricTestRunner::class)
class SeekBarTest {
    @get:Rule
    val compose = createComposeRule()

    private val seeks = mutableListOf<Float>()

    private fun show(fraction: Float, enabled: Boolean = true) {
        compose.setContent {
            RexTheme {
                SeekBar(fraction, { seeks += it }, "Where the clip is", Modifier.width(200.dp), enabled)
            }
        }
    }

    @Test
    fun `a tap goes to where it lands and a drag follows the finger`() {
        show(0.25f)
        val bar = compose.onNodeWithContentDescription("Where the clip is")

        bar.assert(SemanticsMatcher.expectValue(SemanticsProperties.ProgressBarRangeInfo, rangeOf(0.25f)))
        bar.performTouchInput { click(percentOffset(0.75f, 0.5f)) }
        assertEquals(0.75f, seeks.last(), 0.02f)

        bar.performTouchInput { swipeRight(startX = centerX, endX = right) }
        assertEquals(1f, seeks.last(), 0.02f)
    }

    @Test
    fun `a screen reader sets it, within its range`() {
        show(0f)

        compose.onNodeWithContentDescription("Where the clip is")
            .performSemanticsAction(SemanticsActions.SetProgress) { it(1.5f) }

        assertEquals(listOf(1f), seeks)
    }

    @Test
    fun `one that cannot move says so and ignores the finger`() {
        show(2f, enabled = false)
        val bar = compose.onNodeWithContentDescription("Where the clip is")

        bar.assert(SemanticsMatcher.keyIsDefined(SemanticsProperties.Disabled))
        bar.assert(SemanticsMatcher.expectValue(SemanticsProperties.ProgressBarRangeInfo, rangeOf(1f)))
        bar.performTouchInput { click(center) }

        assertTrue(seeks.isEmpty())
    }

    private fun rangeOf(value: Float) = androidx.compose.ui.semantics.ProgressBarRangeInfo(value, 0f..1f)
}
