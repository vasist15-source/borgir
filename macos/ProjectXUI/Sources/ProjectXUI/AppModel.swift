import AppKit
import Observation
import SwiftUI

struct AlertMessage: Identifiable {
    let id = UUID()
    var title: String
    var message: String
}

/// Everything the windows show, and the actions that change it. Lives on the main thread.
@MainActor
@Observable
final class AppModel {
    static let shared = AppModel()

    var info: CoreInfo?
    var games: [Game] = []
    var libraryLoading = false
    var libraryFound = 0
    var libraryLoaded = 0
    var session: GameSession?
    var alert: AlertMessage?
    /// Shown as a sheet while the core does something slow, like installing firmware.
    var busyMessage: String?
    /// The profile whose firmware avatar picker is open in Settings › Profiles.
    var avatarPickerProfile: Profile?
    /// The running game is waiting for the player to set up controllers.
    var controllerRequest: ControllerRequest?
    var searchText = ""
    var sort: LibrarySort = .lastPlayed
    /// The game shown in the library's detail pane.
    var selectedGameID: Game.ID?
    /// Set when the system asked the app to quit (SIGTERM), which skips the confirmation.
    var terminateWithoutAsking = false

    let settings = SettingsStore()

    private var started = false

    var visibleGames: [Game] {
        let query = searchText.trimmingCharacters(in: .whitespaces)
        let filtered = query.isEmpty
            ? games
            : games.filter { $0.name.localizedCaseInsensitiveContains(query) || $0.developer.localizedCaseInsensitiveContains(query) || $0.id.localizedCaseInsensitiveContains(query) }

        switch sort {
        case .name:
            return filtered.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        case .timePlayed:
            return filtered.sorted { $0.timePlayed > $1.timePlayed }
        case .lastPlayed:
            return filtered.sorted { ($0.lastPlayed ?? 0, $1.name) > ($1.lastPlayed ?? 0, $0.name) }
        }
    }

    var needsSetup: Bool {
        guard let info else {
            return false
        }

        return !info.keysInstalled || info.firmwareVersion == nil
    }

    var selectedGame: Game? {
        games.first { $0.id == selectedGameID }
    }

    var activeProfile: Profile? {
        info?.profiles.first { $0.id == info?.activeProfile }
    }

    func start() {
        guard !started else {
            return
        }

        started = true
        reloadInfo()
        settings.reload()
        refreshLibrary()

        if let path = info?.launchPath, !path.isEmpty {
            launchFile(URL(fileURLWithPath: path))
        }
    }

    func reloadInfo() {
        info = Core.command("app.info", as: CoreInfo.self)
    }

    func refreshLibrary() {
        libraryLoading = true
        libraryFound = 0
        libraryLoaded = 0
        report(Core.command("library.refresh"))
    }

    // MARK: Game folders

    func addGameFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = true
        panel.prompt = "Add Folder"
        panel.message = "Choose folders that contain your games (NSP, XCI, NCA, NRO, NSO)."

        guard panel.runModal() == .OK else {
            return
        }

        for url in panel.urls {
            report(Core.command("library.addDir", ["path": url.path]))
        }

