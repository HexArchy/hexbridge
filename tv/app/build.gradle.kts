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

    buildTypes {
        release {
            isMinifyEnabled = false
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
