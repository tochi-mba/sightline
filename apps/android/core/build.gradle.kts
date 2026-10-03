plugins {
    id("sightline.jvm-library")
}

// The app's logic with no Android in it: navigation now, then the camera session, the settings
// schema, sentry and telemetry. Pure JVM, so every line is tested without Robolectric.
sightline {
    coverageFloor(line = 1.0, branch = 1.0)
}

dependencies {
    // The session and driver built here speak the protocol, and hand its types to the app.
    api(project(":protocol"))

    testImplementation(kotlin("test-junit"))
    testImplementation(libs.junit)
}
