pluginManagement {
    // The convention plugins every Kotlin module applies. An included build rather than buildSrc,
    // which Gradle puts on the classpath of every project whether it uses the plugins or not.
    includeBuild("tools/build-logic")
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

plugins {
    // Provisions the JDK a module's toolchain asks for when this machine does not have it.
    id("org.gradle.toolchains.foojay-resolver-convention") version "1.0.0"
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "Sightline"

// Gradle paths follow the product and folders follow the repository: the Kotlin protocol sits under
// protocol/ beside the .NET one, and reads the same golden vectors.
include(":protocol")
project(":protocol").projectDir = file("protocol/kotlin")

// The Android app joins as :android:app, :android:core and :android:design, in apps/android/app,
// apps/android/core and apps/android/design. Point ":android" at apps/android too when they arrive,
// because Gradle refuses a project whose directory does not exist.
