package com.rextechnologies.sightline.buildlogic

import org.gradle.api.Project
import org.gradle.api.artifacts.MinimalExternalModuleDependency
import org.gradle.api.artifacts.VersionCatalog
import org.gradle.api.artifacts.VersionCatalogsExtension
import org.gradle.api.provider.Provider
import org.gradle.kotlin.dsl.getByType

/** gradle/libs.versions.toml, where every version in the build is written. */
internal val Project.libs: VersionCatalog
    get() = extensions.getByType<VersionCatalogsExtension>().named("libs")

internal fun VersionCatalog.version(alias: String): String =
    findVersion(alias)
        .orElseThrow { IllegalStateException("gradle/libs.versions.toml has no version named '$alias'.") }
        .requiredVersion

internal fun VersionCatalog.library(alias: String): Provider<MinimalExternalModuleDependency> =
    findLibrary(alias)
        .orElseThrow { IllegalStateException("gradle/libs.versions.toml has no library named '$alias'.") }

/**
 * The JDK every Kotlin and Java compilation targets.
 *
 * From the catalog rather than a literal here, so the toolchain is written in the same place as
 * every other version in this build and cannot drift from it.
 */
internal val Project.javaVersion: Int
    get() = libs.version("jdk").toInt()

/**
 * VERSION at the repository root, the one version every build reads, .NET and Gradle alike.
 *
 * Read through a provider so the configuration cache tracks the file as an input.
 */
internal val Project.sightlineVersion: String
    get() = providers.fileContents(rootProject.layout.projectDirectory.file("VERSION")).asText.get().trim()
