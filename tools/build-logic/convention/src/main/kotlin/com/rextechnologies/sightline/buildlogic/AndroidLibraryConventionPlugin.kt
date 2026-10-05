package com.rextechnologies.sightline.buildlogic

import com.android.build.api.dsl.LibraryExtension
import com.android.build.api.variant.HostTestBuilder
import com.android.build.api.variant.LibraryAndroidComponentsExtension
import org.gradle.api.Plugin
import org.gradle.api.Project
import org.gradle.kotlin.dsl.configure
import org.gradle.kotlin.dsl.getByType

/**
 * `sightline.android-library`: an Android module the app is built from, such as the design system.
 *
 * The same SDKs, Java level, lint and Robolectric setup as the app, from the same function, so a
 * library can never be built against something the app that ships it is not.
 */
class AndroidLibraryConventionPlugin : Plugin<Project> {
    override fun apply(target: Project) {
        with(target) {
            pluginManager.apply("com.android.library")
            pluginManager.apply(QualityConventionPlugin::class.java)

            val android = extensions.getByType<LibraryExtension>()
            configureAndroid(android)
            // A library has no target SDK of its own. Its tests and its lint are given the app's, so
            // they judge it by the platform behaviour the app actually opts into.
            android.testOptions.targetSdk = libs.version("target-sdk").toInt()
            android.lint.targetSdk = libs.version("target-sdk").toInt()

            extensions.configure<LibraryAndroidComponentsExtension> {
                beforeVariants(selector().withBuildType(UNTESTED_BUILD_TYPE)) { variant ->
                    variant.hostTests[HostTestBuilder.UNIT_TEST_TYPE]?.enable = false
                }
            }
        }
    }
}
