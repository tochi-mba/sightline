package com.rextechnologies.sightline.buildlogic

import com.android.build.api.dsl.ApplicationExtension
import com.android.build.api.variant.ApplicationAndroidComponentsExtension
import com.android.build.api.variant.HostTestBuilder
import org.gradle.api.JavaVersion
import org.gradle.api.Plugin
import org.gradle.api.Project
import org.gradle.kotlin.dsl.configure
import org.gradle.kotlin.dsl.getByType
import org.gradle.kotlin.dsl.withType
import org.jetbrains.kotlin.gradle.tasks.KotlinCompilationTask

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
            android.compileSdk = libs.version("compile-sdk").toInt()
            android.defaultConfig.minSdk = libs.version("min-sdk").toInt()
            android.defaultConfig.targetSdk = libs.version("target-sdk").toInt()
            android.defaultConfig.versionName = sightlineVersion
            android.defaultConfig.versionCode =
                providers.gradleProperty("sightline.versionCode").map(String::toInt).getOrElse(1)
            android.defaultConfig.testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
            android.compileOptions.sourceCompatibility = JavaVersion.toVersion(javaVersion)
            android.compileOptions.targetCompatibility = JavaVersion.toVersion(javaVersion)
            // Off: generating BuildConfig adds a Javac task for no source.
            android.buildFeatures.buildConfig = false
            // Both report that something newer was published, so a commit could pass one day and fail
            // the next. Upgrades arrive as reviewable pull requests instead.
            android.lint.disable += setOf("GradleDependency", "OutdatedLibrary", "NewerVersionAvailable")

            android.testOptions.unitTests.apply {
                // Robolectric needs merged resources and the manifest to inflate anything.
                isIncludeAndroidResources = true
                all { test -> test.jvmArgs(ROBOLECTRIC_OPENS) }
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

            // Unit tests run against debug only: release differs by R8 and signing, which a unit test
            // does not exercise.
            extensions.configure<ApplicationAndroidComponentsExtension> {
                beforeVariants(selector().withBuildType("release")) { variant ->
                    variant.hostTests[HostTestBuilder.UNIT_TEST_TYPE]?.enable = false
                }
            }

            tasks.withType<KotlinCompilationTask<*>>().configureEach {
                compilerOptions.allWarningsAsErrors.set(true)
            }
        }
    }

    private companion object {
        /** Robolectric instruments the platform reflectively; on JDK 17 that needs these opened. */
        val ROBOLECTRIC_OPENS = listOf(
            "--add-opens=java.base/java.lang=ALL-UNNAMED",
            "--add-opens=java.base/java.util=ALL-UNNAMED",
            "--add-opens=java.base/java.io=ALL-UNNAMED",
            "--add-opens=java.base/java.net=ALL-UNNAMED",
        )
    }
}