        reloadInfo()
        refreshLibrary()
    }

    func removeGameFolder(_ path: String) {
        report(Core.command("library.removeDir", ["path": path]))
        reloadInfo()
        refreshLibrary()
    }

    /// Folders of updates and DLC, applied to the games they belong to on every scan.
    func addAutoloadFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = true
        panel.prompt = "Add Folder"
        panel.message = "Choose folders that contain updates or DLC (NSP or XCI)."

        guard panel.runModal() == .OK else {
            return
        }

        for url in panel.urls {
            report(Core.command("library.addDir", ["path": url.path, "kind": "autoload"]))
        }

        reloadInfo()
        refreshLibrary()
    }

    func removeAutoloadFolder(_ path: String) {
        report(Core.command("library.removeDir", ["path": path, "kind": "autoload"]))
        reloadInfo()
        refreshLibrary()
    }

    // MARK: Emulation

    func launch(_ game: Game) {
        guard session == nil else {
            return
        }

        guard let info, info.keysInstalled else {
            alert = AlertMessage(title: "Production Keys Missing", message: "Install your prod.keys before starting a game. Use Settings › General › Install Keys.")
            return
        }

        session = GameSession(game: game)
    }

    func openGameFile() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.allowedContentTypes = []
        panel.allowsOtherFileTypes = true
        panel.message = "Choose a game to start (NSP, XCI, NCA, NRO or NSO)."

        guard panel.runModal() == .OK, let url = panel.url else {
            return
        }

        launchFile(url)
    }

    func launchFile(_ url: URL) {
        let reply = Core.command("library.inspect", ["path": url.path], as: LibraryReply.self)

        guard let game = reply?.games.first else {
            alert = AlertMessage(title: "Unable to Open Game", message: "\(url.lastPathComponent) is not a game Borgir can read. Check that your keys are installed and the file is a Switch game.")
            return
        }

        launch(game)
    }

    /// Called by the game view once its Metal layer is on screen.
    func startEmulation(_ session: GameSession, layer: UnsafeMutableRawPointer, width: Double, height: Double, scale: Double) {
        let reply = Core.command("game.launch", [
            "path": session.game.path,
            "id": session.game.id,
            "layer": String(UInt(bitPattern: layer), radix: 16),
            "width": width,
            "height": height,
            "scale": scale,
        ])

        if reply["ok"] as? Bool != true {
            self.session = nil
            MainWindow.shared.leaveFullScreenAfterGame(wasFullScreen: session.windowWasFullScreen)
            alert = AlertMessage(title: "Unable to Start \(session.game.name)", message: reply["error"] as? String ?? "The emulator could not start this game.")
        }
    }

    func stopGame() {
        guard let session, session.phase != .stopping else {
            return
        }

        session.phase = .stopping

        // The game waits for this answer on one of its threads, which has to finish for it to stop.
        controllerRequest?.answer(false)
        controllerRequest = nil

        report(Core.command("game.stop"))
    }

    func togglePause() {
        guard let session, session.phase == .running else {
            return
        }

        report(Core.command(session.status?.paused == true ? "game.resume" : "game.pause"))
    }

    func gameCommand(_ name: String, _ arguments: [String: Any] = [:]) {
        report(Core.command(name, arguments))
    }

    // MARK: Keys, firmware, profiles

    func installKeys() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = true
        panel.message = "Choose your prod.keys file, or the folder that contains it."

        guard panel.runModal() == .OK, let url = panel.url else {
            return
        }

        let reply = Core.command("keys.install", ["path": url.path])

        if reply["ok"] as? Bool == true {
            reloadInfo()
            refreshLibrary()
        } else {
            report(reply, title: "Unable to Install Keys")
        }
    }

    func installFirmware() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = true
        panel.message = "Choose a firmware ZIP or XCI, or a folder of firmware NCAs."

        guard panel.runModal() == .OK, let url = panel.url else {
            return
        }

        let check = Core.command("firmware.verify", ["path": url.path])

        guard let version = check["version"] as? String else {
            report(check, title: "Unable to Install Firmware")
            return
        }

        let confirm = NSAlert()
        confirm.messageText = "Install Firmware \(version)?"
        confirm.informativeText = info?.firmwareVersion.map { "This replaces the installed firmware \($0)." } ?? "Games need firmware to start."
        confirm.addButton(withTitle: "Install")
        confirm.addButton(withTitle: "Cancel")

        guard confirm.runModal() == .alertFirstButtonReturn else {
            return
        }

        busyMessage = "Installing firmware \(version)…"
        report(Core.command("firmware.install", ["path": url.path]), title: "Unable to Install Firmware")
    }

    func selectProfile(_ profile: Profile) {
        report(Core.command("profiles.select", ["id": profile.id]))
        reloadInfo()
    }

    func revealDataFolder(_ subpath: String = "") {
        guard let info else {
            return
        }

        let url = URL(fileURLWithPath: info.dataDir).appendingPathComponent(subpath)
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }

    // MARK: Events from the core

    func handle(event data: Data) {
        guard let header = try? JSONDecoder().decode(EventHeader.self, from: data) else {
            return
        }

        switch header.event {
        case "library.updated":
            if let reply = try? JSONDecoder().decode(LibraryReply.self, from: data) {
                games = reply.games
                libraryLoading = reply.loading
            }
        case "library.progress":
            if let progress = try? JSONDecoder().decode(LibraryProgress.self, from: data) {
                libraryFound = progress.found
                libraryLoaded = progress.loaded
            }
        case "info.changed":
            if let newInfo = try? JSONDecoder().decode(CoreInfo.self, from: data) {
                info = newInfo
            }
        case "settings.changed":
            settings.reload()
        case "firmware.installed":
            busyMessage = nil
            reloadInfo()
            if let notice = try? JSONDecoder().decode(Notice.self, from: data) {
                alert = AlertMessage(title: notice.title, message: notice.message)
            }
        case "notice":
            busyMessage = nil
            if let notice = try? JSONDecoder().decode(Notice.self, from: data) {
                alert = AlertMessage(title: notice.title, message: notice.message)
            }
        case "game.loading":
            if let progress = try? JSONDecoder().decode(LoadingProgress.self, from: data) {
                session?.loadingStage = progress.stage
                session?.loadingCurrent = progress.current
                session?.loadingTotal = progress.total
                if session?.phase == .starting {
                    session?.phase = .loading
                }
            }
        case "game.firstFrame":
            if session?.phase != .stopping {
                session?.phase = .running
                // Show the controls briefly so the player knows where they are.
                session?.overlay.noteActivity(seconds: 5)
            }
        case "game.status":
            if let status = try? JSONDecoder().decode(GameStatus.self, from: data) {
                session?.status = status
            }
        case "app.terminate":
            // SIGTERM: quit without asking.
            terminateWithoutAsking = true
            NSApp.perform(#selector(NSApplication.terminate(_:)), with: nil, afterDelay: 0)
        case "overlay.toggle":
            session?.overlay.toggle()
        case "screenshot.saved":
            session?.overlay.show(toast: "Screenshot saved")
        case "game.stopped":
            let stopped = try? JSONDecoder().decode(GameStopped.self, from: data)
            let name = session?.game.name ?? "The game"
            let wasFullScreen = session?.windowWasFullScreen ?? true
            session = nil
            MainWindow.shared.leaveFullScreenAfterGame(wasFullScreen: wasFullScreen)
            refreshLibrary()
            if let error = stopped?.error, !error.isEmpty {
                alert = AlertMessage(title: "\(name) Stopped", message: error)
            }
        default:
            break
        }
    }

    func report(_ reply: [String: Any], title: String = "Something Went Wrong") {
        if reply["ok"] as? Bool != true {
            busyMessage = nil
            alert = AlertMessage(title: title, message: reply["error"] as? String ?? "The emulator core reported an error.")
        }
    }
}

