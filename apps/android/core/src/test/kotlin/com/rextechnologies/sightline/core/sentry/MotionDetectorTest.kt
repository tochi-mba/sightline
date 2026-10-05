package com.rextechnologies.sightline.core.sentry

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Telling motion from a still, a slowly changing and a suddenly brighter scene. */
class MotionDetectorTest {
    private val width = 10
    private val height = 10

    /** A scene of one brightness, with an optional square of another standing for something in view. */
    private fun scene(brightness: Int, square: Int = 0, squareBrightness: Int = brightness, at: Int = 0): LumaGrid =
        LumaGrid(
            width,
            height,
            IntArray(width * height) { index ->
                val x = index % width
                val y = index / width
                if (x in at until at + square && y in at until at + square) squareBrightness else brightness
            },
        )

    @Test
    fun `the first picture only starts the background`() {
        val detector = MotionDetector(Sensitivity.Medium)

        assertEquals(0.0, detector.observe(scene(100, square = 5, squareBrightness = 250)))
    }

    @Test
    fun `a still scene is not motion`() {
        val detector = MotionDetector(Sensitivity.High)
        detector.observe(scene(100))

        val score = detector.observe(scene(100))

        assertEquals(0.0, score)
        assertFalse(detector.isMotion(score))
    }

    @Test
    fun `something appearing in part of the picture is motion`() {
        val detector = MotionDetector(Sensitivity.Medium)
        detector.observe(scene(100))

        val score = detector.observe(scene(100, square = 3, squareBrightness = 200))

        assertEquals(0.09, score, 1e-9)
        assertTrue(detector.isMotion(score))
    }

    @Test
    fun `the whole picture getting brighter at once is the camera's exposure, not motion`() {
        val detector = MotionDetector(Sensitivity.High)
        detector.observe(scene(100))

        assertEquals(0.0, detector.observe(scene(160)))
    }

    @Test
    fun `a change that stays is learnt, and stops counting as motion`() {
        val detector = MotionDetector(Sensitivity.Medium)
        detector.observe(scene(100))

        val first = detector.observe(scene(100, square = 3, squareBrightness = 200))
        repeat(100) { detector.observe(scene(100, square = 3, squareBrightness = 200)) }
        val later = detector.observe(scene(100, square = 3, squareBrightness = 200))

        assertTrue(detector.isMotion(first))
        assertFalse(detector.isMotion(later))
    }

    @Test
    fun `each sensitivity needs more or less of the picture to change`() {
        fun scoreOf(sensitivity: Sensitivity, square: Int, change: Int): Boolean {
            val detector = MotionDetector(sensitivity)
            detector.observe(scene(100))
            return detector.isMotion(detector.observe(scene(100, square = square, squareBrightness = 100 + change)))
        }

        // One cell changing by a fifth: enough for High only.
        assertTrue(scoreOf(Sensitivity.High, square = 1, change = 50))
        assertFalse(scoreOf(Sensitivity.Medium, square = 1, change = 50))
        // A sixth of the picture brightening by forty: enough for Medium; Low needs a stronger change.
        assertTrue(scoreOf(Sensitivity.Medium, square = 4, change = 40))
        assertFalse(scoreOf(Sensitivity.Low, square = 4, change = 40))
        assertTrue(scoreOf(Sensitivity.Low, square = 4, change = 60))
    }

    @Test
    fun `motion outside the zone is ignored and inside it counts in full`() {
        val doorway = Zone.rectangle(width, height, left = 0.5, top = 0.5, right = 1.0, bottom = 1.0)
        val detector = MotionDetector(Sensitivity.Medium, doorway)
        detector.observe(scene(100))

        val outside = detector.observe(scene(100, square = 4, squareBrightness = 220))
        val inside = detector.observe(scene(100, square = 2, squareBrightness = 220, at = 6))

        assertEquals(0.0, outside)
        assertEquals(4.0 / 25, inside, 1e-9)
        assertEquals(25, doorway.size)
    }

    @Test
    fun `a picture of a new size, or a reset, starts the background again`() {
        val detector = MotionDetector(Sensitivity.High)
        detector.observe(scene(100))

        assertEquals(0.0, detector.observe(LumaGrid(2, 2, IntArray(4) { 250 })))
        detector.reset()
        assertEquals(0.0, detector.observe(scene(30)))
    }

    @Test
    fun `a zone drawn on another picture size is refused`() {
        val detector = MotionDetector(Sensitivity.Medium, Zone.all(4, 4))

        assertFailsWith<IllegalArgumentException> { detector.observe(scene(100)) }
        assertFailsWith<IllegalArgumentException> {
            MotionDetector(Sensitivity.Medium, Zone.all(10, 4)).observe(scene(1))
        }
    }

    @Test
    fun `grids and zones that cannot exist are refused`() {
        assertFailsWith<IllegalArgumentException> { LumaGrid(0, 1, IntArray(0)) }
        assertFailsWith<IllegalArgumentException> { LumaGrid(1, 0, IntArray(0)) }
        assertFailsWith<IllegalArgumentException> { LumaGrid(2, 2, IntArray(3)) }
        assertFailsWith<IllegalArgumentException> { LumaGrid(1, 1, intArrayOf(256)) }
        assertFailsWith<IllegalArgumentException> { LumaGrid(1, 1, intArrayOf(-1)) }
        assertFailsWith<IllegalArgumentException> { Zone(2, 2, BooleanArray(3)) }
        assertFailsWith<IllegalArgumentException> { Zone(2, 2, BooleanArray(4)) }
        assertFailsWith<IllegalArgumentException> { Zone.rectangle(4, 4, 0.5, 0.0, 0.5, 1.0) }
        assertFailsWith<IllegalArgumentException> { Zone.rectangle(4, 4, 0.0, 0.6, 1.0, 0.5) }
        assertFailsWith<IllegalArgumentException> { Zone.rectangle(4, 4, -0.1, 0.0, 1.0, 1.0) }
        assertFailsWith<IllegalArgumentException> { Zone.rectangle(4, 4, 0.0, 0.0, 1.1, 1.0) }
        assertEquals(16, Zone.all(4, 4).size)
    }

    @Test
    fun `a zone in the middle of the picture leaves every edge out`() {
        val middle = Zone.rectangle(width, height, left = 0.25, top = 0.25, right = 0.75, bottom = 0.75)

        // Cell centres from 0.25 to 0.75 inclusive: columns and rows 2 to 7.
        assertEquals(36, middle.size)
        assertTrue(0 !in middle)
        assertTrue(99 !in middle)
        assertTrue(55 in middle)
    }
}
