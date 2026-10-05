package com.rextechnologies.sightline.design

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

/**
 * One row of a diagnostics stack.
 *
 * The label takes 42% and the value 58%, the split the Windows app uses: enough for a label to stay
 * on one line and enough for an address or a firmware version not to wrap at every value. The hairline
 * belongs to the row above it, so the last row in a stack does not draw a line into empty space.
 */
@Composable
fun DiagnosticRow(
    label: String,
    value: String,
    modifier: Modifier = Modifier,
    isLast: Boolean = false,
    valueTone: Tone? = null,
) {
    Column(modifier = modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(vertical = 9.dp),
            verticalAlignment = Alignment.Top,
        ) {
            RexText(
                text = label,
                style = RexType.BodySmall,
                modifier = Modifier.weight(0.42f),
            )
            RexText(
                text = value,
                style = RexType.BodyMedium.copy(
                    color = valueTone?.color ?: RexColors.Text,
                ),
                modifier = Modifier.weight(0.58f),
            )
        }
        if (!isLast) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(RexSpace.Hairline)
                    .background(RexColors.Line),
            )
        }
    }
}

/** A telemetry value. Tabular figures, so the row does not jitter as digits change. */
@Composable
fun Readout(value: String, modifier: Modifier = Modifier, tone: Tone? = null) {
    RexText(
        text = value,
        modifier = modifier,
        style = RexType.Readout.copy(color = tone?.color ?: RexColors.Text),
        maxLines = 1,
    )
}