private struct EventHeader: Decodable {
    var event: String
}

private struct LibraryProgress: Decodable {
    var loaded: Int
    var found: Int
}

private struct LoadingProgress: Decodable {
    var stage: String
    var current: Int
    var total: Int
}

private struct Notice: Decodable {
    var title: String
    var message: String
}

private struct GameStopped: Decodable {
    var error: String?
}

/// One running (or starting) game.
@MainActor
@Observable
final class GameSession: Identifiable {
    enum Phase {
        case starting
        case loading
        case running
        case stopping
    }

    let id = UUID()
    let game: Game
    let overlay = OverlayState()
    var phase: Phase = .starting
    /// Whether the window was already in full screen when the game appeared; if not, it leaves full screen when the
    /// game ends.
    var windowWasFullScreen = false
    var loadingStage = "Starting"
    var loadingCurrent = 0
    var loadingTotal = 0
    var status: GameStatus?

    init(game: Game) {
        self.game = game
    }

    var loadingFraction: Double? {
        loadingTotal > 0 ? min(1, Double(loadingCurrent) / Double(loadingTotal)) : nil
    }
}

/// The core's settings as plain JSON values, with bindings for the settings and overlay views.
@MainActor
@Observable
final class SettingsStore {
    private(set) var values: [String: Any] = [:]
    /// Choices for the settings that pick from a list, keyed by setting.
    private(set) var options: [String: [String]] = [:]
    /// The settings the running game has its own value for; the values above are the game's for these.
    private(set) var ownSettings: [String] = []

    func reload() {
        let reply = Core.command("settings.get")
        values = reply["settings"] as? [String: Any] ?? [:]
        options = reply["options"] as? [String: [String]] ?? [:]
        ownSettings = reply["ownSettings"] as? [String] ?? []
    }

    func set(_ key: String, _ value: Any) {
        values[key] = value

        let reply = Core.command("settings.set", ["key": key, "value": value])

        if let settings = reply["settings"] as? [String: Any] {
            values = settings
        }

        if reply["ok"] as? Bool != true {
            AppModel.shared.alert = AlertMessage(title: "Unable to Change Setting", message: reply["error"] as? String ?? key)
        }
    }

    func value<T>(_ key: String, default fallback: T) -> T {
        values[key] as? T ?? fallback
    }

    func bool(_ key: String) -> Binding<Bool> {
        Binding(get: { self.values[key] as? Bool ?? false }, set: { self.set(key, $0) })
    }

    func double(_ key: String) -> Binding<Double> {
        Binding(get: { (self.values[key] as? NSNumber)?.doubleValue ?? 0 }, set: { self.set(key, $0) })
    }

    func int(_ key: String) -> Binding<Int> {
        Binding(get: { (self.values[key] as? NSNumber)?.intValue ?? 0 }, set: { self.set(key, $0) })
    }

    func string(_ key: String) -> Binding<String> {
        Binding(get: { self.values[key] as? String ?? "" }, set: { self.set(key, $0) })
    }

    func choices(_ key: String) -> [String] {
        options[key] ?? []
    }
}
