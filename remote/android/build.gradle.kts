// AGP 9 compiles Kotlin itself, so only the Compose compiler plugin is added. This set (AGP 9.5.0, Gradle 9.8.2,
// Kotlin 2.4.21) is the one that built another Compose app on the dev PC on 2026-10-31.
plugins {
    id("com.android.application") version "9.5.0" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.4.21" apply false
}
