package com.rextechnologies.sightline.buildlogic

import com.android.build.api.dsl.ApplicationExtension
import com.android.build.api.variant.ApplicationAndroidComponentsExtension
import com.android.build.api.variant.HostTestBuilder
import org.gradle.api.Plugin
import org.gradle.api.Project
import org.gradle.kotlin.dsl.configure
import org.gradle.kotlin.dsl.getByType

/**
 * `sightline.android-application`: the installable app, versioned from VERSION.
 *
 * `sightline.versionCode` may be passed by a release build; a local build is version code 1.
 */
class AndroidApplicationConventionPlugin : Plugin<Project> {
    override fun apply(target: Project) {
        with(target) {
            pluginManager.apply("com.android.application")
            pluginManager.apply(QualityConventionPlugin::class.java)

            val android = extensions.getByType<ApplicationExtension>()
            configureAndroid(android)
            android.defaultConfig.targetSdk = libs.version("target-sdk").toInt()
            android.defaultConfig.versionName = sightlineVersion
            android.defaultConfig.versionCode =
                providers.gradleProperty("sightline.versionCode").map(String::toInt).getOrElse(1)

            android.buildTypes {
                getByName("debug") {
                    applicationIdSuffix = ".debug"
                    versionNameSuffix = "-debug"
                }
                getByName("release") {
                    isMinifyEnabled = true
                    isShrinkResources = true
                    proguardFiles(
                        android.getDefaultProguardFile("proguard-android-optimize.txt"),
                        "proguard-rules.pro",
                    )
                }
            }

            extensions.configure<ApplicationAndroidComponentsExtension> {
                beforeVariants(selector().withBuildType(UNTESTED_BUILD_TYPE)) { variant ->
                    variant.hostTests[HostTestBuilder.UNIT_TEST_TYPE]?.enable = false
                }
            }
        }
    }
}
