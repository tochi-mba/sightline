package com.rextechnologies.sightline.ui.library

import android.graphics.Bitmap
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.keepScreenOn
import androidx.compose.ui.layout.ContentScale
import com.rextechnologies.sightline.core.playback.ClipSession
import com.rextechnologies.sightline.core.playback.ClipView
import com.rextechnologies.sightline.core.playback.PlayerPhase
import com.rextechnologies.sightline.design.OutlineAction
import com.rextechnologies.sightline.design.RexColors
import com.rextechnologies.sightline.design.RexShapes
import com.rextechnologies.sightline.design.RexSpace
import com.rextechnologies.sightline.design.RexText
import com.rextechnologies.sightline.design.RexType
import com.rextechnologies.sightline.design.SeekBar
import com.rextechnologies.sightline.design.SignalButton
import com.rextechnologies.sightline.ui.live.clock
import java.util.Locale
import kotlin.time.Duration
import kotlin.time.toJavaDuration

/**
 * A video from the card, playing over the card's files while it is still being fetched: its picture, where it is,
 * and why it waits when it does. The screen stays on while it shows. Back, or Close, ends the clip and stops its
 * fetch.
 */
@Composable
fun PlayerScreen(session: ClipSession<Bitmap>, onClose: () -> Unit) {
    val view by session.view.collectAsState()
    BackHandler(onBack = onClose)

    Column(Modifier.fillMaxSize().background(RexColors.Ink).keepScreenOn().padding(horizontal = RexSpace.PageMargin)) {
        Row(
            Modifier.fillMaxWidth().padding(vertical = RexSpace.Medium),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(Modifier.weight(1f)) {
                RexText(text = session.clip.file.displayName, style = RexType.TitleLarge)
                formatWords(view)?.let { RexText(text = it, style = RexType.BodySmall.copy(color = RexColors.Muted)) }
            }
            OutlineAction(text = "Close", onClick = onClose)
        }

        Box(
            Modifier.weight(1f).fillMaxWidth().background(Color.Black, RexShapes.Small),
            contentAlignment = Alignment.Center,
        ) {
            view.picture?.let {
                Image(
                    it.asImageBitmap(),
                    contentDescription = "The clip",
                    contentScale = ContentScale.Fit,
                    modifier = Modifier.fillMaxSize(),
                )
            }
            statusWords(view)?.let {
                RexText(text = it, style = RexType.BodyMedium, modifier = Modifier.padding(RexSpace.Medium))
            }
        }

        val length = view.duration
        SeekBar(
            fraction = if (length > Duration.ZERO) (view.position / length).toFloat() else 0f,
            onSeek = { session.seek(length * it.toDouble()) },
            contentDescription = "Where the clip is",
            enabled = length > Duration.ZERO,
            modifier = Modifier.padding(top = RexSpace.Small),
        )

        Row(
            Modifier.fillMaxWidth().padding(vertical = RexSpace.Compact),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(RexSpace.Medium),
        ) {
            val playing = view.phase == PlayerPhase.Playing || view.phase == PlayerPhase.Waiting
            SignalButton(text = if (playing) "Pause" else "Play", onClick = session::playPause)
            RexText(
                text = "${clock(view.position.toJavaDuration())} / ${clock(length.toJavaDuration())}",
                style = RexType.Readout,
            )
        }
    }
}

/** What the clip really is, from its own header: its size in pixels and its pictures a second. */
fun formatWords(view: ClipView<*>): String? = if (view.width == 0) {
    null
} else {
    "${view.width}×${view.height} · ${String.format(
        Locale.ROOT,
        "%.2f",
        view.framesPerSecond,
    ).trimEnd('0').trimEnd('.')} fps"
}

/** Why nothing moves, when nothing does; null while it plays. */
fun statusWords(view: ClipView<*>): String? = when {
    view.failure != null -> view.failure
    view.phase != PlayerPhase.Waiting -> null
    else -> view.startsIn?.let { "Fetching the clip from the card. It plays in ${clock(it.toJavaDuration())}." }
        ?: "Getting the clip ready."
}
