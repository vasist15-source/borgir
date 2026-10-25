import AppKit
import SwiftUI

/// Settings › Input: the players and their controllers, plus the Switch keyboard and mouse.
struct InputSettings: View {
    @Environment(AppModel.self) private var model
    @ViewState private var input: InputReply?
    @ViewState private var editing: InputPlayer?

    var body: some View {
        let players = input?.players ?? []
        let free = players.filter { !$0.hasDevice }

        Form {
            Section {
                if !players.contains(where: \.hasDevice) {
                    Text("No players yet. Add one to play.")
                        .foregroundStyle(.secondary)
                }

                ForEach(players.filter(\.hasDevice)) { player in
                    PlayerRow(player: player) { editing = player }
                }

                HStack {
                    Menu("Add Player") {
                        ForEach(free) { player in
                            Menu(player.title) {
                                ForEach(input?.devices ?? []) { device in
                                    Button(device.name) {
                                        add(player, device: device)
                                    }
                                }
                            }
                        }
                    }
                    .fixedSize()
                    .disabled(free.isEmpty)

                    Spacer()

                    Button("Refresh Devices") { reload() }
                }
            } header: {
                Text("Players")
            } footer: {
                Text("Each player is one controller to the game. Changes apply at once, even while a game runs, and are shared with other Ryujinx builds.")
            }

            Section("Keyboard and Mouse") {
                Toggle("Keyboard as a Switch keyboard", isOn: Binding(get: { input?.enableKeyboard ?? false }, set: { set("enableKeyboard", $0) }))
                Toggle("Mouse as a Switch mouse", isOn: Binding(get: { input?.enableMouse ?? false }, set: { set("enableMouse", $0) }))
            }
        }
        .formStyle(.grouped)
        .onAppear { reload() }
        .sheet(item: $editing, onDismiss: reload) { player in
            PlayerEditor(playerID: player.player)
        }
        .onReceive(NotificationCenter.default.publisher(for: .pxShowPlayerEditor)) { note in
            editing = input?.players.first { $0.player == note.object as? String && $0.hasDevice }
        }
    }

    private func reload() {
        input = Core.command("input.get", as: InputReply.self)
    }

    private func add(_ player: InputPlayer, device: InputDevice) {
        model.report(Core.command("input.setDevice", ["player": player.player, "id": device.id]), title: "Unable to Add the Player")
        reload()
        editing = input?.players.first { $0.player == player.player && $0.hasDevice }
    }

    private func set(_ key: String, _ value: Bool) {
        model.settings.set(key, value)
        reload()
    }
}

private struct PlayerRow: View {
    let player: InputPlayer
    let edit: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: player.isKeyboard ? "keyboard" : "gamecontroller")
                .font(.title2)
                .foregroundStyle(.secondary)
                .frame(width: 30)

            VStack(alignment: .leading, spacing: 1) {
                Text(player.title)
                Text("\(player.name ?? "") · \(InputNames.type(player.type))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            Spacer(minLength: 8)

            if player.connected == false {
                Label("Not connected", systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }

            Button("Edit…", action: edit)
        }
        .padding(.vertical, 2)
    }
}

/// One player: device, controller type, button layout and, for controllers, sticks, rumble and motion.
struct PlayerEditor: View {
    let playerID: String
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @ViewState private var input: InputReply?
    /// The button waiting for a key or controller button.
    @ViewState private var listening: String?
    @ViewState private var keyMonitor: Any?

    private var player: InputPlayer? {
        input?.players.first { $0.player == playerID }
    }

