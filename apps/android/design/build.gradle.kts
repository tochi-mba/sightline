plugins {
    id("sightline.android-library")
    id("sightline.compose")
}

android {
    namespace = "com.rextechnologies.sightline.design"
}

dependencies {
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.foundation)
    implementation(libs.compose.material3)
    implementation(libs.compose.ui)
}
