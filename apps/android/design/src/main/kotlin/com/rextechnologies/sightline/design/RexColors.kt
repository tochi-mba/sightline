package com.rextechnologies.sightline.design

import androidx.compose.ui.graphics.Color

/**
 * The REX Technologies ink/signal palette.
 *
 * The same values as the Windows app's theme, Flint's and LUCY's site. They are asserted here by
 * `RexTokenTest`, because drift between codebases is exactly the kind of defect no compiler catches:
 * nothing fails to build, the product simply stops looking like one product.
 *
 * Do not re-pick them, and do not add a light variant. REX is a dark system by design, and a washed
 * out light mode would break the instrument character the palette exists to create.
 *
 * [Muted] is the desktop's `#858D83`, not Flint's television value, which is lifted for legibility
 * across a room and reads as washed out at arm's length.
 */
object RexColors {
    /** Page ground. Near-black with a green cast, never pure black. Also the system bar colour. */
    val Ink: Color = Color(0xFF080A09)

    /** Card surface, one step off the ground. */
    val Panel: Color = Color(0xFF111512)

    /** Nested surface, for a panel inside a panel: the inset advisory block. */
    val Raised: Color = Color(0xFF181E19)

    /** Hairline borders and dividers. Always 1dp, never heavier. */
    val Line: Color = Color(0xFF29302A)

    /** Primary text. Warm off-white, never pure white. */
    val Text: Color = Color(0xFFF2F5EE)

    /** Secondary text, labels, and anything inactive. */
    val Muted: Color = Color(0xFF858D83)

    /** The accent. Ready states, primary actions, live values. Used sparingly; it earns attention. */
    val Signal: Color = Color(0xFFD7FF3F)

    /** Errors and blocked states. The only warm colour in the system. */
    val Live: Color = Color(0xFFFF774D)

    /** Signal at low opacity, for the fill behind a selected tab or under a sparkline. */
    val SignalWash: Color = Signal.copy(alpha = 0.10f)

    /** Live at low opacity, for the fill behind a failure row. */
    val LiveWash: Color = Live.copy(alpha = 0.10f)

    /** The scrim over content behind a sheet. */
    val Scrim: Color = Color(0xE6080A09)
}

/**
 * The status a component is carrying.
 *
 * Components take a tone rather than a colour, so a status can never be painted something off the
 * palette.
 */
enum class Tone {
    /** No claim: unprobed, unavailable, or simply not a status. */
    Neutral,

    /** The accent. Only a genuinely available thing earns it. */
    Signal,

    /** The failure tone. Blocked and impossible share it. */
    Live,

    /** Hairline only, for a card carrying no status at all. */
    Line,
}

/** The border and text colour a tone paints. */
val Tone.color: Color
    get() = when (this) {
        Tone.Neutral -> RexColors.Muted
        Tone.Signal -> RexColors.Signal
        Tone.Live -> RexColors.Live
        Tone.Line -> RexColors.Line
    }

/** The wash a tone fills with, for the rare component that fills rather than outlines. */
val Tone.wash: Color
    get() = when (this) {
        Tone.Neutral -> RexColors.Line.copy(alpha = 0.35f)
        Tone.Signal -> RexColors.SignalWash
        Tone.Live -> RexColors.LiveWash
        Tone.Line -> RexColors.Line.copy(alpha = 0.35f)
    }
