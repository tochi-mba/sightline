package com.rextechnologies.sightline.live

import android.graphics.Bitmap
import android.graphics.BitmapFactory

/**
 * Decodes live-view JPEGs into bitmaps, reusing the memory of earlier ones.
 *
 * At twelve frames a second a fresh 640 by 360 bitmap per frame is about eleven megabytes a second of
 * garbage, which on a phone is collection pauses, dropped frames and battery. So frames are decoded into
 * a ring of three bitmaps instead: the one on screen, the one before it that the screen may still be
 * drawing, and the one being filled. A frame of a new size cannot reuse an old bitmap, and gets a new one
 * that then joins the ring.
 *
 * Used from one thread at a time.
 */
class FrameDecoder {
    private val ring = arrayOfNulls<Bitmap>(RING)
    private var next = 0

    /** [jpeg] as a bitmap, or null when it does not decode. */
    fun decode(jpeg: ByteArray): Bitmap? {
        val reused = ring[next]
        val bitmap = try {
            BitmapFactory.decodeByteArray(jpeg, 0, jpeg.size, options(reused))
        } catch (tooSmall: IllegalArgumentException) {
            // The bitmap offered for reuse is smaller than this frame: the camera changed resolution.
            BitmapFactory.decodeByteArray(jpeg, 0, jpeg.size, options(null))
        } ?: return null

        ring[next] = bitmap
        next = (next + 1) % RING
        return bitmap
    }

    private fun options(reuse: Bitmap?) = BitmapFactory.Options().apply {
        inMutable = true
        inBitmap = reuse
    }

    private companion object {
        /** On screen, possibly still being drawn, and being filled. */
        const val RING = 3
    }
}
