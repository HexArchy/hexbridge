plugins {
    kotlin("jvm") version "2.4.20"
}

repositories { mavenCentral() }

dependencies {
    testImplementation(kotlin("test"))
}

kotlin {
    // Android TV on a Mi Box is API 28; the library half of this has to compile
    // for it as well as for the desktop JVM the tests run on.
    jvmToolchain(17)
}

tasks.test { useJUnitPlatform() }
