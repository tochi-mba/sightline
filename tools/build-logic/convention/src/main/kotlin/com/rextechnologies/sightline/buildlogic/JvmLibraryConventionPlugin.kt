package com.rextechnologies.sightline.buildlogic

import org.gradle.api.Plugin
import org.gradle.api.Project
import org.gradle.api.tasks.testing.Test
import org.gradle.kotlin.dsl.configure
import org.gradle.kotlin.dsl.withType
import org.jetbrains.kotlin.gradle.dsl.KotlinJvmProjectExtension

/** `sightline.jvm-library`: a Kotlin module with no Android in it, testable without Robolectric. */
class JvmLibraryConventionPlugin : Plugin<Project> {
    override fun apply(target: Project) {
        with(target) {
            pluginManager.apply("org.jetbrains.kotlin.jvm")
            pluginManager.apply(QualityConventionPlugin::class.java)

            extensions.configure<KotlinJvmProjectExtension> {
                jvmToolchain(javaVersion)
                compilerOptions.allWarningsAsErrors.set(true)
            }
            tasks.withType<Test>().configureEach {
                useJUnit()
            }
        }
    }
}
