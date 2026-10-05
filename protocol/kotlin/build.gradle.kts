plugins {
    id("sightline.jvm-library")
    // The fake camera, both its control channel and its stream, shared with every module that tests
    // against a camera: the Android app's session is held to the same behaviour this library is.
    `java-test-fixtures`
}

// The committed vectors, including the reference camera's own menu, are the test data: the very files
// the .NET tests read, so the two implementations are held to the same bytes.
sourceSets.test {
    resources.srcDir(rootProject.layout.projectDirectory.dir("protocol/golden"))
}

sightline {
    coverageFloor(line = 1.0, branch = 1.0)
}

dependencies {
    implementation(libs.kotlinx.coroutines.core)

    testFixturesImplementation(libs.kotlinx.coroutines.core)

    testImplementation(kotlin("test-junit"))
    testImplementation(libs.junit)
}
