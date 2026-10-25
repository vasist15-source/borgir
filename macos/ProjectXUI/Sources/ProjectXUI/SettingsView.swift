import AppKit
import SwiftUI

struct SettingsView: View {
    @AppStorage("settingsTab") private var tab = "general"

    var body: some View {
        TabView(selection: $tab) {
            Tab("General", systemImage: "gearshape", value: "general") {
                GeneralSettings()
            }
            Tab("Graphics", systemImage: "display", value: "graphics") {
                GraphicsSettings()
            }
            Tab("System", systemImage: "gamecontroller", value: "system") {
                SystemSettings()
            }
            Tab("CPU", systemImage: "cpu", value: "cpu") {
                CPUSettings()
            }
            Tab("Input", systemImage: "keyboard", value: "input") {
                InputSettings()
            }
            Tab("Profiles", systemImage: "person.2", value: "profiles") {
                ProfileSettings()
            }
        }
        .frame(width: 640, height: 600)
    }
}

/// A picker over one of the core's list settings, showing readable names.
private struct ChoicePicker: View {
    let title: String
    let key: String
    @Environment(AppModel.self) private var model

    var body: some View {
        Picker(title, selection: model.settings.string(key)) {
            ForEach(model.settings.choices(key), id: \.self) { choice in
                Text(SettingNames.label(key, choice)).tag(choice)
            }
        }
    }
}

/// A list of folders with a remove button on each and an add button below.
private struct FolderList: View {
    let folders: [String]
    let emptyText: String
    let remove: (String) -> Void
    let add: () -> Void

    var body: some View {
        if folders.isEmpty {
            Text(emptyText)
                .foregroundStyle(.secondary)
        }

        ForEach(folders, id: \.self) { folder in
            HStack {
                Image(systemName: "folder")
                    .foregroundStyle(.secondary)
                Text(folder)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .help(folder)
                Spacer()
                Button {
                    NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: folder)])
                } label: {
                    Image(systemName: "magnifyingglass.circle")
                }
                .buttonStyle(.borderless)
                .help("Show in Finder")
                Button {
                    remove(folder)
                } label: {
                    Image(systemName: "minus.circle.fill")
                }
                .buttonStyle(.borderless)
                .help("Remove this folder")
            }
        }

        Button("Add Folder…", action: add)
    }
}

private struct GeneralSettings: View {
    @Environment(AppModel.self) private var model
    @AppStorage("showGameOverlay") private var showOverlay = true

    var body: some View {
        let settings = model.settings

        Form {
            Section("Game Folders") {
                FolderList(folders: model.info?.gameDirs ?? [], emptyText: "No game folders yet", remove: model.removeGameFolder, add: model.addGameFolder)
            }

            Section {
                FolderList(folders: model.info?.autoloadDirs ?? [], emptyText: "No update or DLC folders", remove: model.removeAutoloadFolder, add: model.addAutoloadFolder)
            } header: {
                Text("Update and DLC Folders")
            } footer: {
                Text("Updates and DLC in these folders are added to the games they belong to whenever the library is scanned.")
            }

            Section("Keys and Firmware") {
                LabeledContent("Production keys") {
                    HStack {
                        Text(model.info?.keysInstalled == true ? "Installed" : "Missing")
                            .foregroundStyle(model.info?.keysInstalled == true ? Color.secondary : Color.red)
                        Button("Install…") { model.installKeys() }
                    }
                }
                LabeledContent("Firmware") {
                    HStack {
                        Text(model.info?.firmwareVersion ?? "Not installed")
                            .foregroundStyle(model.info?.firmwareVersion == nil ? Color.red : Color.secondary)
                        Button("Install…") { model.installFirmware() }
                    }
                }
                LabeledContent("Data folder") {
                    HStack {
                        Text(model.info?.dataDir ?? "")
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .foregroundStyle(.secondary)
                            .help(model.info?.dataDir ?? "")
                        Button("Show in Finder") { model.revealDataFolder() }
                    }
                }
            }

            Section("While Playing") {
                Toggle("Start games in full screen", isOn: settings.bool("startFullscreen"))
                Toggle("Ask before stopping a game or quitting", isOn: settings.bool("confirmExit"))
                Toggle("Show the game overlay", isOn: $showOverlay)
                Picker("Hide the pointer", selection: settings.string("hideCursor")) {
                    Text("Never").tag("Never")
                    Text("When idle").tag("OnIdle")
                    Text("Always").tag("Always")
                }
            }
        }
        .formStyle(.grouped)
    }
}

