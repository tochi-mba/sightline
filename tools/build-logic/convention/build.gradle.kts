plugins {
    `kotlin-dsl`
}

// The gates every other module is held to are written in this project, so it is held to them too. A
// deprecated Gradle or AGP call here is exactly the kind of thing the convention plugins exist to
// stop elsewhere, and it would otherwise compile quietly in the one build that defines them.
kotlin {
    compilerOptions {
        allWarningsAsErrors.set(true)
    }
}

val ktlintDependencies = configurations.dependencyScope("ktlint")
val ktlintClasspath = configurations.resolvable("ktlintClasspath") {
    extendsFrom(ktlintDependencies.get())
}

dependencies {
    // Compiled against, never bundled: the build that applies these plugins already has the Android
    // and Kotlin Gradle plugins on its classpath, at the versions the catalog pins.
    compileOnly(libs.android.gradle.plugin)
    compileOnly(libs.kotlin.gradle.plugin)

    add(ktlintDependencies.name, libs.ktlint.cli)
}

// The same check sightline.quality gives every other module, written out by hand because this
// project cannot apply a plugin it is itself compiling.
val ktlintCheck = tasks.register<JavaExec>("ktlintCheck") {
    group = "verification"
    description = "Checks the convention plugins' own formatting."
    classpath = files(ktlintClasspath)
    mainClass.set("com.pinterest.ktlint.Main")
    // The glob is relative to the process's working directory, which Gradle does not otherwise
    // guarantee is this project.
    workingDir = projectDir
    args("src/**/*.kt", "--reporter=plain", "--relative")

    inputs.files(fileTree("src") { include("**/*.kt") }).withPathSensitivity(PathSensitivity.RELATIVE)
    inputs.file(repositoryFile(".editorconfig"))
    val marker = layout.buildDirectory.file("ktlint/passed.txt")
    outputs.file(marker)
    doLast {
        marker.get().asFile.apply { parentFile.mkdirs() }.writeText("ok\n")
    }
}

tasks.register<JavaExec>("ktlintFormat") {
    group = "formatting"
    description = "Applies ktlint's layout to the convention plugins."
    classpath = files(ktlintClasspath)
    mainClass.set("com.pinterest.ktlint.Main")
    workingDir = projectDir
    args("-F", "src/**/*.kt", "--reporter=plain", "--relative")
}

tasks.named("check") {
    dependsOn(ktlintCheck)
}

/** A file in the repository root, two levels above this included build. */
fun repositoryFile(name: String) = rootProject.layout.projectDirectory.dir("../..").file(name)

gradlePlugin {
    plugins {
        register("jvmLibrary") {
            id = "sightline.jvm-library"
            implementationClass = "com.rextechnologies.sightline.buildlogic.JvmLibraryConventionPlugin"
        }
        register("androidApplication") {
            id = "sightline.android-application"
            implementationClass = "com.rextechnologies.sightline.buildlogic.AndroidApplicationConventionPlugin"
        }
        register("compose") {
            id = "sightline.compose"
            implementationClass = "com.rextechnologies.sightline.buildlogic.ComposeConventionPlugin"
        }
        register("quality") {
            id = "sightline.quality"
            implementationClass = "com.rextechnologies.sightline.buildlogic.QualityConventionPlugin"
        }
    }
}
