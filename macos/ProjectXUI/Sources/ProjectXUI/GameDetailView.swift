import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// The library's right-hand pane: the selected game, a Play button, and the game's update, DLC and mods.
struct GameDetailView: View {
    let game: Game
    @Environment(AppModel.self) private var model
    @ViewState private var content: GameContent?
    @ViewState private var loads = 0
    @ViewState private var showsDetails = false
    @ViewState private var showsSettings = false

    var body: some View {
        Form {
            Section {
                header
            }

            if showsDetails {
                Section {
                    LabeledContent("Version", value: game.version)
                    LabeledContent("Title ID", value: game.id.uppercased())
                    LabeledContent("Format", value: "\(game.fileType.uppercased()) · \(ByteCountFormatter.string(fromByteCount: game.fileSize, countStyle: .file))")
                    LabeledContent("Played", value: game.timePlayedText ?? "Not yet")
                    if let lastPlayed = game.lastPlayedDate {
                        LabeledContent("Last played", value: lastPlayed.formatted(.relative(presentation: .named)))
                    }
                }

                settingsSection
                updateSection
                dlcSection
                modsSection
                cheatsSection

                Section("Folders") {
                    HStack(spacing: 8) {
                        folderButton("Save Data", systemImage: "externaldrive", folder: "save")
                        folderButton("Shaders", systemImage: "cube.transparent", folder: "cache")
                        Button {
                            NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: game.path)])
                        } label: {
                            Label("Game File", systemImage: "doc")
                                .frame(maxWidth: .infinity)
                        }
                        .help("Show the game file in Finder")
                    }
                    .buttonStyle(.glass)
                    .labelStyle(VerticalLabelStyle())
                }
            }
        }
        .formStyle(.grouped)
        .task(id: game.id) {
            await load()
        }
        .task {
            // The header comes first and the rest once the inspector has slid in: building the whole pane in the
            // slide's first frame held that frame for about 50 ms, a visible stutter.
            try? await Task.sleep(for: .milliseconds(350))
            showsDetails = true
        }
        .onChange(of: model.games) {
            // The core rescanned or applied an update; the lists may have changed with it.
            reload()
        }
        .sheet(isPresented: $showsSettings, onDismiss: reload) {
            GameSettingsSheet(game: game)
        }
        .onReceive(NotificationCenter.default.publisher(for: .pxShowGameSettings)) { note in
            if note.object as? String == game.id {
                showsSettings = true
            }
        }
    }

    private var header: some View {
        VStack(spacing: 14) {
            GameIcon(path: game.icon, name: game.name)
                .frame(width: 168, height: 168)
                .clipShape(.rect(cornerRadius: 24))
                .shadow(color: .black.opacity(0.25), radius: 12, y: 6)

            VStack(spacing: 3) {
                Text(game.name)
                    .font(.title2.weight(.semibold))
                    .multilineTextAlignment(.center)
                Text(game.developer)
                    .foregroundStyle(.secondary)
            }

            Button {
                model.launch(game)
            } label: {
                Label("Play", systemImage: "play.fill")
                    .font(.headline)
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.glassProminent)
            .controlSize(.extraLarge)
            .disabled(model.session != nil)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 8)
        .overlay(alignment: .topTrailing) {
            Button {
                model.selectedGameID = nil
            } label: {
                Image(systemName: "xmark")
                    .font(.body.weight(.semibold))
                    .foregroundStyle(.secondary)
                    .frame(width: 26, height: 26)
                    .contentShape(.circle)
            }
            .buttonStyle(.plain)
            .help("Close")
        }
    }

    // MARK: Settings

    private var settingsSection: some View {
        Section("Settings") {
            HStack {
                let own = content?.ownSettings ?? []

                VStack(alignment: .leading, spacing: 1) {
                    Text(own.isEmpty ? "Global settings" : "Own settings")
                    Text(own.isEmpty ? "Follows Borgir's settings" : "Its own \(GameSettingKeys.list(own))")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Spacer()

                Button("Edit…") { showsSettings = true }
            }
        }
    }

    // MARK: Update

    @ViewBuilder
    private var updateSection: some View {
        Section {
            updateRow(title: "Base game", subtitle: "No update", path: "", selected: content?.updates.contains(where: \.selected) != true)

            ForEach(content?.updates ?? []) { update in
                updateRow(title: "Version \(update.version)", subtitle: URL(fileURLWithPath: update.path).lastPathComponent, path: update.path, selected: update.selected)
                    .contextMenu {
                        Button("Show in Finder") { reveal(update.path) }
                        if !update.bundled {
                            Button("Remove from List") {
                                model.removeContent("update", path: update.path, for: game)
                                reload()
                            }
                        }
                    }
            }

            Button("Add Update or DLC File…") {
                model.addContentFiles(to: game)
                reload()
            }
        } header: {
            Text("Update")
        } footer: {
            Text("Files in your update and DLC folders (Settings › General) are added automatically.")
        }
    }

    private func updateRow(title: String, subtitle: String, path: String, selected: Bool) -> some View {
        Button {
            guard !selected else {
                return
            }

            model.selectUpdate(path, for: game)
            reload()
        } label: {
            HStack(spacing: 10) {
                Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                    .font(.title3)
                    .foregroundStyle(selected ? Color.accentColor : Color.secondary)

                VStack(alignment: .leading, spacing: 1) {
                    Text(title)
                    Text(subtitle)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }

                Spacer(minLength: 0)
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .disabled(model.session != nil)
    }

    // MARK: DLC

    @ViewBuilder
    private var dlcSection: some View {
        Section("DLC") {
            if let dlc = content?.dlc, !dlc.isEmpty {
                ForEach(dlc) { item in
                    Toggle(isOn: Binding(get: { item.enabled }, set: { enabled in
                        model.setDLC(item, enabled: enabled, for: game)
                        reload()
                    })) {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(item.name)
                            Text(item.titleId)
                                .font(.caption.monospaced())
                                .foregroundStyle(.secondary)
                        }
                    }
                    .contextMenu {
                        Button("Show in Finder") { reveal(item.path) }
                        if !item.bundled {
                            Button("Remove from List") {
                                model.removeContent("dlc", path: item.path, for: game)
                                reload()
                            }
                        }
                    }
                }
            } else {
                Text("No DLC")
                    .foregroundStyle(.secondary)
            }
        }
    }

    // MARK: Mods

    @ViewBuilder
    private var modsSection: some View {
        Section {
            if let mods = content?.mods, !mods.isEmpty {
                ForEach(mods) { mod in
                    Toggle(mod.name, isOn: Binding(get: { mod.enabled }, set: { enabled in
                        model.setMod(mod, enabled: enabled, for: game)
                        reload()
                    }))
                    .help(mod.path)
                    .contextMenu {
                        Button("Show in Finder") { reveal(mod.path) }
                        Button("Move to Trash…") {
                            model.trashMod(mod) { reload() }
                        }
                    }
                }
            } else {
                Text("No mods")
                    .foregroundStyle(.secondary)
            }

            HStack {
                Button("Import Mod…") {
                    model.importMods(for: game) { reload() }
                }

                Spacer()

                Button("Show Mods Folder") {
                    model.gameCommand("game.revealFolder", ["id": game.id, "folder": "mods"])
                }
            }
        } header: {
            Text("Mods")
        } footer: {
            Text("Import a mod's folder or its download (ZIP, 7z or RAR): Borgir finds the mod inside and installs it for this game, where Ryujinx keeps mods too. Mod changes apply the next time the game starts.")
        }
    }

    // MARK: Cheats

    @ViewBuilder
    private var cheatsSection: some View {
        Section {
            if let cheats = content?.cheats, !cheats.isEmpty {
                ForEach(cheats) { cheat in
                    Toggle(isOn: Binding(get: { cheat.enabled }, set: { enabled in
                        model.setCheat(cheat, enabled: enabled, for: game)
                        reload()
                    })) {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(cheat.name)
                            Text("\(cheat.source) · build \(cheat.buildId.prefix(8))")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    }
                    .help(cheat.path)
                    .contextMenu {
                        Button("Show in Finder") { reveal(cheat.path) }
                    }
                }
            } else {
                Text("No cheats")
                    .foregroundStyle(.secondary)
            }

            Button("Add Cheat File…") {
                guard let modsDir = content?.modsDir else {
                    return
                }

                model.addCheatFiles(to: game, modsDir: modsDir)
                reload()
            }
        } header: {
            Text("Cheats")
        } footer: {
            Text("Cheats turn on and off at once, even while the game runs. A cheat file only works with the game version it was made for.")
        }
    }

    // MARK: Helpers

    private func folderButton(_ title: String, systemImage: String, folder: String) -> some View {
        Button {
            model.gameCommand("game.revealFolder", ["id": game.id, "folder": folder])
        } label: {
            Label(title, systemImage: systemImage)
                .frame(maxWidth: .infinity)
        }
        .help("Show this game's \(title.lowercased()) folder in Finder")
    }

    private func reveal(_ path: String) {
        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
    }

    private func reload() {
        Task {
            await load()
        }
    }

    /// Lists the game's updates, DLC and mods. The core does that off the main thread: the first listing after launch
    /// takes a few hundred milliseconds (it scans the mod folders), which froze the pane as it slid in.
    private func load() async {
        loads += 1
        let load = loads
        let id = game.id

        let loaded = await Task.detached(priority: .userInitiated) {
            Core.command("content.get", ["id": id], as: GameContent.self)
        }.value

        // A later load (another game, or a change made meanwhile) has the newer answer. A failed one (the game is
        // missing while the library rescans after a game stops) keeps what the pane shows.
        if load == loads, let loaded {
            content = loaded
        }
    }
}

private struct VerticalLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        VStack(spacing: 4) {
            configuration.icon
                .font(.title3)
            configuration.title
                .font(.caption)
        }
        .padding(.vertical, 4)
    }
}

