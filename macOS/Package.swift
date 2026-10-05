// swift-tools-version:6.2
import PackageDescription

let package = Package(
    name: "Clawd",
    platforms: [.macOS(.v15)],
    dependencies: [
        // A full xterm emulator and AppKit view: colours, cursor, IME, selection. Pinned to the
        // last release before its Metal renderer, which needs the separately installed Metal toolchain.
        .package(url: "https://github.com/migueldeicaza/SwiftTerm", exact: "1.11.2"),
        // Updates: checks the appcast, verifies each download's EdDSA signature, replaces the app
        // and relaunches it. A prebuilt framework; build.sh embeds and signs it.
        .package(url: "https://github.com/sparkle-project/Sparkle", exact: "2.10.0"),
    ],
    targets: [
        .executableTarget(name: "Clawd", dependencies: ["SwiftTerm", .product(name: "Sparkle", package: "Sparkle")], path: "Sources",
                          swiftSettings: [.swiftLanguageMode(.v6), .defaultIsolation(MainActor.self)]),
        .testTarget(name: "ClawdTests", dependencies: ["Clawd"], path: "Tests/ClawdTests",
                    swiftSettings: [.swiftLanguageMode(.v6)]),
    ]
)
