// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "IPbind",
    platforms: [.macOS(.v13)],
    targets: [
        .target(name: "IPbindCore"),
        .executableTarget(name: "IPbind", dependencies: ["IPbindCore"]),
        // CI smoke-test helper: prints the exact scripts the app would run as root.
        .executableTarget(name: "ipbind-script", dependencies: ["IPbindCore"]),
        .testTarget(name: "IPbindCoreTests", dependencies: ["IPbindCore"]),
    ]
)
