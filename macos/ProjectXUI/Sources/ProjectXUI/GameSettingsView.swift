import AppKit
import SwiftUI

/// Settings one game can have its own value for, in the order the sheet shows them.
enum GameSettingKeys {
    static let graphics = ["resScale", "aspectRatio", "scalingFilter", "scalingFilterLevel", "maxAnisotropy", "vsyncMode", "customRefreshRate"]
    static let system = ["dockedMode", "language", "region", "ignoreMissingServices"]
    static let cpu = ["hypervisor", "memoryManagerMode", "dramSize"]

    static func name(_ key: String) -> String {
        switch key {
        case "resScale": return "resolution"
        case "aspectRatio": return "aspect ratio"
        case "scalingFilter": return "scaling filter"
        case "scalingFilterLevel": return "FSR sharpness"
        case "maxAnisotropy": return "anisotropic filtering"
        case "vsyncMode": return "V-Sync"
        case "customRefreshRate": return "refresh rate"
        case "dockedMode": return "docked mode"
        case "language": return "language"
        case "region": return "region"
        case "ignoreMissingServices": return "missing services setting"
        case "hypervisor": return "Apple Hypervisor setting"
        case "memoryManagerMode": return "memory manager"
        case "dramSize": return "DRAM size"
        default: return key
        }
    }

    /// "resolution, docked mode and DRAM size".
    static func list(_ keys: [String]) -> String {
        let names = keys.map(name)

        switch names.count {
        case 0: return ""
        case 1: return names[0]
        default: return names.dropLast().joined(separator: ", ") + " and " + names.last!
        }
    }
}

/// A setting value as JSON gives it, comparable whatever the number's type.
private enum SettingValue: Hashable {
    case bool(Bool)
    case number(Double)
    case text(String)

    init?(_ value: Any?) {
        switch value {
        case let number as NSNumber where CFGetTypeID(number) == CFBooleanGetTypeID():
            self = .bool(number.boolValue)
        case let number as NSNumber:
            self = .number(number.doubleValue)
        case let text as String:
            self = .text(text)
        default:
            return nil
        }
    }

    var json: Any {
        switch self {
        case .bool(let value): return value
        case .number(let value): return value
        case .text(let value): return value
        }
    }
}

private struct Choice {
    let value: SettingValue
    let label: String
}

/// A game's own settings: each one either follows the global setting or has a value for this game only.
struct GameSettingsSheet: View {
    let game: Game
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @ViewState private var own: [String: Any] = [:]
    @ViewState private var global: [String: Any] = [:]

    private var isRunning: Bool {
        model.session?.game.id == game.id
    }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    row("Resolution", "resScale", [
                        Choice(value: .number(1), label: "Native (720p/1080p)"),
                        Choice(value: .number(2), label: "2x (1440p/2160p)"),
                        Choice(value: .number(3), label: "3x (2160p/3240p)"),
                        Choice(value: .number(4), label: "4x (2880p/4320p)"),
                    ])
                    row("Aspect Ratio", "aspectRatio", model.settings.choices("aspectRatio").map {
                        Choice(value: .text($0), label: SettingNames.aspectRatio($0))
                    })
                    row("Scaling Filter", "scalingFilter", [
                        Choice(value: .text("Bilinear"), label: "Bilinear"),
                        Choice(value: .text("Nearest"), label: "Nearest"),
                        Choice(value: .text("Fsr"), label: "AMD FSR Sharpening"),
                    ])
                    if effective("scalingFilter") == .text("Fsr") {
                        row("Sharpness", "scalingFilterLevel", [0, 25, 50, 80, 100].map {
                            Choice(value: .number(Double($0)), label: "\($0)%")
                        })
                    }
                    row("Anisotropic Filtering", "maxAnisotropy", [
                        Choice(value: .number(-1), label: "Auto"),
                        Choice(value: .number(2), label: "2x"),
                        Choice(value: .number(4), label: "4x"),
                        Choice(value: .number(8), label: "8x"),
                        Choice(value: .number(16), label: "16x"),
                    ])
                    row("V-Sync", "vsyncMode", [
                        Choice(value: .text("Switch"), label: "On (60 Hz, like the Switch)"),
                        Choice(value: .text("Unbounded"), label: "Off (unlimited)"),
                        Choice(value: .text("Custom"), label: "Custom refresh rate"),
                    ])

