// swift-tools-version: 6.2

// The native macOS interface for Project X. It builds into libProjectXUI.dylib, which the emulator
// (src/Ryujinx) loads at startup and hands control of the main thread to. See README.md.

import PackageDescription

let package = Package(
    name: "ProjectXUI",
    platforms: [.macOS(.v26)],
    products: [
        .library(name: "ProjectXUI", type: .dynamic, targets: ["ProjectXUI"]),
    ],
    targets: [
        .target(name: "PXBridge"),
        .target(
            name: "ProjectXUI",
            dependencies: ["PXBridge"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
    ]
)
