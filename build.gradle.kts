// Every plugin the convention plugins apply, on the classpath once at the root so each module that
// applies them gets the version the catalog pins. Nothing is applied here.
plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.android.library) apply false
    alias(libs.plugins.kotlin.jvm) apply false
    alias(libs.plugins.kotlin.compose) apply false
}