    var body: some View {
        VStack(spacing: 0) {
            if let player, player.hasDevice {
                Form {
                    deviceSection(player)
                    buttonsSection(player, side: .left)
                    buttonsSection(player, side: .right)

                    if let options = player.options {
                        controllerSection(options)
                    }
                }
                .formStyle(.grouped)
            } else {
                ContentUnavailableView("No Controller", systemImage: "gamecontroller", description: Text("\(InputNames.player(playerID)) has no device."))
                    .frame(maxHeight: .infinity)
            }

            Divider()

            HStack {
                Button("Restore Default Layout") {
                    stopListening()
                    command("input.reset", [:], title: "Unable to Restore the Layout")
                }
                .disabled(player?.hasDevice != true)

                Spacer()

                Button("Done") {
                    stopListening()
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
            .padding(16)
        }
        // Inside the Settings window, which is 600 points tall.
        .frame(width: 560, height: 540)
        .onAppear(perform: reload)
        .onDisappear(perform: stopListening)
    }

    // MARK: Sections

    private func deviceSection(_ player: InputPlayer) -> some View {
        Section {
            Picker("Device", selection: Binding(get: { player.device ?? "" }, set: { id in
                stopListening()
                command("input.setDevice", ["id": id], title: "Unable to Change the Device")

                if id.isEmpty {
                    dismiss()
                }
            })) {
                ForEach(input?.devices ?? []) { device in
                    Text(device.name).tag(device.id)
                }

                if let device = player.device, !(input?.devices.contains { $0.id == device } ?? false) {
                    Text("\(player.name ?? device) (not connected)").tag(device)
                }

                Divider()
                Text("None (Remove \(player.title))").tag("")
            }

            Picker("Controller Type", selection: Binding(get: { player.type ?? "" }, set: { type in
                command("input.setType", ["type": type], title: "Unable to Change the Controller Type")
            })) {
                ForEach(player.types, id: \.self) { type in
                    Text(InputNames.type(type)).tag(type)
                }
            }
        } header: {
            Text(player.title)
        } footer: {
            Text(player.isKeyboard
                 ? "Click a button, then press the key for it. Esc cancels; right-click a button to clear it."
                 : "Click a button, then press it on the controller. Right-click a button to clear it.")
        }
    }

    private enum Side {
        case left
        case right
    }

    @ViewBuilder
    private func buttonsSection(_ player: InputPlayer, side: Side) -> some View {
        let prefix = side == .left ? "Left" : "Right"

        Section(side == .left ? "Left Joy-Con" : "Right Joy-Con") {
            if side == .left {
                bindingRow("ZL", "ButtonZl", player)
                bindingRow("L", "ButtonL", player)
                bindingRow("−", "ButtonMinus", player)
                bindingRow("Up", "DpadUp", player)
                bindingRow("Down", "DpadDown", player)
                bindingRow("Left", "DpadLeft", player)
                bindingRow("Right", "DpadRight", player)
            } else {
                bindingRow("ZR", "ButtonZr", player)
                bindingRow("R", "ButtonR", player)
                bindingRow("+", "ButtonPlus", player)
                bindingRow("A", "ButtonA", player)
                bindingRow("B", "ButtonB", player)
                bindingRow("X", "ButtonX", player)
                bindingRow("Y", "ButtonY", player)
            }

            if player.isKeyboard {
                bindingRow("Stick Up", "\(prefix)StickUp", player)
                bindingRow("Stick Down", "\(prefix)StickDown", player)
                bindingRow("Stick Left", "\(prefix)StickLeft", player)
                bindingRow("Stick Right", "\(prefix)StickRight", player)
            } else {
                Picker("Stick", selection: Binding(get: { player.bindings?["\(prefix)Stick"] ?? "Unbound" }, set: { stick in
                    command("input.setBinding", ["button": "\(prefix)Stick", "value": stick == "Unbound" ? "" : stick], title: "Unable to Change the Stick")
                })) {
                    Text("Left Stick").tag("Left")
                    Text("Right Stick").tag("Right")
                    Text("None").tag("Unbound")
                }
            }

            bindingRow("Stick Press", "\(prefix)StickButton", player)
            bindingRow("SL", "\(prefix)Sl", player)
            bindingRow("SR", "\(prefix)Sr", player)
        }
    }

    private func controllerSection(_ options: InputOptions) -> some View {
        Section("Sticks, Rumble and Motion") {
            DeadzoneSlider(title: "Left stick dead zone", value: options.deadzoneLeft) { option("deadzoneLeft", $0) }
            DeadzoneSlider(title: "Right stick dead zone", value: options.deadzoneRight) { option("deadzoneRight", $0) }
            Toggle("Invert left stick up and down", isOn: Binding(get: { options.invertLeftY }, set: { option("invertLeftY", $0) }))
            Toggle("Invert right stick up and down", isOn: Binding(get: { options.invertRightY }, set: { option("invertRightY", $0) }))
            Toggle("Rumble", isOn: Binding(get: { options.rumble }, set: { option("rumble", $0) }))
            Toggle("Motion controls", isOn: Binding(get: { options.motion }, set: { option("motion", $0) }))
        }
    }

    private func bindingRow(_ title: String, _ button: String, _ player: InputPlayer) -> some View {
        let isListening = listening == button
        let label = isListening
            ? (player.isKeyboard ? "Press a key…" : "Press a button…")
            : InputNames.binding(player.bindings?[button], keyboard: player.isKeyboard)

        return LabeledContent(title) {
            Button {
                toggleListening(button)
            } label: {
                Text(label)
                    .frame(minWidth: 150)
            }
            .buttonStyle(.bordered)
            .tint(isListening ? .accentColor : nil)
            .contextMenu {
                Button("Clear") {
                    stopListening()
                    command("input.setBinding", ["button": button, "value": ""], title: "Unable to Clear the Button")
                }
            }
        }
    }

    // MARK: Listening for a key or button

    private func toggleListening(_ button: String) {
        let wasListening = listening == button
        stopListening()

        guard !wasListening, let player else {
            return
        }

        listening = button

        if player.isKeyboard {
            // The Settings window gets the keys; take the next one (or a modifier going down) before anything else does.
            keyMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .flagsChanged]) { event in
                handleKey(event)
            }
            return
        }

        let id = playerID

        Task {
            let reply = await Task.detached(priority: .userInitiated) {
                Core.command("input.listen", ["player": id, "timeout": 8.0])
            }.value

            // Clicking elsewhere meanwhile moved on.
            guard listening == button else {
                return
            }

            listening = nil

            if let value = reply["value"] as? String {
                command("input.setBinding", ["button": button, "value": value], title: "Unable to Change the Button")
            } else {
                model.report(reply, title: "Unable to Read the Controller")
            }
        }
    }

