package com.rextechnologies.sightline.ui.onboarding

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.rextechnologies.sightline.core.updates.WhatsNewEntry
import com.rextechnologies.sightline.design.InfoCard
import com.rextechnologies.sightline.design.PageHeading
import com.rextechnologies.sightline.design.RexColors
import com.rextechnologies.sightline.design.RexSpace
import com.rextechnologies.sightline.design.RexText
import com.rextechnologies.sightline.design.RexType
import com.rextechnologies.sightline.design.SignalButton

/**
 * What changed, shown once on the first run after an update that earned it, newest version first.
 */
@Composable
fun WhatsNewScreen(entries: List<WhatsNewEntry>, onContinue: () -> Unit) {
    Box(Modifier.fillMaxSize().background(RexColors.Ink), contentAlignment = Alignment.Center) {
        Column(
            Modifier.widthIn(
                max = 520.dp,
            ).fillMaxWidth().verticalScroll(rememberScrollState()).padding(RexSpace.PageMargin),
            verticalArrangement = Arrangement.spacedBy(RexSpace.CardSpacing),
        ) {
            PageHeading(eyebrow = "Updated", headline = "What's new")
            entries.forEach { entry ->
                InfoCard {
                    RexText("Version ${entry.version}", style = RexType.LabelSmall.copy(color = RexColors.Signal))
                    entry.lines.forEach { RexText("• $it", style = RexType.BodyMedium) }
                }
            }
            SignalButton(text = "Continue", onClick = onContinue, modifier = Modifier.fillMaxWidth())
        }
    }
}
