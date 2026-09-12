pluginManagement { repositories { gradlePluginPortal(); maven("https://cache-redirector.jetbrains.com/intellij-dependencies") } }
dependencyResolutionManagement { repositoriesMode.set(RepositoriesMode.PREFER_PROJECT); repositories { mavenCentral() } }
rootProject.name = "sushi-jetbrains"
