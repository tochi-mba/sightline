package com.rextechnologies.sightline.buildlogic

import org.gradle.api.Plugin
import org.gradle.api.Project
import org.gradle.api.tasks.JavaExec
import org.gradle.api.tasks.PathSensitivity
import org.gradle.kotlin.dsl.create
import org.gradle.kotlin.dsl.register

/** `sightline.quality`: the `sightline { }` block, and ktlint wired into `check`, for every Kotlin module. */
class QualityConventionPlugin : Plugin<Project> {
    override fun apply(target: Project) {
        with(target) {
            extensions.create<SightlineExtension>("sightline")

            val ktlintDependencies = configurations.dependencyScope("ktlint")
            val ktlintClasspath = configurations.resolvable("ktlintClasspath") {
                extendsFrom(ktlintDependencies.get())
            }
            dependencies.add(ktlintDependencies.name, libs.library("ktlint-cli"))

            val ktlintCheck = tasks.register<KtlintCheckTask>("ktlintCheck") {
                group = "verification"
                description = "Checks the layout of this module's Kotlin."
                setClasspath(files(ktlintClasspath))
                mainClass.set(KTLINT_MAIN)
                // The glob is relative to the working directory, which Gradle does not otherwise
                // promise is this module.
                setWorkingDir(projectDir)
                args("src/**/*.kt", "--reporter=plain", "--relative")
                // Declared so Gradle skips the check when nothing it reads has changed, and cannot skip
                // it when something has.
                inputs.files(fileTree("src") { include("**/*.kt") }).withPathSensitivity(PathSensitivity.RELATIVE)
                inputs.file(rootProject.file(".editorconfig"))
                marker.set(layout.buildDirectory.file("ktlint/passed.txt"))
            }

            tasks.register<JavaExec>("ktlintFormat") {
                group = "formatting"
                description = "Rewrites the layout of this module's Kotlin to what ktlintCheck expects."
                setClasspath(files(ktlintClasspath))
                mainClass.set(KTLINT_MAIN)
                setWorkingDir(projectDir)
                args("-F", "src/**/*.kt", "--relative")
            }

            tasks.matching { it.name == "check" }.configureEach {
                dependsOn(ktlintCheck)
            }
        }
    }

    private companion object {
        const val KTLINT_MAIN = "com.pinterest.ktlint.Main"
    }
}
