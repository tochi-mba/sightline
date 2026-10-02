package com.rextechnologies.sightline.buildlogic

import org.gradle.api.file.RegularFileProperty
import org.gradle.api.tasks.JavaExec
import org.gradle.api.tasks.OutputFile
import org.gradle.api.tasks.TaskAction

/**
 * Runs the ktlint command-line tool and records a pass, so an unchanged module is not linted again.
 *
 * The command line rather than a Gradle plugin: a plugin has to track both Gradle and Kotlin releases
 * to keep working, while the command-line distribution only has to be on a classpath, so the lint
 * gate can never be what breaks an upgrade.
 */
abstract class KtlintCheckTask : JavaExec() {
    @get:OutputFile
    abstract val marker: RegularFileProperty

    @TaskAction
    override fun exec() {
        super.exec()
        marker.get().asFile.apply { parentFile.mkdirs() }.writeText("ok\n")
    }
}