                    if effective("vsyncMode") == .text("Custom") {
                        row("Refresh Rate", "customRefreshRate", [30, 40, 45, 50, 60, 75, 90, 120, 144].map {
                            Choice(value: .number(Double($0)), label: "\($0) Hz")
                        })
                    }
                } header: {
                    Text("Graphics")
                }

                Section {
                    row("Mode", "dockedMode", [
                        Choice(value: .bool(true), label: "Docked"),
                        Choice(value: .bool(false), label: "Handheld"),
                    ])
                    row("Language", "language", listChoices("language"))
                    row("Region", "region", listChoices("region"))
                    row("Ignore Missing Services", "ignoreMissingServices", onOff)
                } header: {
                    Text("System")
                }

                Section {
                    row("Apple Hypervisor", "hypervisor", onOff)
                    row("Memory Manager", "memoryManagerMode", listChoices("memoryManagerMode"))
                    row("DRAM Size", "dramSize", listChoices("dramSize"))
                } header: {
                    Text("CPU and Memory")
                } footer: {
                    Text(isRunning
                         ? "Graphics settings and the mode change at once. The others apply the next time \(game.name) starts."
                         : "\(game.name) uses these whenever it starts. Settings left on Global follow Borgir's settings.")
                }
            }
            .formStyle(.grouped)

            Divider()

            HStack {
                Button("Use Global Settings for All") {
                    for key in own.keys.sorted() {
                        set(key, nil)
                    }
                }
                .disabled(own.isEmpty)

                Spacer()

                Button("Done") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(16)
        }
        .frame(width: 540, height: 640)
        .navigationTitle("\(game.name) Settings")
        .onAppear(perform: load)
    }

    private var onOff: [Choice] {
        [Choice(value: .bool(true), label: "On"), Choice(value: .bool(false), label: "Off")]
    }

    private func listChoices(_ key: String) -> [Choice] {
        model.settings.choices(key).map { Choice(value: .text($0), label: SettingNames.label(key, $0)) }
    }

    /// The value the game uses: its own, or the global one.
    private func effective(_ key: String) -> SettingValue? {
        SettingValue(own[key]) ?? SettingValue(global[key])
    }

    /// A picker whose first choice follows the global setting (named with its current value) and whose others set
    /// the game's own value.
    private func row(_ title: String, _ key: String, _ choices: [Choice]) -> some View {
        var choices = choices
        let ownValue = SettingValue(own[key])

        // A value set elsewhere (a custom resolution, say) still shows as itself.
        for value in [ownValue, SettingValue(global[key])].compactMap({ $0 }) where !choices.contains(where: { $0.value == value }) {
            choices.append(Choice(value: value, label: describe(value)))
        }

        let globalLabel = SettingValue(global[key]).flatMap { value in choices.first { $0.value == value }?.label } ?? "–"
        let selection = Binding<Int>(
            get: { ownValue.flatMap { value in choices.firstIndex { $0.value == value } } ?? -1 },
            set: { index in set(key, index < 0 ? nil : choices[index].value.json) }
        )

        return Picker(title, selection: selection) {
            Text("Global (\(globalLabel))").tag(-1)
            Divider()
            ForEach(choices.indices, id: \.self) { index in
                Text(choices[index].label).tag(index)
            }
        }
        .fontWeight(ownValue == nil ? .regular : .semibold)
    }

    private func describe(_ value: SettingValue) -> String {
        switch value {
        case .bool(let on): return on ? "On" : "Off"
        case .number(let number): return number.formatted()
        case .text(let text): return SettingNames.spaced(text)
        }
    }

    private func load() {
        apply(Core.command("game.settings.get", ["id": game.id]))
    }

    private func set(_ key: String, _ value: Any?) {
        let reply = Core.command("game.settings.set", ["id": game.id, "key": key, "value": value ?? NSNull()])

        apply(reply)
        model.report(reply, title: "Unable to Change the Setting")

        if isRunning {
            model.settings.reload()
        }
    }

    private func apply(_ reply: [String: Any]) {
        if let own = reply["own"] as? [String: Any] {
            self.own = own
        }

        if let global = reply["global"] as? [String: Any] {
            self.global = global
        }
    }
}

/// In a Settings tab while the running game has its own value for some of the tab's settings.
struct OwnSettingsNote: View {
    let keys: [String]
    @Environment(AppModel.self) private var model

    var body: some View {
        let overridden = keys.filter(model.settings.ownSettings.contains)

        if let session = model.session, !overridden.isEmpty {
            Section {
                Label {
                    Text("\(session.game.name) has its own \(GameSettingKeys.list(overridden)). Changing \(overridden.count == 1 ? "it" : "them") here while it runs changes the game's setting.")
                } icon: {
                    Image(systemName: "gamecontroller")
                }
                .foregroundStyle(.secondary)
            }
        }
    }
}
