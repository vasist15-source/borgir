import AppKit
import QuartzCore
import SwiftUI

/// Borgir has one main window, and closing it quits the app. SwiftUI only does that by itself for an app whose
/// only scene is that window, and this one also has a Settings scene.
@MainActor
final class MainWindow: NSObject {
    static let shared = MainWindow()

    private(set) weak var window: NSWindow?
    private var closeObserver: NSObjectProtocol?
    private var fullScreenObservers: [NSObjectProtocol] = []

    /// Whether the window is in full screen or, during a transition, whether it will be.
    private(set) var targetFullScreen = false
    private var transition = 0
    private var inTransition = false
    /// A change asked for during a transition: AppKit ignores toggles in the middle of one.
    private var pendingFullScreen: Bool?

    func attach(_ window: NSWindow) {
        guard window !== self.window else {
            return
        }

        self.window = window

        // The close button quits like Cmd+Q, so the player is asked first while a game runs.
        if let closeButton = window.standardWindowButton(.closeButton) {
            closeButton.target = self
            closeButton.action = #selector(closeButtonClicked(_:))
        }

        // Any other close (Cmd+W, Window › Close) has already happened when this runs; quit after it.
        if let closeObserver {
            NotificationCenter.default.removeObserver(closeObserver)
        }

        closeObserver = NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { _ in
            // From the run loop rather than a dispatch block, so AppKit's wait for the core's shutdown isn't nested
            // inside the main dispatch queue.
            NSApp.perform(#selector(NSApplication.terminate(_:)), with: nil, afterDelay: 0)
        }

        targetFullScreen = window.styleMask.contains(.fullScreen)
        inTransition = false
        pendingFullScreen = nil

        for observer in fullScreenObservers {
            NotificationCenter.default.removeObserver(observer)
        }

        let center = NotificationCenter.default

        fullScreenObservers = [
            center.addObserver(forName: NSWindow.willEnterFullScreenNotification, object: window, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.fullScreenWillChange(to: true) }
            },
            center.addObserver(forName: NSWindow.willExitFullScreenNotification, object: window, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.fullScreenWillChange(to: false) }
            },
            center.addObserver(forName: NSWindow.didEnterFullScreenNotification, object: window, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.fullScreenDidChange() }
            },
            center.addObserver(forName: NSWindow.didExitFullScreenNotification, object: window, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.fullScreenDidChange() }
            },
        ]
    }

    @objc private func closeButtonClicked(_ sender: Any?) {
        NSApp.terminate(sender)
    }

    var isFullScreen: Bool {
        window?.styleMask.contains(.fullScreen) ?? false
    }

    /// Enters or leaves full screen. Asked during a transition, it waits for that transition to end.
    func setFullScreen(_ fullScreen: Bool) {
        guard let window else {
            return
        }

        if inTransition {
            pendingFullScreen = fullScreen
            return
        }

        if window.styleMask.contains(.fullScreen) != fullScreen {
            window.toggleFullScreen(nil)
        }
    }

    /// Back in the library after a game, the window leaves full screen unless it was in full screen before the game
    /// started. A game can enter it through the "Start games in full screen" setting, the overlay's button or the
    /// green button, and the library in full screen has no window buttons or Dock.
    func leaveFullScreenAfterGame(wasFullScreen: Bool) {
        guard !wasFullScreen else {
            return
        }

        // After SwiftUI has swapped the game for the library.
        DispatchQueue.main.async {
            self.setFullScreen(false)
        }
    }

    private func fullScreenWillChange(to fullScreen: Bool) {
        targetFullScreen = fullScreen
        inTransition = true
        transition += 1

        // AppKit tells a failed transition only to the window's delegate, which SwiftUI owns; don't wait forever.
        let current = transition

        DispatchQueue.main.asyncAfter(deadline: .now() + 3) {
            if self.inTransition && self.transition == current {
                self.fullScreenDidChange()
            }
        }
    }

    private func fullScreenDidChange() {
        inTransition = false

        guard let window else {
            return
        }

        targetFullScreen = window.styleMask.contains(.fullScreen)

        // Leaving full screen puts the toolbar back as it was on entering it, and a game hides it.
        if !targetFullScreen && AppModel.shared.session == nil {
            window.toolbar?.isVisible = true
        }

        if let pending = pendingFullScreen {
            pendingFullScreen = nil

            // Once AppKit has finished with this transition.
            DispatchQueue.main.async {
                self.setFullScreen(pending)
            }
        }
    }
}

/// Reports the NSWindow a SwiftUI view ends up in.
struct WindowReader: NSViewRepresentable {
    let onWindow: (NSWindow) -> Void

    func makeNSView(context: Context) -> NSView {
        ReportingView(onWindow: onWindow)
    }

    func updateNSView(_ nsView: NSView, context: Context) {}

    private final class ReportingView: NSView {
        let onWindow: (NSWindow) -> Void

        init(onWindow: @escaping (NSWindow) -> Void) {
            self.onWindow = onWindow
            super.init(frame: .zero)
        }

