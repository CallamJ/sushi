plugins {
    // IntelliJ 2026.2 is compiled with Kotlin 2.4 metadata. The plugin
    // compiler must be able to read that metadata from the platform jars.
    kotlin("jvm") version "2.4.20"
    id("org.jetbrains.intellij.platform") version "2.5.0"
}

group = "dev.sushi"
version = providers.gradleProperty("sushiVersion").orElse("0.0.0").get()

repositories { mavenCentral(); intellijPlatform { defaultRepositories() } }

dependencies {
    intellijPlatform {
        intellijIdeaUltimate("2026.2")
        pluginVerifier()
    }
}

intellijPlatform {
    pluginConfiguration {
        ideaVersion {
            sinceBuild = "262"
            untilBuild = "262.*"
        }
    }
}

kotlin { jvmToolchain(21) }

// Searchable options are an optimization for settings pages. Sushi has no
// settings that contribute search entries, and producing them starts a second
// bundled IDE instance, which IntelliJ rejects while the developer IDE is open.
tasks.buildSearchableOptions {
    enabled = false
}
