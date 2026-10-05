package com.rextechnologies.sightline.design

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.semantics.ProgressBarRangeInfo
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.progressBarRangeInfo
import androidx.compose.ui.semantics.semantics

/**
 * A track that shows how far through something is, and can be pressed to move it.
 *
 * Deliberately not a slider. A slider promises continuous control, and moving something on the camera
 * is a request it answers a moment later; sending one per pixel of drag would queue dozens of them. So
 * a tap moves it, and a drag moves it once, where it ends. Read-only, it is a download's progress.
 *
 * @param fraction how far through, or `null` when there is no end to measure against, in which case
 *   the track is drawn empty rather than pretending.
 * @param onSeek where the person asked to go, as a fraction, or `null` when the track is read-only.
 */
@Composable
fun ProgressTrack(
    fraction: Float?,
    modifier: Modifier = Modifier,
    onSeek: ((Float) -> Unit)? = null,
    contentDescription: String? = null,
) {
    var width by remember { mutableIntStateOf(0) }
    var dragX by remember { mutableFloatStateOf(0f) }
    val clamped = fraction?.coerceIn(0f, 1f)

    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(RexSpace.Small)
            .onSizeChanged { width = it.width }
            .background(RexColors.Raised, RexShapes.Small)
            .semantics {
                progressBarRangeInfo = ProgressBarRangeInfo(clamped ?: 0f, 0f..1f)
                contentDescription?.let { this.contentDescription = it }
            }
            .then(
                if (onSeek == null) {
                    Modifier
                } else {
                    Modifier
                        .pointerInput(onSeek) {
                            detectTapGestures { offset ->
                                if (width > 0) onSeek((offset.x / width).coerceIn(0f, 1f))
                            }
                        }
                        .pointerInput(onSeek) {
                            detectHorizontalDragGestures(
                                onDragStart = { dragX = it.x },
                                onDragEnd = { if (width > 0) onSeek((dragX / width).coerceIn(0f, 1f)) },
                            ) { change, _ -> dragX = change.position.x }
                        }
                },
            ),
    ) {
        if (clamped != null && clamped > 0f) {
            Box(
                modifier = Modifier
                    .fillMaxWidth(clamped)
                    .fillMaxHeight()
                    .background(RexColors.Signal, RexShapes.Small),
            )
        }
    }
}
