import Foundation

/// A title found in the game folders.
struct Game: Codable, Identifiable, Hashable {
    /// Title id, 16 hex digits.
    var id: String
    var path: String
    var name: String
    var developer: String
    var version: String
    /// Cached icon on disk, if the title has one.
    var icon: String?
    var fileSize: Int64
    var fileType: String
    /// Seconds.
    var timePlayed: Double
    /// Unix time.
    var lastPlayed: Double?
    var favorite: Bool

    var lastPlayedDate: Date? {
        lastPlayed.map { Date(timeIntervalSince1970: $0) }
    }

    var timePlayedText: String? {
        guard timePlayed >= 60 else {
            return nil
        }

        let formatter = DateComponentsFormatter()
        formatter.allowedUnits = timePlayed >= 3600 ? [.hour, .minute] : [.minute]
        formatter.unitsStyle = .abbreviated

        return formatter.string(from: timePlayed)
    }
}

struct Profile: Codable, Identifiable, Hashable {
    var id: String
    var name: String
    /// Cached avatar on disk.
    var image: String?
}

/// What the core reports at startup and after keys, firmware or profiles change.
struct CoreInfo: Codable {
    var version: String
    var dataDir: String
    var keysInstalled: Bool
    var firmwareVersion: String?
    var profiles: [Profile]
    var activeProfile: String?
    var gameDirs: [String]
    /// Folders scanned for updates and DLC to apply to the library's games.
    var autoloadDirs: [String]?
    /// A game passed on the command line, reported once.
    var launchPath: String?
}

/// A game's updates, DLC and mods.
struct GameContent: Codable {
    var updates: [GameUpdate]
    var dlc: [GameDLC]
    var mods: [GameMod]
    var cheats: [GameCheat]?
    /// The settings this game has its own value for.
    var ownSettings: [String]?
    /// Where this game's mods are installed.
    var modsDir: String
}

struct GameUpdate: Codable, Identifiable, Hashable {
    var path: String
    var version: String
    var selected: Bool
    /// Comes with an XCI rather than its own file.
    var bundled: Bool

    var id: String { path + "|" + version }
}

struct GameDLC: Codable, Identifiable, Hashable {
    var titleId: String
    var name: String
    /// The NSP or XCI that holds it.
    var path: String
    /// Its NCA inside that file.
    var contentPath: String
    var enabled: Bool
    var bundled: Bool

    var id: String { path + "|" + contentPath }
}

struct GameMod: Codable, Identifiable, Hashable {
    var name: String
    var path: String
    var enabled: Bool

    var id: String { path }
}

struct GameCheat: Codable, Identifiable, Hashable {
    /// How enabled.txt names it: the build id and the cheat's name.
    var key: String
    var name: String
    /// The game executable it was made for.
    var buildId: String
    /// The mod folder it came with.
    var source: String
    /// Its cheat file.
    var path: String
    var enabled: Bool

    var id: String { key }
}

struct LibraryReply: Codable {
    var games: [Game]
    var loading: Bool
}

/// Live numbers from a running game, sent a few times a second.
struct GameStatus: Codable, Equatable {
    var fps: Double
    var frameTime: Double
    var fifo: Double
    var vsync: String
    var docked: Bool
    var volume: Double
    var resScale: Double
    var aspectRatio: String
    var shaders: Int
    var paused: Bool
    var gpu: String?
}

struct InputDevice: Codable, Identifiable, Hashable {
    var id: String
    var name: String
    /// "keyboard" or "controller".
    var kind: String
}

struct InputReply: Codable {
    var devices: [InputDevice]
    /// Players 1 to 8 and Handheld, with or without a device.
    var players: [InputPlayer]
    var enableKeyboard: Bool
    var enableMouse: Bool
}

/// One player's controller: the device, the controller type the game sees, and its layout.
struct InputPlayer: Codable, Identifiable, Hashable {
    /// "Player1" to "Player8", or "Handheld".
    var player: String
    /// The controller types this player can be.
    var types: [String]
    /// Device id, when the player has one.
    var device: String?
    var name: String?
    /// "keyboard" or "controller".
    var kind: String?
    var connected: Bool?
    var type: String?
    /// Switch button to key or controller button, by the core's names.
    var bindings: [String: String]?
    var options: InputOptions?

    var id: String { player }
    var hasDevice: Bool { device != nil }
    var isKeyboard: Bool { kind == "keyboard" }
    var title: String { InputNames.player(player) }
}

struct InputOptions: Codable, Hashable {
    var deadzoneLeft: Double
    var deadzoneRight: Double
    var invertLeftY: Bool
    var invertRightY: Bool
    var rumble: Bool
    var motion: Bool
}

enum LibrarySort: String, CaseIterable, Identifiable {
    case lastPlayed = "Last Played"
    case name = "Name"
    case timePlayed = "Time Played"

    var id: String { rawValue }
}
