plugins {
    id("com.android.application")
    kotlin("android")
}

android {
    namespace = "ru.hexarch.hexbridge.tv"
    compileSdk = 34

    defaultConfig {
        applicationId = "ru.hexarch.hexbridge.tv"
        // A Mi Box S is Android TV 8.1 or 9, so 26 costs nothing and buys
        // java.util.Base64 — which lets the key be parsed in `core`, where a test
        // can get at it, rather than behind android.util.Base64 where none can.
        minSdk = 26
        targetSdk = 34
        versionCode = 1
        versionName = "1.0.0"
    }

    // Signed with a key that lives outside both repositories — never in git, and
    // never regenerated. Android refuses to install an update signed by a
    // different key, so a keystore made fresh on every build machine would mean
    // uninstalling the app to update it. Passwords come from
    // ~/.gradle/gradle.properties; without them the release build is unsigned and
    // `assembleDebug` is the one to use.
    val keystore = File(System.getProperty("user.home"), ".hexbridge/tv-release.jks")
    val storePassword = providers.gradleProperty("hexbridgeStorePassword").orNull

    signingConfigs {
        if (keystore.exists() && storePassword != null) {
            create("release") {
                storeFile = keystore
                this.storePassword = storePassword
                keyAlias = "hexbridge"
                keyPassword = providers.gradleProperty("hexbridgeKeyPassword").orNull ?: storePassword
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlin { jvmToolchain(17) }
}

dependencies {
    implementation(project(":core"))
}
