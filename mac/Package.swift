// swift-tools-version:5.9
import Foundation
import PackageDescription

// libopus comes from Homebrew. We link the static archive so the produced binary
// keeps working if the Homebrew cellar is upgraded or the app is moved.
let opusPrefix = ProcessInfo.processInfo.environment["OPUS_PREFIX"] ?? "/opt/homebrew/opt/opus"

let package = Package(
    name: "HexBridge",
    // SwiftUI's MenuBarExtra window style, @Observable and SettingsLink all
    // need 14; 13 would still build the CLI but not the UI.
    platforms: [.macOS(.v14)],
    dependencies: [
        // `MenuBarExtra` still has no API for opening its own window: the
        // macOS 27 SDK ships eight initialisers and `isPresented` is not among
        // them. This package supplies the binding and the `NSStatusItem`
        // handle without private API. Verified on macOS 26.6.2 with Swift
        // 6.3.3 — see mac/docs/STAGE-MINUS-1.md.
        //
        // Not added, and why: `KeyboardShortcuts` 3.0.1 cannot be built with
        // the Command Line Tools toolchain on this machine — it uses SwiftUI's
        // `@Entry` macro, whose plugin ships only inside Xcode. `Sparkle` is
        // deferred until there is an appcast and an EdDSA key to sign it with.
        // `LaunchAtLogin` is deliberately absent: `SMAppService` is 30 lines.
        .package(url: "https://github.com/orchetect/MenuBarExtraAccess.git", from: "1.3.1"),
    ],
    targets: [
        .target(
            name: "COpusShim",
            path: "Sources/COpusShim",
            cSettings: [.unsafeFlags(["-I\(opusPrefix)/include"])]
        ),
        .executableTarget(
            name: "HexBridge",
            dependencies: ["COpusShim", "MenuBarExtraAccess"],
            path: "Sources/HexBridge",
            linkerSettings: [.unsafeFlags(["-Xlinker", "\(opusPrefix)/lib/libopus.a"])]
        ),
    ]
)
