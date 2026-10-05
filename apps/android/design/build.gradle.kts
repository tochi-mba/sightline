plugins {
    id("sightline.android-library")
    id("sightline.compose")
}

android {
    namespace = "com.rextechnologies.sightline.design"
}

// The REX design system: tokens and components on Compose foundation, with no Material dependency, so a
// Material update can never quietly restyle the app. Screens draw only with what is here.
dependencies {
    implementation(platform(libs.compose.bom))
    api(libs.compose.foundation)
    api(libs.compose.ui)

    testImplementation(libs.junit)
    testImplementation(libs.robolectric)
    testImplementation(libs.androidx.test.core)
    testImplementation(platform(libs.compose.bom))
    testImplementation(libs.compose.ui.test.junit4)
    debugImplementation(libs.compose.ui.test.manifest)
}
