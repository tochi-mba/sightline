package com.rextechnologies.sightline.design

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import java.util.Locale

/**
 * The card everything sits in.
 *
 * The border tone is the entire status language of this system. A card with a Signal border is
 * saying something is ready; a Live border is saying something is wrong; a Line border is saying
 * nothing at all, which is what an informational card should say.
 */
@Composable
fun InfoCard(
    modifier: Modifier = Modifier,
    borderTone: Tone = Tone.Line,
    shape: Shape = RexShapes.Medium,
    content: @Composable ColumnScope.() -> Unit,
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .background(RexColors.Panel, shape)
            .border(RexSpace.Hairline, borderTone.color, shape)
            .padding(RexSpace.CardPadding),
        verticalArrangement = Arrangement.spacedBy(RexSpace.Small),
        content = content,
    )
}

/**
 * The inset block under a card's reason.
 *
 * One step further off the ground than the card, and its body is painted in the primary text colour
 * rather than the muted one - brighter than the reason above it, because the reason explains and this
 * tells somebody what to do.
 */
@Composable
fun AdvisoryBlock(heading: String, body: String, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .background(RexColors.Raised, RexShapes.Small)
            .padding(horizontal = RexSpace.Compact, vertical = 10.dp),
        verticalArrangement = Arrangement.spacedBy(5.dp),
    ) {
        RexText(text = heading, style = RexType.LabelSmall)
        RexText(text = body, style = RexType.BodySmall.copy(color = RexColors.Text))
    }
}

/**
 * A page's heading.
 *
 * The eyebrow is Signal, the headline is the page's name, and any status pill is aligned to the
 * bottom of the headline rather than its centre, so a long headline that wraps does not drag the
 * pill upward with it.
 */
@Composable
fun PageHeading(
    eyebrow: String,
    headline: String,
    modifier: Modifier = Modifier,
    statusText: String? = null,
    statusTone: Tone = Tone.Neutral,
) {
    Row(
        modifier = modifier.fillMaxWidth(),
        verticalAlignment = Alignment.Bottom,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            RexText(
                text = eyebrow.uppercase(Locale.ROOT),
                style = RexType.LabelSmall.copy(color = RexColors.Signal),
            )
            Spacer(Modifier.height(RexSpace.Tiny))
            RexText(
                text = headline,
                style = RexType.HeadlineLarge,
                modifier = Modifier.semantics { heading() },
            )
        }
        if (statusText != null) {
            Spacer(Modifier.width(RexSpace.Compact))
            Pill(text = statusText, tone = statusTone)
        }
    }
}

/**
 * What a screen shows when it has nothing.
 *
 * The glyph is a letterform rather than an icon, which is what makes an empty state read as part of
 * the same instrument rather than as a picture that failed to load.
 */
@Composable
fun EmptyState(
    glyph: String,
    title: String,
    body: String,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .background(RexColors.Panel, RexShapes.Medium)
            .border(RexSpace.Hairline, RexColors.Line, RexShapes.Medium)
            .padding(RexSpace.Large),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(RexSpace.Small),
    ) {
        Box(
            modifier = Modifier
                .clearAndSetSemantics { }
                .size(RexSpace.Huge)
                .border(RexSpace.Hairline, RexColors.Line, RexShapes.Small),
            contentAlignment = Alignment.Center,
        ) {
            RexText(
                text = glyph,
                style = RexType.TitleMedium.copy(color = RexColors.Muted),
            )
        }
        RexText(text = title, style = RexType.TitleMedium)
        RexText(text = body, style = RexType.BodySmall)
    }
}
