package com.rextechnologies.sightline.design

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.unit.dp
import java.util.Locale

/**
 * A status pill.
 *
 * Outline only, never filled: a filled pill in Signal would out-shout the thing it is labelling, and
 * the point of the accent is that it is rare. The border and the text share one tone, so a pill can
 * never say one thing in its colour and another in its words.
 */
@Composable
fun Pill(text: String, tone: Tone, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .border(RexSpace.Hairline, tone.color, RexShapes.Pill)
            .padding(horizontal = 10.dp, vertical = 5.dp),
    ) {
        RexText(
            text = text.uppercase(Locale.ROOT),
            style = RexType.LabelSmall.copy(color = tone.color),
            maxLines = 1,
        )
    }
}

/**
 * A nine-dp dot.
 *
 * Decorative: it always sits beside text that says the same thing, so it is hidden from the screen
 * reader rather than announced as an unlabelled image.
 */
@Composable
fun StatusDot(tone: Tone, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .clearAndSetSemantics { }
            .size(9.dp)
            .background(tone.color, CircleShape),
    )
}
