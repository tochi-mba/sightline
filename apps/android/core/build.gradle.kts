plugins {
    id("sightline.jvm-library")
}

// The app's logic with no Android in it: the camera session and everything that drives it, navigation,
// the settings schema, Sentry, the HUD's arithmetic. Pure JVM, so every line is tested without
// Robolectric, against the same fake camera the protocol is tested against.
sightline {
    coverageFloor(line = 1.0, branch = 1.0)
}

// The reference camera's own menu, among the committed vectors, is what the fake camera in these tests
// describes itself with: the controller is tested against the twenty-one settings a real camera has.
sourceSets.test {
    resources.srcDir(rootProject.layout.projectDirectory.dir("protocol/golden"))
}

// Virtual time (advanceTimeBy, currentTime, the unconfined test dispatcher) is still marked experimental
// in kotlinx-coroutines-test. The tests are what use it, so only their compilation opts in.
tasks.named<org.jetbrains.kotlin.gradle.tasks.KotlinCompile>("compileTestKotlin") {
    compilerOptions.optIn.add("kotlinx.coroutines.ExperimentalCoroutinesApi")
}

dependencies {
    // The session and controller built here speak the protocol, and hand its types to the app.
    api(project(":protocol"))
    api(libs.kotlinx.coroutines.core)

    testImplementation(testFixtures(project(":protocol")))
    testImplementation(kotlin("test-junit"))
    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
}
