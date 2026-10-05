package com.rextechnologies.sightline.design

import android.provider.Settings
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.ProvidableCompositionLocal
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.unit.dp

/** Corner radii, matching the Windows app's RadiusSmall / Medium / Large / Pill. */
object RexShapes {
    val Small: Shape = RoundedCornerShape(8.dp)
    val Medium: Shape = RoundedCornerShape(12.dp)
    val Large: Shape = RoundedCornerShape(18.dp)
    val Pill: Shape = RoundedCornerShape(50)
}

/**
 * The text style everything inherits.
 *
 * Declared here rather than pulled from Material because this app takes no Material dependency at
 * all: every REX component is built on foundation primitives, which is what keeps a Material theme
 * update from quietly restyling an instrument.
 */
val LocalRexTextStyle: ProvidableCompositionLocal<TextStyle> =
    compositionLocalOf { RexType.BodyMedium.merge(RexType.CenteredMetrics) }

/**
 * Whether this device has asked for less movement.
 *
 * Every animation in the app is gated on this rather than on a preference of its own, because the
 * person already told the system once.
 */
val LocalReducedMotion: ProvidableCompositionLocal<Boolean> = staticCompositionLocalOf { false }

/**
 * Wraps content in the REX system.
 *
 * There is no colour scheme object to provide: components reference [RexColors] directly and take a
 * [Tone] where a status is involved, so there is no path by which a caller can hand one an
 * off-palette colour.
 */
@Composable
fun RexTheme(content: @Composable () -> Unit) {
    val context = LocalContext.current
    val reducedMotion = remember(context) { animationsAreDisabled(context.contentResolver) }
    CompositionLocalProvider(
        LocalRexTextStyle provides RexType.BodyMedium.merge(RexType.CenteredMetrics),
        LocalReducedMotion provides reducedMotion,
        content = content,
    )
}

/**
 * Reads the system animation scale.
 *
 * A scale of zero is what "Remove animations" in accessibility settings actually sets, and it is the
 * only reliable signal an ordinary app gets. Reading it can throw on a device with an unusual
 * settings provider, and an app that crashes on start because it asked about animation is worse than
 * one that animates when it should not have.
 */
private fun animationsAreDisabled(resolver: android.content.ContentResolver): Boolean =
    runCatching {
        Settings.Global.getFloat(resolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f)
    }.getOrDefault(1f) == 0f