// MARK: - Content actions

extension AppModel {
    func selectUpdate(_ path: String, for game: Game) {
        report(Core.command("content.selectUpdate", ["id": game.id, "path": path]), title: "Unable to Change the Update")
    }

    func setDLC(_ dlc: GameDLC, enabled: Bool, for game: Game) {
        report(Core.command("content.setDlc", ["id": game.id, "path": dlc.path, "contentPath": dlc.contentPath, "enabled": enabled]), title: "Unable to Change DLC")
    }

    func setMod(_ mod: GameMod, enabled: Bool, for game: Game) {
        report(Core.command("content.setMod", ["id": game.id, "path": mod.path, "enabled": enabled]), title: "Unable to Change the Mod")
    }

    func setCheat(_ cheat: GameCheat, enabled: Bool, for game: Game) {
        report(Core.command("content.setCheat", ["id": game.id, "key": cheat.key, "enabled": enabled]), title: "Unable to Change the Cheat")
    }

    /// Copies Atmosphère cheat files (named after the build id they are for, like 0123456789ABCDEF.txt) into the
    /// game's cheats folder.
    func addCheatFiles(to game: Game, modsDir: String) {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        panel.allowedContentTypes = [.plainText]
        panel.prompt = "Add"
        panel.message = "Choose cheat files for \(game.name). Each is named after the game build it is for, like 0123456789ABCDEF.txt."

        guard panel.runModal() == .OK else {
            return
        }

        let cheatsDir = URL(fileURLWithPath: modsDir).appendingPathComponent("cheats")
        var problems: [String] = []

        for source in panel.urls {
            let buildId = source.deletingPathExtension().lastPathComponent

            guard buildId.count == 16, UInt64(buildId, radix: 16) != nil else {
                problems.append("\(source.lastPathComponent): the name isn't a 16-digit build id, so Borgir can't tell which game version it is for.")
                continue
            }

            let destination = cheatsDir.appendingPathComponent(source.lastPathComponent.uppercased().replacingOccurrences(of: ".TXT", with: ".txt"))

            do {
                try FileManager.default.createDirectory(at: cheatsDir, withIntermediateDirectories: true)

                if FileManager.default.fileExists(atPath: destination.path) {
                    problems.append("\(source.lastPathComponent): this game already has a cheat file for that build.")
                } else {
                    try FileManager.default.copyItem(at: source, to: destination)
                }
            } catch {
                problems.append("\(source.lastPathComponent): \(error.localizedDescription)")
            }
        }

        if !problems.isEmpty {
            alert = AlertMessage(title: panel.urls.count == 1 ? "Unable to Add the Cheat File" : "Some Cheat Files Were Not Added", message: problems.joined(separator: "\n"))
        }
    }

