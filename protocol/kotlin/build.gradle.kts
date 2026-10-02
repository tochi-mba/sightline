plugins {
    id("sightline.jvm-library")
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

    testImplementation(kotlin("test-junit"))
    testImplementation(libs.junit)
}
