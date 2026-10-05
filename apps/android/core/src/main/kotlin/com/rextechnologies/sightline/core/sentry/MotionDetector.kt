package com.rextechnologies.sightline.core.sentry

import kotlin.math.abs

/**
 * A small picture of brightness: [width] by [height] cells, each 0 (black) to 255 (white).
 *
 * Sentry looks at the live view through one of these rather than at full pictures: a 40 by 23 grid of
 * a 640 by 360 frame is fine enough to see a person cross it and coarse enough that sensor noise and
 * JPEG blocks average out.
 */
class LumaGrid(val width: Int, val height: Int, val cells: IntArray) {
    init {
        require(width > 0 && height > 0) { "A grid needs at least one cell." }
        require(cells.size == width * height) {
            "A $width by $height grid has ${width * height} cells, not ${cells.size}."
        }
        require(cells.all { it in 0..255 }) { "Brightness runs from 0 to 255." }
    }
}

/**
 * Which cells of the picture Sentry watches.
 *
 * A doorway, not the road beyond it: cars passing the window are not what a person arming Sentry wants
 * to be told about.
 */
class Zone(val width: Int, val height: Int, private val watched: BooleanArray) {
    init {
        require(watched.size == width * height) { "A $width by $height zone has ${width * height} cells." }
        require(watched.any { it }) { "A zone must watch at least one cell." }
    }

    /** Whether the cell at [index] is watched. */
    operator fun contains(index: Int): Boolean = watched[index]

    /** How many cells are watched. */
    val size: Int get() = watched.count { it }

    companion object {
        /** Every cell of a [width] by [height] picture. */
        fun all(width: Int, height: Int): Zone = Zone(width, height, BooleanArray(width * height) { true })

        /**
         * The cells inside a rectangle given as fractions of the picture, 0 to 1 from the top left: what
         * a person drags out on the screen.
         */
        fun rectangle(width: Int, height: Int, left: Double, top: Double, right: Double, bottom: Double): Zone {
            val inside = listOf(left, top, right, bottom).all { it in 0.0..1.0 }
            require(inside && left < right && top < bottom) { "A zone is a rectangle inside the picture." }

            return Zone(
                width,
                height,
                BooleanArray(width * height) { index ->
                    val x = (index % width + 0.5) / width
                    val y = (index / width + 0.5) / height
                    x in left..right && y in top..bottom
                },
            )
        }
    }
}

/**
 * How much it takes to count as motion.
 *
 * @property cellChange How far a cell's brightness must move from the learnt background, out of 255.
 * @property changedFraction What share of the watched cells must change at once.
 */
enum class Sensitivity(val cellChange: Int, val changedFraction: Double) {
    /** A person close to the camera; ignores pets, leaves and rain. */
    Low(40, 0.08),

    /** A person anywhere in view. */
    Medium(28, 0.03),

    /** Anything that moves, a cat included. */
    High(18, 0.01),
}

/**
 * Tells motion from a still scene, a few times a second, with no machine learning.
 *
 * It keeps a background: a slowly updated average of what each cell usually looks like, so a scene that
 * changes slowly (the sun moving, a light warming up) is learnt rather than reported. A sudden change
 * across the whole picture at once (the camera adjusting its exposure, a cloud) moves every cell by
 * about the same amount; that shared shift is taken out before cells are compared, so it is not
 * reported either. What is left, a part of the picture changing on its own, is motion.
 */
class MotionDetector(private val sensitivity: Sensitivity, private val zone: Zone? = null) {
    private var background: DoubleArray? = null
    private var watched = IntArray(0)

    /**
     * Compares [grid] with the background, then learns from it.
     *
     * @return The share of watched cells that changed, 0 to 1; the first grid, and one of a different
     *   size, only start the background and return 0.
     * @throws IllegalArgumentException The zone was drawn on a picture of a different size.
     */
    fun observe(grid: LumaGrid): Double {
        require(zone == null || (zone.width == grid.width && zone.height == grid.height)) {
            "The zone was drawn on a picture of a different size."
        }

        val learnt = background
        if (learnt == null || learnt.size != grid.cells.size) {
            background = DoubleArray(grid.cells.size) { grid.cells[it].toDouble() }
            watched = grid.cells.indices.filter { zone == null || it in zone }.toIntArray()
            return 0.0
        }

        var shift = 0.0
        for (index in watched) {
            shift += grid.cells[index] - learnt[index]
        }
        shift /= watched.size

        var changed = 0
        for (index in watched) {
            if (abs(grid.cells[index] - learnt[index] - shift) >= sensitivity.cellChange) {
                changed++
            }
        }

        for (index in learnt.indices) {
            learnt[index] += LEARNING_RATE * (grid.cells[index] - learnt[index])
        }

        return changed.toDouble() / watched.size
    }

    /** Whether [score], from [observe], is motion at this sensitivity. */
    fun isMotion(score: Double): Boolean = score >= sensitivity.changedFraction

    /** Forgets the background, so the next grid starts it again: after the camera moved, say. */
    fun reset() {
        background = null
    }

    private companion object {
        /**
         * How much of each grid the background takes in. At five grids a second the background follows a
         * change that stays in a few seconds, and a person walking through is long gone by then.
         */
        const val LEARNING_RATE = 0.05
    }
}
