package com.rextechnologies.sightline.sentry

import android.graphics.BitmapFactory
import com.rextechnologies.sightline.core.sentry.LumaGrid

/**
 * Turns a live-view JPEG into the small brightness grid Sentry looks at.
 *
 * The JPEG is decoded at a sixteenth of its size, which the decoder does by skipping most of the work: a
 * 640 by 360 frame becomes a 40 by 23 grid for a fraction of the cost of a full decode, five times a
 * second with the screen off.
 */
object LumaSampler {
    /** How much smaller than the frame the grid is, on each side. */
    const val SAMPLE = 16

    /** The grid for [jpeg], or null when it does not decode. */
    fun sample(jpeg: ByteArray): LumaGrid? {
        val options = BitmapFactory.Options().apply { inSampleSize = SAMPLE }
        val bitmap = BitmapFactory.decodeByteArray(jpeg, 0, jpeg.size, options) ?: return null
        try {
            val pixels = IntArray(bitmap.width * bitmap.height)
            bitmap.getPixels(pixels, 0, bitmap.width, 0, 0, bitmap.width, bitmap.height)
            return LumaGrid(bitmap.width, bitmap.height, IntArray(pixels.size) { luma(pixels[it]) })
        } finally {
            bitmap.recycle()
        }
    }

    /** A colour's brightness, 0 to 255, weighted as the eye weights red, green and blue. */
    fun luma(argb: Int): Int {
        val red = argb shr 16 and 0xFF
        val green = argb shr 8 and 0xFF
        val blue = argb and 0xFF
        return (299 * red + 587 * green + 114 * blue) / 1000
    }
}
