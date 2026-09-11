// swift-tools-version:5.9
import Foundation
import PackageDescription

// libopus comes from Homebrew. We link the static archive so the produced binary
// keeps working if the Homebrew cellar is upgraded or the app is moved.
let opusPrefix = ProcessInfo.processInfo.environment["OPUS_PREFIX"] ?? "/opt/homebrew/opt/opus"

let package = Package(
    name: "HexBridge",
    // Required by SwiftPM before a target may carry `<lang>.lproj` resources,
    // and true besides: English is the language the interface is written in and
    // the one a key falls back to when the other file has not got it.
    defaultLocalization: "en",
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
        // `@Entry` macro, whose plugin ships only inside Xcode.
        // `LaunchAtLogin` is deliberately absent: `SMAppService` is 30 lines.
        .package(url: "https://github.com/orchetect/MenuBarExtraAccess.git", from: "1.3.1"),
        // Updates outside the App Store. There is no alternative and there has
        // not been one for a decade. 2.9.6 rather than 2.10: the 2.10 line
        // raises the floor to macOS 12, and 2.9.4 carries the fix for
        // «activation for backgrounded / dockless applications», which is a
        // description of exactly this app (DESIGN.md §2).
        //
        // ⚠️ The licence is not plain MIT: MIT plus an EXTERNAL LICENSES
        // section for the vendored bsdiff (BSD-2-clause) and sais-lite. The
        // whole file has to ship — see docs/UPDATES.md.
        .package(url: "https://github.com/sparkle-project/Sparkle", from: "2.9.6"),
    ],
    targets: [
        .target(
            name: "COpusShim",
            path: "Sources/COpusShim",
            cSettings: [.unsafeFlags(["-I\(opusPrefix)/include"])]
        ),
        // The rule that decides whether a host found on the network is ours
        // lives here rather than in the app, and for one reason: it is a
        // security decision, so it has to be testable. The app target drags in
        // AppKit, SwiftUI, CoreAudio and a static libopus, and none of that can
        // be linked into an XCTest bundle without a fight. This target is
        // Foundation and CryptoKit, and `swift test` runs it in a second.
        .target(
            name: "HexBridgeDiscovery",
            path: "Sources/HexBridgeDiscovery"
        ),
        // Every word the interface says, in both languages, plus the plural
        // rules and the number formatting that go with them. Its own target for
        // the same reason as `HexBridgeDiscovery`: the app target cannot be
        // linked into an XCTest bundle, and «есть ли у каждого ключа обе
        // строки» has to be a test rather than a promise.
        .target(
            name: "HexBridgeText",
            path: "Sources/HexBridgeText",
            resources: [.process("Resources")]
        ),
        // The short-code exchange hands the key over the network, so what protects it
        // is a security decision and belongs where a test can reach it — the same
        // argument as `HexBridgeDiscovery`, and the same shape. CommonCrypto and
        // CryptoKit, nothing else.
        .target(
            name: "HexBridgePairing",
            path: "Sources/HexBridgePairing"
        ),
        // The name on an incoming file is written by the other machine and then
        // handed to the filesystem, which makes it the same kind of decision as
        // the two above and gives it the same kind of home. A name that still
        // carries a path writes outside Downloads and a name that matches
        // something already there destroys it — both are reachable from a test
        // here and neither is reachable inside the app.
        .target(
            name: "HexBridgeFiles",
            path: "Sources/HexBridgeFiles"
        ),
        // The bookkeeping a four-gigabyte transfer turns on: which chunks have
        // arrived, which holes are left, and how fast the next ones may go out.
        // Same argument as the three targets above, with one more on top — the
        // interesting failures here are the ones that only show at four million
        // chunks, and pushing four gigabytes through the app to find them is not
        // a test anybody would run twice.
        .target(
            name: "HexBridgeBulk",
            path: "Sources/HexBridgeBulk"
        ),
        .executableTarget(
            name: "HexBridge",
            dependencies: ["COpusShim", "MenuBarExtraAccess", "HexBridgeBulk", "HexBridgeDiscovery", "HexBridgeFiles", "HexBridgePairing", "HexBridgeText", "Sparkle"],
            path: "Sources/HexBridge",
            linkerSettings: [.unsafeFlags(["-Xlinker", "\(opusPrefix)/lib/libopus.a"])]
        ),
        .testTarget(
            name: "HexBridgeBulkTests",
            dependencies: ["HexBridgeBulk"],
            path: "Tests/HexBridgeBulkTests"
        ),
        .testTarget(
            name: "HexBridgeDiscoveryTests",
            dependencies: ["HexBridgeDiscovery"],
            path: "Tests/HexBridgeDiscoveryTests"
        ),
        .testTarget(
            name: "HexBridgeFilesTests",
            dependencies: ["HexBridgeFiles"],
            path: "Tests/HexBridgeFilesTests"
        ),
        .testTarget(
            name: "HexBridgePairingTests",
            dependencies: ["HexBridgePairing"],
            path: "Tests/HexBridgePairingTests"
        ),
        .testTarget(
            name: "HexBridgeTextTests",
            dependencies: ["HexBridgeText"],
            path: "Tests/HexBridgeTextTests"
        ),
    ]
)