    private func handleKey(_ event: NSEvent) -> NSEvent? {
        guard let button = listening else {
            return event
        }

        if event.type == .flagsChanged {
            guard InputNames.isModifierDown(event) else {
                return nil
            }
        } else if event.isARepeat {
            return nil
        }

        stopListening()

        // Esc cancels, as in the rest of macOS.
        if event.keyCode != 53 {
            command("input.setBinding", ["button": button, "macKeyCode": Int(event.keyCode)], title: "Unable to Change the Button")
        }

        return nil
    }

    private func stopListening() {
        if let keyMonitor {
            NSEvent.removeMonitor(keyMonitor)
            self.keyMonitor = nil
        }

        if listening != nil, player?.isKeyboard == false {
            Core.command("input.cancelListen")
        }

        listening = nil
    }

    // MARK: Commands

    private func option(_ name: String, _ value: Any) {
        command("input.setOption", ["option": name, "value": value], title: "Unable to Change the Setting")
    }

    private func command(_ name: String, _ arguments: [String: Any], title: String) {
        var arguments = arguments
        arguments["player"] = playerID

        model.report(Core.command(name, arguments), title: title)
        reload()
    }

    private func reload() {
        input = Core.command("input.get", as: InputReply.self)
    }
}

/// A dead zone slider that saves when the drag ends, not on every step.
private struct DeadzoneSlider: View {
    let title: String
    let value: Double
    let commit: (Double) -> Void
    @ViewState private var draft: Double?

    var body: some View {
        LabeledContent(title) {
            HStack {
                Slider(value: Binding(get: { draft ?? value }, set: { draft = $0 }), in: 0...0.5) { editing in
                    if !editing, let draft {
                        commit(draft)
                        self.draft = nil
                    }
                }
                Text((draft ?? value), format: .percent.precision(.fractionLength(0)))
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                    .frame(width: 40, alignment: .trailing)
            }
        }
    }
}

