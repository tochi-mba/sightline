package com.rextechnologies.sightline.buildlogic

import com.android.build.api.dsl.CommonExtension
import org.gradle.api.Plugin
import org.gradle.api.Project

/** `sightline.compose`: the Compose compiler, for an Android module that draws with Compose. */
class ComposeConventionPlugin : Plugin<Project> {
    override fun apply(target: Project) {
        with(target) {
            pluginManager.apply("org.jetbrains.kotlin.plugin.compose")
            // An application or a library such as a design system; whichever the module turns out to be.
            for (android in listOf("com.android.application", "com.android.library")) {
                pluginManager.withPlugin(android) {
                    (extensions.getByName("android") as CommonExtension).buildFeatures.compose = true
                }
            }
        }
    }
}