        required init?(coder: NSCoder) {
            fatalError("init(coder:) is not used")
        }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()

            if let window {
                onWindow(window)
            }
        }
    }
}

/// PX_TEST drives the interface without a mouse, for checking it from scripts. Steps run in order, separated by
/// commas: "wait:<seconds>", "select:<title id>", "scrollPane" (the detail pane to its end), "settings:<tab>", "close"
/// (the close button), "closeWindow" (Cmd+W), "play:<title id>", "stop" (the running game), "report" (whether a game
/// is running and the window is in full screen), "command:<name>;key=value;…" (a core command and its reply),
/// "avatarPicker:<profile name>", "profilePicture:<profile name>/<avatar index>/<background hex>". Steps that report
/// write to the PX_TEST_LOG file, or NSLog without one.
@MainActor
enum InterfaceTest {
    static func run(_ script: String) {
        FrameLog.startIfAsked()

        Task { @MainActor in
            for step in script.split(separator: ",").map({ $0.trimmingCharacters(in: .whitespaces) }) {
                let parts = step.split(separator: ":", maxSplits: 1).map(String.init)
                let argument = parts.count > 1 ? parts[1] : ""

                switch parts.first ?? "" {
                case "wait":
                    try? await Task.sleep(for: .seconds(Double(argument) ?? 1))
                case "select":
                    FrameLog.mark("select \(argument)")
                    AppModel.shared.selectedGameID = argument.lowercased()
                case "settings":
                    if !argument.isEmpty {
                        UserDefaults.standard.set(argument, forKey: "settingsTab")
                    }
                    // The app menu's Settings… item (Cmd+,); SwiftUI ignores the older showSettingsWindow: action.
                    if let appMenu = NSApp.mainMenu?.items.first?.submenu,
                       let index = appMenu.items.firstIndex(where: { $0.keyEquivalent == "," }) {
                        appMenu.performActionForItem(at: index)
                    }
                case "close":
                    MainWindow.shared.window?.standardWindowButton(.closeButton)?.performClick(nil)
                case "closeWindow":
                    MainWindow.shared.window?.performClose(nil)
                case "scrollPane":
                    scrollDetailPaneToBottom()
                case "stop":
                    AppModel.shared.stopGame()
                case "play":
                    if let game = AppModel.shared.games.first(where: { $0.id.lowercased() == argument.lowercased() }) {
                        AppModel.shared.launch(game)
                    } else {
                        NSLog("PX_TEST: no game \(argument) in the library")
                    }
                case "report":
                    log("report: game=\(AppModel.shared.session != nil) fullScreen=\(MainWindow.shared.isFullScreen)")
                case "command":
                    // command:<name>;key=value;key=value — the reply goes to the test log. A value "@<profile name>"
                    // stands for that profile's id.
                    let fields = argument.split(separator: ";").map(String.init)
                    var arguments: [String: Any] = [:]

                    for field in fields.dropFirst() {
                        let pair = field.split(separator: "=", maxSplits: 1).map(String.init)
                        let value = pair.count > 1 ? pair[1] : ""

                        // "#2" is a number, true and false are booleans, "null" is null; anything else a string.
                        switch value {
                        case "true", "false":
                            arguments[pair[0]] = value == "true"
                        case "null":
                            arguments[pair[0]] = NSNull()
                        case _ where value.hasPrefix("#"):
                            arguments[pair[0]] = Double(value.dropFirst()) ?? 0
                        case _ where value.hasPrefix("@"):
                            arguments[pair[0]] = profile(named: String(value.dropFirst()))?.id ?? value
                        default:
                            arguments[pair[0]] = value
                        }
                    }

                    let reply = Core.command(fields.first ?? "", arguments)
                    let json = (try? JSONSerialization.data(withJSONObject: reply, options: [.sortedKeys])).flatMap { String(data: $0, encoding: .utf8) } ?? "?"
                    log("command \(fields.first ?? ""): \(json.prefix(400))")
                    AppModel.shared.reloadInfo()
                case "avatarPicker":
                    AppModel.shared.avatarPickerProfile = profile(named: argument)
                case "profilePicture":
                    // profilePicture:<profile name>/<avatar index>/<background hex>, as the avatar picker sets it.
                    let parts = argument.split(separator: "/").map(String.init)
                    let avatars = Core.command("profiles.avatars")["avatars"] as? [String] ?? []

                    if parts.count == 3, let target = profile(named: parts[0]), let index = Int(parts[1]), avatars.indices.contains(index),
                       let image = NSImage(contentsOfFile: avatars[index]), let rgb = UInt32(parts[2], radix: 16),
                       let jpeg = ProfileImage.jpeg(from: image, background: NSColor(red: CGFloat(rgb >> 16 & 0xFF) / 255, green: CGFloat(rgb >> 8 & 0xFF) / 255, blue: CGFloat(rgb & 0xFF) / 255, alpha: 1), fill: false) {
                        AppModel.shared.setProfilePicture(target, jpeg: jpeg)
                        log("profilePicture: \(target.name) avatar \(index) of \(avatars.count), \(jpeg.count) bytes")
                    } else {
                        log("profilePicture: could not set \(argument) (\(avatars.count) avatars)")
                    }
                case "gameSettings":
                    // gameSettings:<title id> opens that game's settings sheet; select it first.
                    NotificationCenter.default.post(name: .pxShowGameSettings, object: argument.lowercased())
                case "playerEditor":
                    // playerEditor:<Player1…Handheld> opens a player in Settings › Input; open that tab first.
                    NotificationCenter.default.post(name: .pxShowPlayerEditor, object: argument)
                case "controllerRequest":
                    // controllerRequest:<min>/<max>/<style+style…> shows the controller prompt as a game would.
                    let parts = argument.split(separator: "/").map(String.init)
                    let request: [String: Any] = [
                        "min": Int(parts.first ?? "") ?? 1,
                        "max": Int(parts.count > 1 ? parts[1] : "") ?? 4,
                        "styles": (parts.count > 2 ? parts[2] : "ProController+JoyconPair").split(separator: "+").map(String.init),
                        "players": (1...8).map { "Player\($0)" } + ["Handheld"],
                        "docked": true,
                    ]
                    AppModel.shared.controllerRequest = ControllerRequest(request) { checkAgain in
                        log("controllerRequest: answered \(checkAgain)")
                    }
                default:
                    NSLog("PX_TEST: unknown step \(step)")
                }
            }
        }
    }

