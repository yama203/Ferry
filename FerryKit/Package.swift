// swift-tools-version:6.0
import PackageDescription

// Homebrew の curl（libssh2 同梱で SFTP 対応）を使う。macOS 標準の libcurl は SFTP 非対応。
let curlPrefix = "/opt/homebrew/opt/curl"

let package = Package(
    name: "FerryKit",
    platforms: [.macOS(.v15)],
    products: [
        .library(name: "FerryKit", targets: ["FerryKit"]),
        .executable(name: "ferry-cli", targets: ["ferry-cli"]),
    ],
    targets: [
        .target(
            name: "CCurl",
            linkerSettings: [
                .unsafeFlags(["-L\(curlPrefix)/lib"]),
                .linkedLibrary("curl"),
            ]
        ),
        .target(name: "FerryKit", dependencies: ["CCurl"]),
        .executableTarget(name: "ferry-cli", dependencies: ["FerryKit"]),
        .testTarget(name: "FerryKitTests", dependencies: ["FerryKit"]),
    ],
    swiftLanguageModes: [.v5]
)
