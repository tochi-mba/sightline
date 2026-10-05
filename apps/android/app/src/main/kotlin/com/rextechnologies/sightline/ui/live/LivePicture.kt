package com.rextechnologies.sightline.ui.live

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.ProduceStateScope
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import com.rextechnologies.sightline.core.camera.LiveFrame
import com.rextechnologies.sightline.core.settings.GridOverlay
import com.rextechnologies.sightline.core.settings.PictureFit
import com.rextechnologies.sightline.design.RexColors
import com.rextechnologies.sightline.live.FrameDecoder
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.conflate
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.mapNotNull

/**
 * The camera's live picture, fitted or filling, turned and mirrored as the settings say, with the
 * framing guide over it.
 *
 * Frames are decoded away from the main thread, and a frame that arrives while the last is still being
 * decoded replaces it rather than queueing: a picture that falls behind is never caught up by showing
 * old frames, only by skipping to the newest.
 */
@Composable
fun LivePicture(frames: StateFlow<LiveFrame?>, settings: LiveSettings, modifier: Modifier = Modifier) {
    val picture by rememberLivePicture(frames)
    Box(
        modifier
            .background(RexColors.Ink)
            .clipToBounds()
            .semantics { contentDescription = "The camera's live picture" },
    ) {
        picture?.let { bitmap ->
            Image(
                bitmap = bitmap,
                contentDescription = null,
                contentScale = if (settings.fit == PictureFit.Fill) ContentScale.Crop else ContentScale.Fit,
                modifier = Modifier.fillMaxSize().graphicsLayer {
                    rotationZ = if (settings.flip) 180f else 0f
                    scaleX = if (settings.mirror) -1f else 1f
                },
            )
        }
        if (settings.grid != GridOverlay.None) {
            Canvas(Modifier.fillMaxSize()) { drawGuide(settings.grid) }
        }
    }
}

/** The newest frame as a picture, decoded off the main thread, or null before the first. */
@Composable
fun rememberLivePicture(frames: StateFlow<LiveFrame?>): androidx.compose.runtime.State<ImageBitmap?> {
    val decoder = remember { FrameDecoder() }
    return produceState<ImageBitmap?>(null, frames) { show(frames, decoder) }
}

/** Shows each newest frame as it is decoded, for as long as the picture is on screen. */
private suspend fun ProduceStateScope<ImageBitmap?>.show(frames: StateFlow<LiveFrame?>, decoder: FrameDecoder) =
    frames.filterNotNull()
        .conflate()
        .map { decoder.decode(it.jpeg) }
        .flowOn(Dispatchers.Default)
        .mapNotNull { it?.asImageBitmap() }
        .collect { value = it }

/** Draws [guide]: thirds as four hairlines, the centre as a small cross. */
private fun DrawScope.drawGuide(guide: GridOverlay) {
    val line = RexColors.Text.copy(alpha = 0.45f)
    val stroke = 1.5f
    if (guide == GridOverlay.Thirds) {
        for (third in 1..2) {
            val x = size.width * third / 3
            val y = size.height * third / 3
            drawLine(line, Offset(x, 0f), Offset(x, size.height), stroke)
            drawLine(line, Offset(0f, y), Offset(size.width, y), stroke)
        }
    } else {
        val arm = size.minDimension / 20
        drawLine(line, Offset(center.x - arm, center.y), Offset(center.x + arm, center.y), stroke)
        drawLine(line, Offset(center.x, center.y - arm), Offset(center.x, center.y + arm), stroke)
    }
}
