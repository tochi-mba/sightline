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
 * A release build may pass `sightline.versionCode`, and `sightline.versionName` for a rolling build's longer
 * name; a local build is version code 1 named VERSION. A release is signed when all four of
 * `SIGHTLINE_KEYSTORE_PATH`, `SIGHTLINE_KEYSTORE_PASSWORD`, `SIGHTLINE_KEY_ALIAS` and `SIGHTLINE_KEY_PASSWORD`
 * are set; otherwise it is built unsigned, and the release workflow refuses to publish it.
 */
class AndroidApplicationConventionPlugin : Plugin<Project> {
    override fun apply(target: Project) {
        with(target) {
            pluginManager.apply("com.android.application")
            pluginManager.apply(QualityConventionPlugin::class.java)

            val android = extensions.getByType<ApplicationExtension>()
            configureAndroid(android)
            android.defaultConfig.targetSdk = libs.version("target-sdk").toInt()
            android.defaultConfig.versionName =
                providers.gradleProperty("sightline.versionName").getOrElse(sightlineVersion)
            android.defaultConfig.versionCode =
                providers.gradleProperty("sightline.versionCode").map(String::toInt).getOrElse(1)

            val signing = listOf(
                "SIGHTLINE_KEYSTORE_PATH",
                "SIGHTLINE_KEYSTORE_PASSWORD",
                "SIGHTLINE_KEY_ALIAS",
                "SIGHTLINE_KEY_PASSWORD",
            ).map { providers.environmentVariable(it).orNull }
            if (signing.all { !it.isNullOrEmpty() }) {
                val (keystore, storePassword, alias, keyPassword) = signing.map { it!! }
                android.signingConfigs.create("release") {
                    storeFile = file(keystore)
                    this.storePassword = storePassword
                    keyAlias = alias
                    this.keyPassword = keyPassword
                }
                android.buildTypes.getByName("release").signingConfig = android.signingConfigs.getByName("release")
            }

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