private struct GraphicsSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let settings = model.settings

        Form {
            OwnSettingsNote(keys: GameSettingKeys.graphics)

            Section {
                GraphicsQuickSettings()
            } header: {
                Text("Image")
            } footer: {
                Text("These also appear in the game overlay, and apply while a game runs.")
            }

            Section("Frame Pacing") {
                Picker("V-Sync", selection: settings.string("vsyncMode")) {
                    Text("On (60 Hz, like the Switch)").tag("Switch")
                    Text("Off (unlimited)").tag("Unbounded")
                    Text("Custom refresh rate").tag("Custom")
                }

                if settings.value("vsyncMode", default: "") == "Custom" {
                    Stepper(value: settings.int("customRefreshRate"), in: 1...999) {
                        LabeledContent("Refresh rate", value: "\(settings.value("customRefreshRate", default: 60)) Hz")
                    }
                }
            }

            Section {
                LabeledContent("Backend", value: "Metal")
                Picker("Multithreading", selection: settings.string("backendThreading")) {
                    Text("Automatic").tag("Auto")
                    Text("On").tag("On")
                    Text("Off").tag("Off")
                }
                Toggle("Shader cache", isOn: settings.bool("shaderCache"))
                    .help("Keeps translated shaders on disk so later launches skip translating them again.")
                Toggle("Macro HLE", isOn: settings.bool("macroHLE"))
                    .help("Runs some GPU macros natively instead of emulating them. Faster; turn off only to debug.")
                Toggle("Color space passthrough", isOn: settings.bool("colorSpacePassthrough"))
            } header: {
                Text("Renderer")
            } footer: {
                Text("Multithreading, the shader cache and Macro HLE apply the next time a game starts.")
            }
        }
        .formStyle(.grouped)
    }
}

private struct SystemSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let settings = model.settings
        let volume = settings.double("volume")

        Form {
            OwnSettingsNote(keys: GameSettingKeys.system)

            Section {
                Toggle("Docked mode", isOn: settings.bool("dockedMode"))
                ChoicePicker(title: "Language", key: "language")
                ChoicePicker(title: "Region", key: "region")
            } header: {
                Text("Console")
            } footer: {
                Text("Language and region apply the next time a game starts.")
            }

            Section {
                ChoicePicker(title: "Output", key: "audioBackend")
                LabeledContent("Volume") {
                    HStack {
                        Slider(value: volume, in: 0...1)
                        Text(volume.wrappedValue, format: .percent.precision(.fractionLength(0)))
                            .monospacedDigit()
                            .foregroundStyle(.secondary)
                            .frame(width: 44, alignment: .trailing)
                    }
                }
            } header: {
                Text("Audio")
            } footer: {
                if settings.value("audioBackend", default: "") == "Dummy" {
                    Text("Audio is off. Choose Core Audio to hear games.")
                        .foregroundStyle(.orange)
                } else {
                    Text("The output applies the next time a game starts.")
                }
            }

            Section("Network") {
                Toggle("Guest internet access", isOn: settings.bool("internetAccess"))
            }

            Section("Compatibility") {
                Toggle("Ignore missing services", isOn: settings.bool("ignoreMissingServices"))
                Toggle("File system integrity checks", isOn: settings.bool("fsIntegrityChecks"))
            }
        }
        .formStyle(.grouped)
    }
}

private struct CPUSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let settings = model.settings

        Form {
            OwnSettingsNote(keys: GameSettingKeys.cpu)

            Section {
                Toggle("Apple Hypervisor", isOn: settings.bool("hypervisor"))
                Toggle("Profiled translation cache (PPTC)", isOn: settings.bool("ptc"))
                Toggle("Low-power PPTC", isOn: settings.bool("lowPowerPtc"))
                    .disabled(!settings.value("ptc", default: true))
            } footer: {
                Text("Apple Hypervisor runs game code directly on the CPU and is the fastest choice on Apple silicon. PPTC keeps translated code between launches when the hypervisor is off.")
            }

            Section {
                ChoicePicker(title: "Memory manager", key: "memoryManagerMode")
                ChoicePicker(title: "DRAM size", key: "dramSize")
                Toggle("Low-latency garbage collector", isOn: settings.bool("gcLowLatency"))
            } header: {
                Text("Memory")
            } footer: {
                Text("Most games need 4 GiB. Some mods, such as texture packs, ask for more. These apply the next time a game starts.")
            }
        }
        .formStyle(.grouped)
    }
}
