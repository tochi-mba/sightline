plugins {
    id("sightline.android-application")
    id("sightline.compose")
}

android {
    namespace = "com.rextechnologies.sightline"

    defaultConfig {
        applicationId = "com.rextechnologies.sightline"
    }

    // The reference camera's own menu, as the fake camera in these tests describes itself.
    sourceSets.getByName("test").resources.directories.add(rootProject.file("protocol/golden").path)
}

// The floors the plan sets: every line and branch of the app's logic, and 99% of its screens' lines,
// which leaves room only for what the Compose compiler adds and a test cannot steer.
sightline {
    appCoverageFloor(logicLine = 1.0, logicBranch = 1.0, screenLine = 0.99)
}

// Virtual time in the tests, as in :android:core.
tasks.withType<org.jetbrains.kotlin.gradle.tasks.KotlinCompile>().matching { it.name.contains("UnitTest") }.configureEach {
    compilerOptions.optIn.add("kotlinx.coroutines.ExperimentalCoroutinesApi")
}

dependencies {
    implementation(project(":android:core"))
    implementation(project(":android:design"))

    implementation(platform(libs.compose.bom))
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.core.ktx)
    implementation(libs.compose.foundation)
    implementation(libs.compose.ui)
    implementation(libs.kotlinx.coroutines.android)

    testImplementation(testFixtures(project(":protocol")))
    testImplementation(libs.junit)
    testImplementation(kotlin("test-junit"))
    testImplementation(libs.robolectric)
    testImplementation(libs.androidx.test.core)
    testImplementation(libs.androidx.test.ext.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(platform(libs.compose.bom))
    testImplementation(libs.compose.ui.test.junit4)
    debugImplementation(libs.compose.ui.test.manifest)
}
