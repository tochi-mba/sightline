package com.rextechnologies.sightline.design

import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.interaction.collectIsHoveredAsState
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.composed
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.semantics.Role

/**
 * The REX touch state.
 *
 * The Windows app drops a control to 0.88 opacity on hover and 0.72 on press, and so does every REX
 * phone app, so those are the numbers rather than a new pair invented for this one.
 *
 * Focus is still honoured, because a phone can have a keyboard attached and a switch-access user has
 * nothing else to go on.
 *
 * Opacity rather than scale on purpose: a scale animation is motion, and motion is the thing a
 * reduced-motion setting asks for less of. This reads identically whether or not animations are on.
 */
fun Modifier.rexClickable(
    enabled: Boolean = true,
    role: Role? = Role.Button,
    onClickLabel: String? = null,
    onClick: () -> Unit,
): Modifier = composed {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val hovered by interaction.collectIsHoveredAsState()
    val focused by interaction.collectIsFocusedAsState()

    this
        .alpha(
            when {
                !enabled -> DISABLED_ALPHA
                pressed -> PRESSED_ALPHA
                hovered || focused -> HOVERED_ALPHA
                else -> 1f
            },
        )
        .clickable(
            interactionSource = interaction,
            // Material's ripple is invisible against this palette and would be the app's only
            // Material dependency. The opacity above is the affordance.
            indication = null,
            enabled = enabled,
            role = role,
            onClickLabel = onClickLabel,
            onClick = onClick,
        )
}

/** What a disabled control looks like: present, legible, obviously not for pressing. */
const val DISABLED_ALPHA: Float = 0.45f

private const val PRESSED_ALPHA = 0.72f
private const val HOVERED_ALPHA = 0.88f