// MARK: - The game asking for controllers

/// A game's request for controllers (the Switch's controller applet), waiting for the player's answer.
@MainActor
final class ControllerRequest: Identifiable {
    let id = UUID()
    let minPlayers: Int
    let maxPlayers: Int
    /// The controller types the game accepts.
    let styles: [String]
    /// The players it can use.
    let players: [String]
    let docked: Bool
    private var reply: ((Bool) -> Void)?

    init(_ request: [String: Any], reply: @escaping (Bool) -> Void) {
        minPlayers = request["min"] as? Int ?? 1
        maxPlayers = request["max"] as? Int ?? 1
        styles = request["styles"] as? [String] ?? []
        players = request["players"] as? [String] ?? []
        docked = request["docked"] as? Bool ?? true
        self.reply = reply
    }

    /// True has the game check the controllers again; false lets it go on as they are. Answers once.
    func answer(_ checkAgain: Bool) {
        reply?(checkAgain)
        reply = nil
    }
}

struct ControllerRequestSheet: View {
    let request: ControllerRequest
    @Environment(AppModel.self) private var model
    @Environment(\.openSettings) private var openSettings
    @ViewState private var input: InputReply?

    var body: some View {
        let name = model.session?.game.name ?? "The game"
        let players = (input?.players ?? []).filter { $0.hasDevice && request.players.contains($0.player) }

        VStack(alignment: .leading, spacing: 16) {
            Label("Connect Controllers", systemImage: "gamecontroller")
                .font(.title2.weight(.semibold))

            Text("\(name) wants \(playerCount) using \(request.styles.map(InputNames.type).joined(separator: ", ")).")
                .fixedSize(horizontal: false, vertical: true)

            VStack(alignment: .leading, spacing: 8) {
                if players.isEmpty {
                    Text("No players have a controller yet.")
                        .foregroundStyle(.secondary)
                }

                ForEach(players) { player in
                    let fits = player.connected == true && request.styles.contains(player.type ?? "")

                    HStack {
                        Image(systemName: fits ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                            .foregroundStyle(fits ? .green : .orange)
                        Text(player.title)
                        Text("\(player.name ?? "") · \(InputNames.type(player.type))")
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.5), in: .rect(cornerRadius: 10))

            if request.docked && request.styles == ["Handheld"] {
                Text("The game wants handheld controls, which docked mode turns off. Switch to handheld mode in the overlay.")
                    .font(.callout)
                    .foregroundStyle(.orange)
            }

            HStack {
                Button("Controller Settings…") {
                    UserDefaults.standard.set("input", forKey: "settingsTab")
                    openSettings()
                }

                Spacer()

                Button("Continue Anyway") { finish(false) }
                Button("Check Again") { finish(true) }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(24)
        .frame(width: 520)
        .interactiveDismissDisabled()
        .task {
            // Settings › Input is open beside this while the player sets controllers up; keep the list current.
            while !Task.isCancelled {
                input = Core.command("input.get", as: InputReply.self)
                try? await Task.sleep(for: .seconds(1.5))
            }
        }
    }

    private var playerCount: String {
        let noun = request.maxPlayers == 1 ? "player" : "players"

        return request.minPlayers == request.maxPlayers
            ? "\(request.minPlayers) \(noun)"
            : "\(request.minPlayers) to \(request.maxPlayers) \(noun)"
    }

    private func finish(_ checkAgain: Bool) {
        request.answer(checkAgain)
        model.controllerRequest = nil
    }
}

// MARK: - Names

enum InputNames {
    static func player(_ id: String) -> String {
        id.hasPrefix("Player") ? "Player \(id.dropFirst("Player".count))" : id
    }

    static func type(_ type: String?) -> String {
        switch type {
        case "ProController": return "Pro Controller"
        case "JoyconPair": return "Joy-Con Pair"
        case "JoyconLeft": return "Left Joy-Con"
        case "JoyconRight": return "Right Joy-Con"
        case "Handheld": return "Handheld"
        default: return type ?? ""
        }
    }

    static func binding(_ value: String?, keyboard: Bool) -> String {
        guard let value, value != "Unbound", value != "Unknown" else {
            return "None"
        }

        return keyboard ? key(value) : button(value)
    }

    /// Controller buttons go by the Switch's names for their positions; SDL matches A, B, X and Y by their labels
    /// (✕ ○ □ △ on PlayStation pads).
    static func button(_ value: String) -> String {
        switch value {
        case "A": return "A / ✕"
        case "B": return "B / ○"
        case "X": return "X / □"
        case "Y": return "Y / △"
        case "LeftStick": return "Left Stick Press"
        case "RightStick": return "Right Stick Press"
        case "LeftShoulder": return "Left Shoulder"
        case "RightShoulder": return "Right Shoulder"
        case "LeftTrigger": return "Left Trigger"
        case "RightTrigger": return "Right Trigger"
        case "DpadUp": return "D-pad Up"
        case "DpadDown": return "D-pad Down"
        case "DpadLeft": return "D-pad Left"
        case "DpadRight": return "D-pad Right"
        // Back and Start are the core's other names for Minus and Plus.
        case "Minus", "Back": return "Minus / Back"
        case "Plus", "Start": return "Plus / Start"
        case "Guide": return "Home / Guide"
        case "Misc1": return "Capture / Share"
        case "Touchpad": return "Touchpad"
        case "SingleLeftTrigger0": return "SL (Left Joy-Con)"
        case "SingleRightTrigger0": return "SR (Left Joy-Con)"
        case "SingleLeftTrigger1": return "SL (Right Joy-Con)"
        case "SingleRightTrigger1": return "SR (Right Joy-Con)"
        default:
            if value.hasPrefix("Paddle") {
                return "Paddle \(value.dropFirst("Paddle".count))"
            }

            return SettingNames.spaced(value)
        }
    }

    static func key(_ value: String) -> String {
        let names: [String: String] = [
            "Up": "↑", "Down": "↓", "Left": "←", "Right": "→",
            "ShiftLeft": "Left Shift", "ShiftRight": "Right Shift",
            "ControlLeft": "Left Control", "ControlRight": "Right Control",
            "AltLeft": "Left Option", "AltRight": "Right Option",
            "WinLeft": "Left Command", "WinRight": "Right Command",
            "Enter": "Return", "BackSpace": "Delete", "Delete": "Forward Delete", "Escape": "Esc",
            "Plus": "=", "Minus": "-", "BracketLeft": "[", "BracketRight": "]", "Semicolon": ";", "Quote": "'",
            "Comma": ",", "Period": ".", "Slash": "/", "BackSlash": "\\", "Tilde": "`", "Grave": "§",
            "CapsLock": "Caps Lock", "PageUp": "Page Up", "PageDown": "Page Down", "NumLock": "Clear",
            "KeypadDecimal": "Keypad .", "KeypadMultiply": "Keypad *", "KeypadAdd": "Keypad +",
            "KeypadSubtract": "Keypad -", "KeypadDivide": "Keypad /", "KeypadEnter": "Keypad Enter",
        ]

        if let name = names[value] {
            return name
        }

        if value.hasPrefix("Number"), value.count == 7 {
            return String(value.last!)
        }

        if value.hasPrefix("Keypad"), value.count == 7 {
            return "Keypad \(value.last!)"
        }

        return value
    }

    /// Whether a flagsChanged event is its modifier key going down rather than up.
    static func isModifierDown(_ event: NSEvent) -> Bool {
        let flag: NSEvent.ModifierFlags

        switch event.keyCode {
        case 0x38, 0x3C: flag = .shift
        case 0x3B, 0x3E: flag = .control
        case 0x3A, 0x3D: flag = .option
        case 0x37, 0x36: flag = .command
        case 0x39: flag = .capsLock
        default: return false
        }

        return event.modifierFlags.contains(flag)
    }
}
