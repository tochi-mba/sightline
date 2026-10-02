package com.rextechnologies.sightline.buildlogic

import org.gradle.api.Project
import org.gradle.api.file.ConfigurableFileTree
import org.gradle.api.tasks.testing.Test
import org.gradle.kotlin.dsl.configure
import org.gradle.kotlin.dsl.register
import org.gradle.kotlin.dsl.withType
import org.gradle.testing.jacoco.plugins.JacocoPluginExtension
import org.gradle.testing.jacoco.plugins.JacocoTaskExtension
import org.gradle.testing.jacoco.tasks.JacocoCoverageVerification
import org.gradle.testing.jacoco.tasks.JacocoReport

/** The debug unit tests: the only variant whose unit tests run. */
private const val TEST_TASK = "testDebugUnitTest"

/** Where the Android Gradle plugin's built-in Kotlin compiler puts the debug classes. */
private const val DEBUG_CLASSES = "intermediates/built_in_kotlinc/debug/compileDebugKotlin/classes"

/** What the Compose compiler generates around every composable; no test can reach its lines. */
private val COMPOSE_SYNTHETICS = listOf("**/ComposableSingletons*")

/** The Compose screens. Their lines are measured; their branches are the Compose compiler's. */
private const val SCREENS = "**/ui/**"

/**
 * JaCoCo over the app's Robolectric tests, with two floors that `check` fails below.
 *
 * The logic (everything outside `ui/`) is held to [logicLine] and [logicBranch]. The screens are
 * held to [screenLine] only: the Compose compiler wraps every composable in branches for skipping
 * and restarting that a test cannot steer, so a branch floor there would measure the compiler.
 */
internal fun Project.configureAppCoverage(logicLine: Double, logicBranch: Double, screenLine: Double) {
    pluginManager.apply("jacoco")
    extensions.configure<JacocoPluginExtension> {
        toolVersion = libs.version("jacoco")
    }
    tasks.withType<Test>().configureEach {
        extensions.configure<JacocoTaskExtension> {
            // Robolectric loads the app's classes through its own class loader, with no location.
            isIncludeNoLocationClasses = true
            excludes = listOf("jdk.internal.*")
        }
    }
    val execution = layout.buildDirectory.file("jacoco/$TEST_TASK.exec")
    val classes = { configure: ConfigurableFileTree.() -> Unit ->
        fileTree(layout.buildDirectory.dir(DEBUG_CLASSES)) {
            exclude(SYNTHETIC_CLASSES + COMPOSE_SYNTHETICS)
            configure()
        }
    }

    val report = tasks.register<JacocoReport>("jacocoAppReport") {
        group = "verification"
        description = "Coverage of the app's code by its Robolectric and Compose tests."
        dependsOn(TEST_TASK)
        executionData.setFrom(execution)
        classDirectories.setFrom(classes {})
        sourceDirectories.setFrom(files("src/main/kotlin"))
        reports {
            xml.required.set(true)
            html.required.set(true)
            csv.required.set(false)
        }
    }
    tasks.withType<Test>().configureEach {
        finalizedBy(report)
    }

    val logic = tasks.register<JacocoCoverageVerification>("jacocoAppLogicVerification") {
        group = "verification"
        description = "Holds the app's logic to its line and branch floor."
        dependsOn(TEST_TASK)
        executionData.setFrom(execution)
        classDirectories.setFrom(classes { exclude(SCREENS) })
        violationRules {
            rule {
                element = "BUNDLE"
                limit {
                    counter = "LINE"
                    value = "COVEREDRATIO"
                    minimum = logicLine.toString().toBigDecimal()
                }
                limit {
                    counter = "BRANCH"
                    value = "COVEREDRATIO"
                    minimum = logicBranch.toString().toBigDecimal()
                }
            }
        }
    }
    val screens = tasks.register<JacocoCoverageVerification>("jacocoAppScreenVerification") {
        group = "verification"
        description = "Holds the app's Compose screens to their line floor."
        dependsOn(TEST_TASK)
        executionData.setFrom(execution)
        classDirectories.setFrom(classes { include(SCREENS) })
        violationRules {
            rule {
                element = "BUNDLE"
                limit {
                    counter = "LINE"
                    value = "COVEREDRATIO"
                    minimum = screenLine.toString().toBigDecimal()
                }
            }
        }
    }
    tasks.named("check") {
        dependsOn(logic, screens)
    }
}
