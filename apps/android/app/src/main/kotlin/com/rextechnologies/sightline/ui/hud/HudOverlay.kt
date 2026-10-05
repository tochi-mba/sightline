package com.rextechnologies.sightline.ui.hud

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import com.rextechnologies.sightline.AppGraph
import com.rextechnologies.sightline.core.hud.HudFormat
import com.rextechnologies.sightline.core.settings.AppSettings
import com.rextechnologies.sightline.core.settings.HudLayout
import com.rextechnologies.sightline.design.RexColors
import com.rextechnologies.sightline.design.RexSpace
import com.rextechnologies.sightline.design.RexText
import com.rextechnologies.sightline.design.RexType
import com.rextechnologies.sightline.hud.HudReading
import java.util.Locale

/**
 * The ride HUD over the live picture: speed as large as the screen allows, and as much else as the
 * layout asks for, readable at a glance from a handlebar.
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

    Box(
        modifier
            .background(RexColors.Scrim)
            .graphicsLayer { scaleX = if (mirror) -1f else 1f }
            .padding(RexSpace.Medium),
    ) {
        HudContent(reading, format, layout)
    }
}

/** The HUD's numbers for [layout], drawn from [reading] in [format]'s units. */
@Composable
fun HudContent(reading: HudReading, format: HudFormat, layout: HudLayout) {
    val fix = reading.fix
    Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
        val speed = format.speed(fix?.speed)
        RexText(
            text = speed,
            style = RexType.HudFigure,
            modifier = Modifier.semantics { contentDescription = "$speed ${format.speedUnit}" },
        )
        RexText(text = format.speedUnit.uppercase(Locale.ROOT), style = RexType.LabelSmall)

        if (layout != HudLayout.Minimal) {
            Row(
                Modifier.fillMaxWidth().padding(top = RexSpace.Medium),
                horizontalArrangement = Arrangement.SpaceEvenly,
            ) {
                Figure("Heading", format.heading(fix?.bearing))
                Figure("Height", format.altitude(fix?.altitude))
            }
        }

        if (layout == HudLayout.Cockpit) {
            Row(
                Modifier.fillMaxWidth().padding(top = RexSpace.Medium),
                horizontalArrangement = Arrangement.SpaceEvenly,
            ) {
                Figure("Distance", format.distance(reading.distance))
                Figure("Top speed", "${format.speed(reading.topSpeed)} ${format.speedUnit}")
                Figure("Moving", format.duration(reading.moving))
            }
        }

        if (fix == null) {
            RexText(
                text = "Waiting for GPS",
                style = RexType.BodySmall,
                modifier = Modifier.padding(top = RexSpace.Small),
            )
        }
    }
}

@Composable
private fun Figure(label: String, value: String) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        RexText(text = value, style = RexType.HudSecondary, maxLines = 1)
        RexText(text = label.uppercase(Locale.ROOT), style = RexType.LabelSmall)
    }
}
