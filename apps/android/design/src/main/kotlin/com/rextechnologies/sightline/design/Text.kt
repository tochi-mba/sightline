package com.rextechnologies.sightline.design

import androidx.compose.foundation.text.BasicText
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.style.TextOverflow
import java.util.Locale

/**
 * Every piece of text in the system.
 *
 * A thin wrapper over [BasicText] rather than a Material `Text`, so the app carries no Material
 * dependency and every string inherits the centred metrics the theme provides.
 */
@Composable
fun RexText(
    text: String,
    modifier: Modifier = Modifier,
    style: TextStyle = LocalRexTextStyle.current,
    maxLines: Int = Int.MAX_VALUE,
    overflow: TextOverflow = TextOverflow.Clip,
) {
    BasicText(
        text = text,
        modifier = modifier,
        style = LocalRexTextStyle.current.merge(style),
        maxLines = maxLines,
        overflow = overflow,
    )
}

/**
 * A section heading.
 *
 * Upper-cased here rather than at the call site, so the rhythm of the system cannot be broken by
 * somebody typing a heading in sentence case.
 */
@Composable
fun SectionLabel(text: String, modifier: Modifier = Modifier, tone: Tone = Tone.Neutral) {
    RexText(
        text = text.uppercase(Locale.ROOT),
        modifier = modifier.semantics { heading() },
        style = RexType.LabelSmall.copy(color = tone.color),
    )
}
