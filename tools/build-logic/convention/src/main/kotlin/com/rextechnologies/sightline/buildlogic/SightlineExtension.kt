package com.rextechnologies.sightline.buildlogic

import org.gradle.api.Project
import javax.inject.Inject

/** The `sightline { }` block: the decisions a module makes that the conventions cannot make for it. */
abstract class SightlineExtension @Inject constructor(private val project: Project) {
    /**
     * Holds this module's tests to a coverage floor, which `check` verifies.
     *
     * For pure JVM modules; the Android app uses [appCoverageFloor].
     *
     * @param excludes class-file patterns left out of the measurement, each for a reason stated where
     *   it is passed.
     */
    fun coverageFloor(line: Double, branch: Double, excludes: List<String> = emptyList()) {
        project.configureCoverageFloor(line, branch, excludes)
    }

    /**
     * Holds the app's Robolectric tests to coverage floors, which `check` verifies.
     *
     * For the Android application module: the logic by line and branch, the Compose screens by line.
     */
    fun appCoverageFloor(logicLine: Double, logicBranch: Double, screenLine: Double) {
        project.configureAppCoverage(logicLine, logicBranch, screenLine)
    }
}
