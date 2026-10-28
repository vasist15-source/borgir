import AppKit
import SwiftUI

/// A game's mods as a list with + and − buttons under it, like the lists in System Settings: + adds mods from a
/// folder or a download, − moves the selected ones to the Trash.
struct ModsSheet: View {
    let game: Game
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @ViewState private var mods: [GameMod] = []
    @ViewState private var selection = Set<GameMod.ID>()
    @ViewState private var adding: String?
    @ViewState private var alert: AlertMessage?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Mods for \(game.name)")
                .font(.headline)
                .lineLimit(1)

            VStack(spacing: 0) {
                List(selection: $selection) {
                    ForEach(mods) { mod in
                        Toggle(mod.name, isOn: Binding(get: { mod.enabled }, set: { enabled in
                            model.setMod(mod, enabled: enabled, for: game)
                            reload()
                        }))
                        .toggleStyle(.checkbox)
                        .help(mod.path)
                        .contextMenu {
                            Button("Show in Finder") {
                                NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: mod.path)])
                            }
                        }
                    }
                }
                .listStyle(.inset(alternatesRowBackgrounds: true))
                .onDeleteCommand(perform: remove)
                .overlay {
                    if mods.isEmpty {
                        Text("No Mods")
                            .foregroundStyle(.secondary)
                    }
                }

                Divider()

                HStack(spacing: 0) {
                    Button(action: add) {
                        Image(systemName: "plus")
                            .frame(width: 24, height: 22)
                    }
                    .help("Add a mod's folder, or the ZIP, 7z or RAR file you downloaded")
                    .disabled(adding != nil)

                    Divider()
                        .frame(height: 14)

                    Button(action: remove) {
                        Image(systemName: "minus")
                            .frame(width: 24, height: 22)
                    }
                    .help("Move the selected mods to the Trash")
                    .disabled(selection.isEmpty || adding != nil)

                    Spacer()

                    if let adding {
                        ProgressView()
                            .controlSize(.small)
                        Text(adding)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .padding(.horizontal, 6)
                    }
                }
                .buttonStyle(.borderless)
                .padding(.horizontal, 2)
            }
            .clipShape(.rect(cornerRadius: 6))
            .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(.separator))

            Text("Turn mods on and off with their checkboxes. Changes apply the next time the game starts.")
                .font(.caption)
                .foregroundStyle(.secondary)

            HStack {
                Button("Show Mods Folder") {
                    model.gameCommand("game.revealFolder", ["id": game.id, "folder": "mods"])
                }

                Spacer()

                Button("Done") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 440, height: 380)
        .task {
            await load()
        }
        .alert(item: $alert) { alert in
            Alert(title: Text(alert.title), message: Text(alert.message))
        }
    }

    private func add() {
        guard let sources = model.chooseMods(for: game) else {
            return
        }

        adding = sources.count == 1 ? "Adding \(sources[0].lastPathComponent)…" : "Adding \(sources.count) mods…"

        Task {
            let problem = await AppModel.installMods(sources, for: game.id)
            adding = nil
            alert = problem
            await load()
        }
    }

    private func remove() {
        let chosen = mods.filter { selection.contains($0.id) }

        model.trashMods(chosen) { problem in
            alert = problem
            selection.removeAll()
            reload()
        }
    }

    private func reload() {
        Task {
            await load()
        }
    }

    private func load() async {
        let id = game.id

        let loaded = await Task.detached(priority: .userInitiated) {
            Core.command("content.get", ["id": id], as: GameContent.self)
        }.value

        if let loaded {
            mods = loaded.mods
        }
    }
}
