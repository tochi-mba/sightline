package com.rextechnologies.sightline.buildlogic

import org.gradle.api.Project
import org.gradle.api.tasks.testing.Test
import org.gradle.kotlin.dsl.configure
import org.gradle.kotlin.dsl.named
import org.gradle.testing.jacoco.plugins.JacocoPluginExtension
import org.gradle.testing.jacoco.tasks.JacocoCoverageVerification
import org.gradle.testing.jacoco.tasks.JacocoReport

/**
 * Classes the Kotlin compiler generates that have no source line a test could reach: `when` lookup
 * tables and interface default-method holders. Code somebody wrote is never left out this way; a
 * branch no test can reach is deleted instead.
 */
internal val SYNTHETIC_CLASSES = listOf("**/*\$WhenMappings*", "**/*\$DefaultImpls*")

/** JaCoCo reports on every test run, and a line and branch floor that `check` fails below. */
internal fun Project.configureCoverageFloor(line: Double, branch: Double, excludes: List<String>) {
    pluginManager.apply("jacoco")
    extensions.configure<JacocoPluginExtension> {
        toolVersion = libs.version("jacoco")
    }
    val excluded = SYNTHETIC_CLASSES + excludes

    val report = tasks.named<JacocoReport>("jacocoTestReport") {
        dependsOn(tasks.named("test"))
        classDirectories.setFrom(
            files(classDirectories.files.map { directory -> fileTree(directory) { exclude(excluded) } }),
        )
        reports {
            xml.required.set(true)
            html.required.set(true)
            csv.required.set(false)
        }
    }
    tasks.named<Test>("test") {
        finalizedBy(report)
    }

    val verification = tasks.named<JacocoCoverageVerification>("jacocoTestCoverageVerification") {
        dependsOn(tasks.named("test"))
        classDirectories.setFrom(
            files(classDirectories.files.map { directory -> fileTree(directory) { exclude(excluded) } }),
        )
        violationRules {
            rule {
                element = "BUNDLE"
                limit {
                    counter = "LINE"
                    value = "COVEREDRATIO"
                    minimum = line.toString().toBigDecimal()
                }
                limit {
                    counter = "BRANCH"
                    value = "COVEREDRATIO"
                    minimum = branch.toString().toBigDecimal()
                }
            }
        }
    }
    tasks.named("check") {
        dependsOn(verification)
    }
}
