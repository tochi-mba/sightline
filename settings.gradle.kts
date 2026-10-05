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

// The Android app's pure-JVM logic, and the design system it draws with, under apps/android.
// ":android" is pointed at apps/android too, because Gradle refuses a project whose directory does not
// exist.
include(":android:core", ":android:design")
project(":android").projectDir = file("apps/android")
project(":android:core").projectDir = file("apps/android/core")
project(":android:design").projectDir = file("apps/android/design")