    private static func profile(named name: String) -> Profile? {
        AppModel.shared.info?.profiles.first { $0.name == name }
    }

    /// PX_TEST_LOG=<file> collects what the steps report; without it they go to NSLog.
    private static func log(_ line: String) {
        guard let path = ProcessInfo.processInfo.environment["PX_TEST_LOG"] else {
            NSLog("PX_TEST \(line)")
            return
        }

        if !FileManager.default.fileExists(atPath: path) {
            FileManager.default.createFile(atPath: path, contents: nil)
        }

        if let handle = FileHandle(forWritingAtPath: path) {
            handle.seekToEndOfFile()
            handle.write(Data((line + "\n").utf8))
            try? handle.close()
        }
    }

    /// The detail pane is the rightmost scroll view in the main window.
    private static func scrollDetailPaneToBottom() {
        guard let contentView = MainWindow.shared.window?.contentView else {
            return
        }

        var scrollViews: [NSScrollView] = []
        var pending: [NSView] = [contentView]

        while let view = pending.popLast() {
            if let scrollView = view as? NSScrollView {
                scrollViews.append(scrollView)
            }

            pending.append(contentsOf: view.subviews)
        }

        guard let pane = scrollViews.max(by: { $0.convert($0.bounds, to: nil).minX < $1.convert($1.bounds, to: nil).minX }),
              let document = pane.documentView else {
            return
        }

        document.scroll(NSPoint(x: 0, y: document.isFlipped ? document.bounds.height : 0))
    }
}

/// With PX_TEST, PX_FRAME_LOG=<file> appends one line per display frame: the time since the previous frame on the main
/// thread, in milliseconds, plus a line for each "select" step. A long gap is a frame the main thread delivered late.
@MainActor
final class FrameLog: NSObject {
    private static var shared: FrameLog?

    private let output: FileHandle
    private var link: CADisplayLink?
    private var last: CFTimeInterval = 0

    private init(output: FileHandle) {
        self.output = output
    }

    static func startIfAsked() {
        guard shared == nil, let path = ProcessInfo.processInfo.environment["PX_FRAME_LOG"],
              FileManager.default.createFile(atPath: path, contents: nil),
              let output = FileHandle(forWritingAtPath: path)
        else {
            return
        }

        let log = FrameLog(output: output)
        shared = log

        // The main window attaches a moment after launch.
        Task { @MainActor in
            while MainWindow.shared.window == nil {
                try? await Task.sleep(for: .milliseconds(100))
            }

            let link = MainWindow.shared.window!.displayLink(target: log, selector: #selector(frame(_:)))
            link.add(to: .main, forMode: .common)
            log.link = link
        }
    }

    static func mark(_ text: String) {
        shared?.write(String(format: "%.3f %@\n", CACurrentMediaTime(), text))
    }

    @objc private func frame(_ link: CADisplayLink) {
        let now = CACurrentMediaTime()

        if last > 0 {
            write(String(format: "%.3f frame %.1f\n", now, (now - last) * 1000))
        }

        last = now
    }

    private func write(_ line: String) {
        output.write(Data(line.utf8))
    }
}

extension Notification.Name {
    /// Scripted checks open sheets that are otherwise only a click away (see InterfaceTest).
    static let pxShowGameSettings = Notification.Name("PXShowGameSettings")
    static let pxShowPlayerEditor = Notification.Name("PXShowPlayerEditor")
}