    func removeContent(_ kind: String, path: String, for game: Game) {
        report(Core.command("content.remove", ["id": game.id, "kind": kind, "path": path]))
    }

    func addContentFiles(to game: Game) {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        panel.allowedContentTypes = ["nsp", "xci"].compactMap { UTType(filenameExtension: $0) }
        panel.prompt = "Add"
        panel.message = "Choose update or DLC files for \(game.name)."

        guard panel.runModal() == .OK else {
            return
        }

        var problems: [String] = []

        for url in panel.urls {
            let reply = Core.command("content.add", ["id": game.id, "path": url.path])

            if reply["ok"] as? Bool != true {
                problems.append(reply["error"] as? String ?? url.lastPathComponent)
            }
        }

        if !problems.isEmpty {
            alert = AlertMessage(title: panel.urls.count == 1 ? "Unable to Add the File" : "Some Files Were Not Added", message: problems.joined(separator: "\n"))
        }
    }

    /// Imports mods from folders or downloaded archives, off the main thread since mods can be large. The core finds
    /// the mods inside and checks which game they are for.
    func importMods(for game: Game, completion: @escaping @MainActor () -> Void) {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = true
        panel.allowedContentTypes = [.folder, .zip] + ["7z", "rar"].compactMap { UTType(filenameExtension: $0) }
        panel.prompt = "Import"
        panel.message = "Choose mods for \(game.name): a mod's folder, or the ZIP, 7z or RAR file you downloaded."

        guard panel.runModal() == .OK else {
            return
        }

        let sources = panel.urls
        let id = game.id
        busyMessage = sources.count == 1 ? "Importing \(sources[0].lastPathComponent)…" : "Importing \(sources.count) mods…"

        Task.detached(priority: .userInitiated) {
            var notes: [String] = []
            var failures = 0

            for source in sources {
                let reply = Core.command("content.importMod", ["id": id, "path": source.path])
                let imported = reply["imported"] as? [String] ?? []
                let skipped = reply["skipped"] as? [String] ?? []

                if reply["ok"] as? Bool != true {
                    failures += 1
                    notes.append("\(source.lastPathComponent): \(reply["error"] as? String ?? "it could not be imported.")")
                    continue
                }

                // A download with several mods (often variants of one) installs them all, turned on.
                if imported.count > 1 {
                    notes.append("\(source.lastPathComponent) had \(imported.count) mods: \(imported.joined(separator: ", ")). Turn off the ones you don't want.")
                }

                notes.append(contentsOf: skipped.map { "Not imported from \(source.lastPathComponent): \($0)" })
            }

            let summary = notes
            let allFailed = failures == sources.count

            await MainActor.run {
                self.busyMessage = nil

                if !summary.isEmpty {
                    self.alert = AlertMessage(title: allFailed ? "Unable to Import the Mod" : "Mods Imported", message: summary.joined(separator: "\n\n"))
                }

                completion()
            }
        }
    }

    func trashMod(_ mod: GameMod, completion: @escaping @MainActor () -> Void) {
        let confirm = NSAlert()
        confirm.messageText = "Move “\(mod.name)” to the Trash?"
        confirm.informativeText = mod.path
        confirm.addButton(withTitle: "Move to Trash")
        confirm.addButton(withTitle: "Cancel")

        guard confirm.runModal() == .alertFirstButtonReturn else {
            return
        }

        NSWorkspace.shared.recycle([URL(fileURLWithPath: mod.path)]) { _, error in
            Task { @MainActor in
                if let error {
                    self.alert = AlertMessage(title: "Unable to Move the Mod to the Trash", message: error.localizedDescription)
                }

                completion()
            }
        }
    }
}
