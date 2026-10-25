import AppKit
import SwiftUI

struct ProjectXApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @ViewState private var model = AppModel.shared

    var body: some Scene {
        Window("Borgir", id: "main") {
            RootView()
                .environment(model)
                .frame(minWidth: 960, minHeight: 600)
        }
        .defaultSize(width: 1280, height: 800)
        .windowToolbarStyle(.unified)
        .commands {
            ProjectXCommands(model: model)
        }

        Settings {
            SettingsView()
                .environment(model)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    /// Set once the core has been told to quit; AppKit can ask again before it exits.
    private var quitting = false

    func applicationWillFinishLaunching(_ notification: Notification) {
        // The emulator binary is the process' entry point, so make sure AppKit treats it as a regular app.
        NSApp.setActivationPolicy(.regular)
        NSWindow.allowsAutomaticWindowTabbing = false
        ExitHelpers.endOnExit()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.activate()

        if let script = ProcessInfo.processInfo.environment["PX_TEST"] {
            InterfaceTest.run(script)
        }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard !quitting else {
            return .terminateLater
        }

        let model = AppModel.shared

        // Quitting stops the game, so ask first, like the overlay's Stop button. Closing the window has already
        // decided for the player, and cancelling then would leave the game running without a window.
        if let session = model.session, !model.terminateWithoutAsking, model.settings.value("confirmExit", default: true),
           sender.windows.contains(where: { $0.isVisible && $0.canBecomeMain }) {
            let alert = NSAlert()
            alert.messageText = "Quit Borgir?"
            alert.informativeText = "\(session.game.name) is running. Progress since your last save will be lost."
            alert.addButton(withTitle: "Quit")
            alert.addButton(withTitle: "Cancel")

            guard alert.runModal() == .alertFirstButtonReturn else {
                return .terminateCancel
            }
        }

        // Stopping a game can take a few seconds; do it off the main thread so the window keeps drawing, then let
        // AppKit exit.
        quitting = true

        DispatchQueue.global(qos: .userInitiated).async {
            Core.command("app.quit")

            // Not through the main dispatch queue: when termination started inside a main-queue block, AppKit waits
            // for this reply in a run loop nested in that block, where GCD never runs main-queue work. Run loop
            // blocks still run there.
            let mainLoop = CFRunLoopGetMain()
            CFRunLoopPerformBlock(mainLoop, CFRunLoopMode.commonModes.rawValue) {
                sender.reply(toApplicationShouldTerminate: true)
            }
            CFRunLoopWakeUp(mainLoop)
        }

        return .terminateLater
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        guard let url = urls.first else {
            return
        }

        // When a game opened from Finder launches the app, AppKit skips applicationOpenUntitledFile(_:), which is
        // where SwiftUI opens its window, and the game needs that window to render into.
        if !application.windows.contains(where: { $0.isVisible && $0.canBecomeMain }) {
            _ = application.delegate?.applicationOpenUntitledFile?(application)
        }

        Task { @MainActor in
            AppModel.shared.start()
            AppModel.shared.launchFile(url)
        }
    }
}

struct ProjectXCommands: Commands {
    let model: AppModel

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("Open Game…") { model.openGameFile() }
                .keyboardShortcut("o")
                .disabled(model.session != nil)
            Button("Add Game Folder…") { model.addGameFolder() }
            Button("Refresh Library") { model.refreshLibrary() }
                .keyboardShortcut("r")
                .disabled(model.session != nil)
        }

        CommandMenu("Emulation") {
            Button(model.session?.status?.paused == true ? "Resume" : "Pause") { model.togglePause() }
                .keyboardShortcut("p")
                .disabled(model.session?.phase != .running)
            Button("Stop") { model.stopGame() }
                .keyboardShortcut(".")
                .disabled(model.session == nil)

            Divider()

            Button(model.session?.status?.docked == false ? "Switch to Docked Mode" : "Switch to Handheld Mode") {
                model.settings.set("dockedMode", !(model.settings.value("dockedMode", default: true)))
            }
            .keyboardShortcut("d", modifiers: [.command, .shift])
            Button("Toggle V-Sync") { model.gameCommand("game.toggleVSync") }
                .keyboardShortcut("v", modifiers: [.command, .shift])
                .disabled(model.session?.phase != .running)
            Button("Take Screenshot") { model.gameCommand("game.screenshot") }
                .keyboardShortcut("s", modifiers: [.command, .shift])
                .disabled(model.session?.phase != .running)
        }

        CommandGroup(after: .appSettings) {
            Button("Install Keys…") { model.installKeys() }
            Button("Install Firmware…") { model.installFirmware() }
        }

        CommandGroup(replacing: .help) {
            Button("Show Data Folder in Finder") { model.revealDataFolder() }
            Button("Show Screenshots in Finder") { model.revealDataFolder("screenshots") }
            Button("Show Logs in Finder") { model.gameCommand("app.revealLogs") }
        }
    }
}
