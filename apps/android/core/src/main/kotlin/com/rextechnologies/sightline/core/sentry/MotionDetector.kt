package com.rextechnologies.sightline.core.sentry

import kotlin.math.abs

/**
 * A small, deterministic motion detector over down-sampled luminance.
 *
 * Android decodes a frame and supplies one luma byte per sample. Keeping this class platform-free
 * makes the threshold and warm-up behaviour fully testable. Motion is the fraction of samples whose
 * brightness changed by at least [pixelThreshold].
 */
class MotionDetector(
    private val pixelThreshold: Int = 24,
    private val changedFraction: Double = 0.18,
) {
    private var previous: ByteArray? = null

    init {
        require(pixelThreshold in 1..255) { "The pixel threshold must be between 1 and 255." }
        require(changedFraction in 0.0..1.0) { "The changed fraction must be between 0 and 1." }
    }

    fun observe(luma: ByteArray): Boolean {
        require(luma.isNotEmpty()) { "A luminance sample cannot be empty." }
        val before = previous
        previous = luma.copyOf()
        if (before == null || before.size != luma.size) return false

        val changed = luma.indices.count { index ->
            abs(luma[index].toUByte().toInt() - before[index].toUByte().toInt()) >= pixelThreshold
        }
        return changed.toDouble() / luma.size >= changedFraction
    }

    fun reset() {
        previous = null
    }
}
