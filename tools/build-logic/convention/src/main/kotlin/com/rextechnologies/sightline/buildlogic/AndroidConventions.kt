package com.rextechnologies.sightline.buildlogic

import com.android.build.api.dsl.CommonExtension
import org.gradle.api.JavaVersion
import org.gradle.api.Project
import org.gradle.kotlin.dsl.withType
import org.jetbrains.kotlin.gradle.tasks.KotlinCompilationTask

/**
 * What every Android module shares: the app, and the libraries it is built from.
 *
 * Written once so the application and library plugins cannot drift. A library compiled against a
 * different SDK or Java level from the app that ships it fails a long way from its cause. A module's
 * own build file then says only what is true of that module: its namespace and its dependencies.
 */
internal fun Project.configureAndroid(android: CommonExtension) {
    android.compileSdk = libs.version("compile-sdk").toInt()
    android.defaultConfig.minSdk = libs.version("min-sdk").toInt()
    android.defaultConfig.testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    android.compileOptions.sourceCompatibility = JavaVersion.toVersion(javaVersion)
    android.compileOptions.targetCompatibility = JavaVersion.toVersion(javaVersion)
    // Off: generating BuildConfig adds a Javac task for no source.
    android.buildFeatures.buildConfig = false
    // Both report that something newer was published, so a commit could pass one day and fail the
    // next. Upgrades arrive as reviewable pull requests instead.
    android.lint.disable += setOf("GradleDependency", "OutdatedLibrary", "NewerVersionAvailable")

    android.testOptions.unitTests.apply {
        // Robolectric needs merged resources and the manifest to inflate anything.
        isIncludeAndroidResources = true
        all { test -> test.jvmArgs(ROBOLECTRIC_OPENS) }
    }

    tasks.withType<KotlinCompilationTask<*>>().configureEach {
        compilerOptions.allWarningsAsErrors.set(true)
    }
}

/**
 * The build type whose unit tests are switched off. Release differs from debug by R8 and signing,
 * which a unit test does not exercise, so running the suite again for it would prove nothing new.
 */
internal const val UNTESTED_BUILD_TYPE = "release"

/** Robolectric instruments the platform reflectively; on JDK 17 that needs these opened. */
private val ROBOLECTRIC_OPENS = listOf(
    "--add-opens=java.base/java.lang=ALL-UNNAMED",
    "--add-opens=java.base/java.util=ALL-UNNAMED",
    "--add-opens=java.base/java.io=ALL-UNNAMED",
    "--add-opens=java.base/java.net=ALL-UNNAMED",
)
