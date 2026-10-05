package com.rextechnologies.sightline

import android.app.Application

/** The process: makes the [AppGraph] that everything else shares. */
class SightlineApplication : Application() {
    /** Made on first use, so a test can install its own before anything asks for it. */
    var graph: AppGraph
        get() = installed ?: AppGraph(this).also { installed = it }
        set(value) {
            installed = value
        }

    private var installed: AppGraph? = null
}
