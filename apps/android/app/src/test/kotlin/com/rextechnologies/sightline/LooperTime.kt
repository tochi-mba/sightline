package com.rextechnologies.sightline

import android.os.SystemClock
import kotlin.time.Duration
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.TimeMark
import kotlin.time.TimeSource

/**
 * The main thread's clock, as Robolectric runs it: it moves only when a test idles the looper for a
 * while, so frame rates and Sentry's timings are measured on the same clock the controller's delays use.
 */
object LooperTime : TimeSource {
    override fun markNow(): TimeMark = Mark(SystemClock.uptimeMillis())

    private class Mark(private val at: Long) : TimeMark {
        override fun elapsedNow(): Duration = (SystemClock.uptimeMillis() - at).milliseconds

        override fun plus(duration: Duration): TimeMark = Mark(at + duration.inWholeMilliseconds)
    }
}
