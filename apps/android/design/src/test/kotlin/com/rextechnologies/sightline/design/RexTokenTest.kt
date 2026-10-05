package com.rextechnologies.sightline.design

import androidx.compose.ui.graphics.toArgb
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The REX palette, held to the same values as the Windows app's theme and the site's stylesheet. A wrong
 * value compiles and runs; only this notices that the product stopped looking like one product.
 */
class RexTokenTest {
    private fun hex(colour: androidx.compose.ui.graphics.Color) = "#%08X".format(colour.toArgb())

    @Test
    fun `the palette is the REX one`() {
        assertEquals("#FF080A09", hex(RexColors.Ink))
        assertEquals("#FF111512", hex(RexColors.Panel))
        assertEquals("#FF181E19", hex(RexColors.Raised))
        assertEquals("#FF29302A", hex(RexColors.Line))
        assertEquals("#FFF2F5EE", hex(RexColors.Text))
        assertEquals("#FF858D83", hex(RexColors.Muted))
        assertEquals("#FFD7FF3F", hex(RexColors.Signal))
        assertEquals("#FFFF774D", hex(RexColors.Live))
    }

    @Test
    fun `tones paint the palette and nothing else`() {
        assertEquals(RexColors.Muted, Tone.Neutral.color)
        assertEquals(RexColors.Signal, Tone.Signal.color)
        assertEquals(RexColors.Live, Tone.Live.color)
        assertEquals(RexColors.Line, Tone.Line.color)
        assertEquals(RexColors.SignalWash, Tone.Signal.wash)
        assertEquals(RexColors.LiveWash, Tone.Live.wash)
    }

    @Test
    fun `nothing is smaller than a thumb can hit`() {
        assertEquals(48f, RexSpace.TouchTarget.value)
    }
}
