// Two modules on purpose. `core` is the contract — plain Kotlin on the JVM, no
// Android class anywhere in it — so it can be tested on any machine and pointed
// at a real Windows receiver without a television in the room. `app` is the
// Android TV client built on top of it, and is the only part that needs an SDK.
rootProject.name = "hexbridge-tv"

include(":core")
