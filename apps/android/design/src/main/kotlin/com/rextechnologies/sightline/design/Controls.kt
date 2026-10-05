package com.rextechnologies.sightline.design

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import java.util.Locale

/**
 * On or off.
 *
 * A pill track with a knob that sits right and turns Signal when on: the accent means "on" here as it
 * means "ready" everywhere else. Announced as a switch with its state, so a screen reader says what a
 * tap will change.
 */
@Composable
fun ToggleSwitch(
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    contentDescription: String? = null,
) {
    Box(
        modifier = modifier
            .semantics {
                stateDescription = if (checked) "On" else "Off"
                contentDescription?.let { this.contentDescription = it }
            }
            .rexClickable(enabled = enabled, role = Role.Switch) { onCheckedChange(!checked) }
            .size(width = 46.dp, height = 28.dp)
            .background(if (checked) RexColors.SignalWash else RexColors.Raised, RexShapes.Pill)
            .border(RexSpace.Hairline, if (checked) RexColors.Signal else RexColors.Line, RexShapes.Pill)
            .padding(4.dp),
        contentAlignment = if (checked) Alignment.CenterEnd else Alignment.CenterStart,
    ) {
        Box(Modifier.size(20.dp).background(if (checked) RexColors.Signal else RexColors.Muted, CircleShape))
    }
}

/**
 * One of a few, side by side: video or photo, fit or fill.
 *
 * For two to four options whose names are short. Longer lists go in a [RexDialog] of [OptionRow]s.
 */
@Composable
fun <T> SegmentedChoice(
    options: List<T>,
    selected: T,
    label: (T) -> String,
    onSelect: (T) -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    Row(
        modifier = modifier
            .defaultMinSize(minHeight = RexSpace.TouchTarget)
            .border(RexSpace.Hairline, RexColors.Line, RexShapes.Pill)
            .padding(3.dp),
        horizontalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        options.forEach { option ->
            val chosen = option == selected
            Box(
                modifier = Modifier
                    .weight(1f)
                    .semantics { this.selected = chosen }
                    .rexClickable(enabled = enabled, role = Role.Tab) { onSelect(option) }
                    .defaultMinSize(minHeight = 42.dp)
                    .background(if (chosen) RexColors.SignalWash else RexColors.Ink, RexShapes.Pill)
                    .padding(horizontal = RexSpace.Compact),
                contentAlignment = Alignment.Center,
            ) {
                RexText(
                    text = label(option).uppercase(Locale.ROOT),
                    style = RexType.LabelSmall.copy(color = if (chosen) RexColors.Signal else RexColors.Muted),
                    maxLines = 1,
                )
            }
        }
    }
}

/** A whole number moved a step at a time, between [range]'s ends, with its [unit] beside it. */
@Composable
fun Stepper(
    value: Int,
    range: IntRange,
    step: Int,
    unit: String,
    onChange: (Int) -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(modifier = modifier, verticalAlignment = Alignment.CenterVertically) {
        StepButton("−", "Less", value - step >= range.first) { onChange(value - step) }
        RexText(
            text = "$value $unit",
            style = RexType.Readout,
            modifier = Modifier.width(96.dp).padding(horizontal = RexSpace.Small),
            maxLines = 1,
        )
        StepButton("+", "More", value + step <= range.last) { onChange(value + step) }
    }
}

@Composable
private fun StepButton(glyph: String, description: String, enabled: Boolean, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .semantics { contentDescription = description }
            .rexClickable(enabled = enabled, onClick = onClick)
            .size(RexSpace.TouchTarget)
            .border(RexSpace.Hairline, RexColors.Line, CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        RexText(text = glyph, style = RexType.TitleLarge)
    }
}

/**
 * One setting: its name, a line on what it does, and whatever changes it on the right.
 *
 * The whole row is the touch target when [onClick] is given, so a person does not have to hit the
 * small control at the end.
 */
@Composable
fun SettingRow(
    title: String,
    modifier: Modifier = Modifier,
    summary: String? = null,
    value: String? = null,
    enabled: Boolean = true,
    onClick: (() -> Unit)? = null,
    trailing: @Composable (() -> Unit)? = null,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .then(if (onClick != null) Modifier.rexClickable(enabled = enabled, onClick = onClick) else Modifier)
            .defaultMinSize(minHeight = RexSpace.TouchTarget)
            .padding(vertical = RexSpace.Small),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            RexText(text = title, style = RexType.TitleMedium)
            summary?.let { RexText(text = it, style = RexType.BodySmall) }
        }
        value?.let {
            RexText(
                text = it,
                style = RexType.BodyMedium.copy(color = RexColors.Signal),
                modifier = Modifier.padding(start = RexSpace.Compact),
                maxLines = 1,
            )
        }
        trailing?.let {
            Box(Modifier.padding(start = RexSpace.Compact)) { it() }
        }
    }
}

/** One option in a list of them, with a ring that fills when it is the one chosen. */
@Composable
fun OptionRow(text: String, selected: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .semantics { this.selected = selected }
            .rexClickable(role = Role.RadioButton, onClick = onClick)
            .defaultMinSize(minHeight = RexSpace.TouchTarget),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            modifier = Modifier
                .size(18.dp)
                .border(RexSpace.Hairline, if (selected) RexColors.Signal else RexColors.Muted, CircleShape)
                .padding(4.dp),
        ) {
            if (selected) {
                Box(Modifier.size(10.dp).background(RexColors.Signal, CircleShape))
            }
        }
        RexText(text = text, style = RexType.BodyMedium, modifier = Modifier.padding(start = RexSpace.Compact))
    }
}

/**
 * A question that needs an answer before anything else happens, for the few things that lose data:
 * deleting from the card, or disconnecting during a recording.
 *
 * @param confirm The action that goes ahead, and its words; null for a dialog that only informs or lists.
 * @param destructive Whether going ahead loses something, which paints the confirm button in Live.
 */
@Composable
fun RexDialog(
    title: String,
    onDismiss: () -> Unit,
    body: String? = null,
    confirm: Pair<String, () -> Unit>? = null,
    dismissLabel: String = "Cancel",
    destructive: Boolean = false,
    content: @Composable ColumnScope.() -> Unit = {},
) {
    Dialog(onDismissRequest = onDismiss) {
        InfoCard(shape = RexShapes.Large) {
            RexText(text = title, style = RexType.TitleLarge)
            body?.let { RexText(text = it, style = RexType.BodyMedium.copy(color = RexColors.Muted)) }
            content()
            Row(
                modifier = Modifier.fillMaxWidth().padding(top = RexSpace.Small),
                horizontalArrangement = Arrangement.spacedBy(RexSpace.Small, Alignment.End),
            ) {
                OutlineAction(text = dismissLabel, onClick = onDismiss)
                confirm?.let { (label, action) ->
                    if (destructive) {
                        OutlineAction(text = label, onClick = action, tone = Tone.Live)
                    } else {
                        SignalButton(text = label, onClick = action)
                    }
                }
            }
        }
    }
}

/** A thin rule between groups. */
@Composable
fun Divider(modifier: Modifier = Modifier) {
    Box(modifier.fillMaxWidth().height(RexSpace.Hairline).background(RexColors.Line))
}
