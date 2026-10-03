package com.rextechnologies.sightline.core.sentry

import kotlin.test.Test
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class MotionDetectorTest {
    @Test
    fun `first frame warms up and small brightness changes are not motion`() {
        val detector = MotionDetector(pixelThreshold = 20, changedFraction = 0.5)

        assertFalse(detector.observe(byteArrayOf(0, 20, 40, 60)))
        assertFalse(detector.observe(byteArrayOf(19, 1, 59, 41)))
    }

    @Test
    fun `motion begins at the configured changed pixel fraction`() {
        val detector = MotionDetector(pixelThreshold = 20, changedFraction = 0.5)
        detector.observe(byteArrayOf(0, 0, 0, 0))

        assertTrue(detector.observe(byteArrayOf(20, 20, 0, 0)))
    }

    @Test
    fun `unsigned luminance and a changed sample size are handled`() {
        val detector = MotionDetector(pixelThreshold = 20, changedFraction = 0.5)
        detector.observe(byteArrayOf((-1).toByte(), 0))
        assertTrue(detector.observe(byteArrayOf(0, (-1).toByte())))
        assertFalse(detector.observe(byteArrayOf(0)))
    }

    @Test
    fun `reset requires another warm-up frame`() {
        val detector = MotionDetector()
        detector.observe(byteArrayOf(0, 0))
        detector.reset()

        assertFalse(detector.observe(byteArrayOf(100, 100)))
    }

    @Test
    fun `invalid configuration and empty observations are refused`() {
        assertFailsWith<IllegalArgumentException> { MotionDetector(pixelThreshold = 0) }
        assertFailsWith<IllegalArgumentException> { MotionDetector(pixelThreshold = 256) }
        assertFailsWith<IllegalArgumentException> { MotionDetector(changedFraction = -0.1) }
        assertFailsWith<IllegalArgumentException> { MotionDetector(changedFraction = 1.1) }
        assertFailsWith<IllegalArgumentException> { MotionDetector().observe(byteArrayOf()) }
    }
}
