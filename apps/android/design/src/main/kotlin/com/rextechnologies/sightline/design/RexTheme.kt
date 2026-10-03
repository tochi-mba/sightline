package com.rextechnologies.sightline.design

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

val RexInk = Color(0xFF080A09)
val RexPanel = Color(0xFF111512)
val RexRaised = Color(0xFF181E19)
val RexLine = Color(0xFF29302A)
val RexText = Color(0xFFF2F5EE)
val RexMuted = Color(0xFF858D83)
val RexSignal = Color(0xFFD7FF3F)
val RexLive = Color(0xFFFF774D)

private val RexColors = darkColorScheme(
    primary = RexSignal,
    onPrimary = RexInk,
    secondary = RexLive,
    onSecondary = RexInk,
    background = RexInk,
    onBackground = RexText,
    surface = RexPanel,
    onSurface = RexText,
    surfaceVariant = RexRaised,
    onSurfaceVariant = RexMuted,
    outline = RexLine,
    error = RexLive,
)

@Composable
fun SightlineTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = RexColors, content = content)
}
