package com.rextechnologies.sightline.ui.hud

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.rextechnologies.sightline.AppGraph
import com.rextechnologies.sightline.core.hud.HudFormat
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.core.settings.HudLayout
import com.rextechnologies.sightline.design.RexColors
import com.rextechnologies.sightline.design.RexShapes
import com.rextechnologies.sightline.design.RexSpace
import com.rextechnologies.sightline.design.RexText
import com.rextechnologies.sightline.design.RexType
import com.rextechnologies.sightline.hud.HudReading
import java.util.Locale

/** How much of the picture's height the speed may take. */
private const val SPEED_SHARE = 0.26f

/** From this width the other figures sit beside the speed, along the bottom, rather than at the top. */
private val SIDE_BY_SIDE = 520.dp

/** Dark enough behind a figure to read it in sunlight, light enough to see the picture through it. */
private val PANEL = RexColors.Scrim.copy(alpha = 0.55f)

/**
 * The ride HUD over the live picture, readable at a glance from a handlebar without hiding the picture:
 * speed in one corner, as large as the picture's height allows, and as much else as the layout asks for
 * in another, each on a translucent panel of its own. The middle of the picture is left clear.
 *
 * The GPS runs only while this is showing.
 */
@Composable
fun HudOverlay(graph: AppGraph, modifier: Modifier = Modifier) {
    DisposableEffect(Unit) {
        graph.hud.start()
        onDispose { graph.hud.stop() }
    }
    val reading by graph.hud.reading.collectAsState()
    val version by graph.settings.changes.collectAsState()
    val (format, layout, mirror) = remember(version) {
        Triple(
            HudFormat(graph.settings[AppSettings.Units]),
            graph.settings[AppSettings.Layout],
            graph.settings[AppSettings.HudMirror],
        )
    }

    BoxWithConstraints(
        modifier
            .graphicsLayer { scaleX = if (mirror) -1f else 1f }
            .padding(RexSpace.Compact),
    ) {
        // Big on a phone held upright, still clear of the picture's middle on one turned on its side.
        val speedSize = with(LocalDensity.current) {
            (maxHeight * SPEED_SHARE).toSp().value.coerceAtMost(RexType.HudFigure.fontSize.value).sp
        }
        HudContent(reading, format, layout, speedSize, sideBySide = maxWidth >= SIDE_BY_SIDE)
    }
}

/**
 * The HUD's numbers for [layout], drawn from [reading] in [format]'s units: the speed at [speedSize] in the
 * bottom corner, and the other figures [sideBySide] with it along the bottom, or in the top corner when the
 * picture is too narrow for both.
 */
@Composable
fun HudContent(
    reading: HudReading,
    format: HudFormat,
    layout: HudLayout,
    speedSize: TextUnit = RexType.HudFigure.fontSize,
    sideBySide: Boolean = false,
) {
    val fix = reading.fix
    Box(Modifier.fillMaxSize()) {
        Column(Modifier.align(Alignment.BottomStart).hudPanel()) {
            val speed = format.speed(fix?.speed)
            RexText(
                text = speed,
                style = RexType.HudFigure.copy(fontSize = speedSize, lineHeight = speedSize),
                modifier = Modifier.semantics { contentDescription = "$speed ${format.speedUnit}" },
            )
            RexText(text = format.speedUnit.uppercase(Locale.ROOT), style = RexType.LabelSmall)
            if (fix == null) {
                RexText(text = "Waiting for GPS", style = RexType.BodySmall)
            }
        }

        if (layout != HudLayout.Minimal) {
            Column(
                Modifier.align(if (sideBySide) Alignment.BottomEnd else Alignment.TopEnd).hudPanel(),
                horizontalAlignment = Alignment.End,
                verticalArrangement = Arrangement.spacedBy(RexSpace.Small),
            ) {
                Row(horizontalArrangement = Arrangement.spacedBy(RexSpace.Medium)) {
                    Figure("Heading", format.heading(fix?.bearing))
                    Figure("Height", format.altitude(fix?.altitude))
                }
                if (layout == HudLayout.Cockpit) {
                    Row(horizontalArrangement = Arrangement.spacedBy(RexSpace.Medium)) {
                        Figure("Distance", format.distance(reading.distance))
                        Figure("Top speed", "${format.speed(reading.topSpeed)} ${format.speedUnit}")
                        Figure("Moving", format.duration(reading.moving))
                    }
                }
            }
        }
    }
}

private fun Modifier.hudPanel(): Modifier =
    background(PANEL, RexShapes.Small).padding(horizontal = RexSpace.Medium, vertical = RexSpace.Compact)

@Composable
private fun Figure(label: String, value: String) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        RexText(text = value, style = RexType.HudSecondary, maxLines = 1)
        RexText(text = label.uppercase(Locale.ROOT), style = RexType.LabelSmall)
    }
}
