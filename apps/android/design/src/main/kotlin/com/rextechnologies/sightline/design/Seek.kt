package com.rextechnologies.sightline.design

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.ProgressBarRangeInfo
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.disabled
import androidx.compose.ui.semantics.progressBarRangeInfo
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.setProgress
import androidx.compose.ui.unit.dp

/**
 * Where something is along its length, which a tap or a drag moves: a clip's playhead.
 *
 * A thin track filled in Signal as far as [fraction], with a knob there. Announced as a range with its value, and
 * adjustable by a screen reader's own gestures, so it is never only a picture of a slider.
 */
@Composable
fun SeekBar(
    fraction: Float,
    onSeek: (Float) -> Unit,
    contentDescription: String,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    val shown = fraction.coerceIn(0f, 1f)
    val gestures = if (enabled) {
        Modifier
            .pointerInput(Unit) { detectTapGestures { onSeek((it.x / size.width).coerceIn(0f, 1f)) } }
            .pointerInput(Unit) {
                detectHorizontalDragGestures { change, _ -> onSeek((change.position.x / size.width).coerceIn(0f, 1f)) }
            }
            .semantics {
                setProgress { target ->
                    onSeek(target.coerceIn(0f, 1f))
                    true
                }
            }
    } else {
        Modifier.semantics { disabled() }
    }

    Box(
        modifier
            .semantics {
                this.contentDescription = contentDescription
                progressBarRangeInfo = ProgressBarRangeInfo(shown, 0f..1f)
            }
            .then(gestures)
            .fillMaxWidth()
            .height(RexSpace.TouchTarget),
        contentAlignment = Alignment.CenterStart,
    ) {
        Box(Modifier.fillMaxWidth().height(4.dp).background(RexColors.Line, RexShapes.Pill))
        Box(Modifier.fillMaxWidth(shown).height(KNOB), contentAlignment = Alignment.CenterEnd) {
            Box(
                Modifier.fillMaxWidth().height(4.dp)
                    .background(if (enabled) RexColors.Signal else RexColors.Muted, RexShapes.Pill),
            )
            Box(Modifier.size(KNOB).background(if (enabled) RexColors.Signal else RexColors.Muted, CircleShape))
        }
    }
}

private val KNOB = 16.dp
