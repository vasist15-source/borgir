import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct LibraryView: View {
    @Environment(AppModel.self) private var model
    @ViewState private var isDropTargeted = false

    var body: some View {
        @Bindable var model = model

        // The grid and the detail pane are a view of their own. This one owns the toolbar, and while its body also
        // read the selection, every click re-applied the toolbar and flashed its buttons through their disabled look.
        LibraryContent()
            .background(.background)
            .overlay {
                if isDropTargeted {
                    RoundedRectangle(cornerRadius: 18)
                        .strokeBorder(Color.accentColor, lineWidth: 3)
                        .padding(8)
                        .allowsHitTesting(false)
                }
            }
            .dropDestination(for: URL.self) { urls, _ in
                guard let url = urls.first else {
                    return false
                }

                model.launchFile(url)
                return true
            } isTargeted: { isDropTargeted = $0 }
            .searchable(text: $model.searchText, placement: .toolbar, prompt: "Search Games")
            .navigationTitle("Borgir")
            .navigationSubtitle(subtitle)
            .toolbar {
                ToolbarItemGroup(placement: .primaryAction) {
                    Picker("Sort By", selection: $model.sort) {
                        ForEach(LibrarySort.allCases) { sort in
                            Text(sort.rawValue).tag(sort)
                        }
                    }
                    .pickerStyle(.menu)
                    .help("Sort games")

                    Button {
                        model.refreshLibrary()
                    } label: {
                        Label("Refresh", systemImage: "arrow.clockwise")
                    }
                    .help("Scan game folders again")
                    .disabled(model.libraryLoading)

                    Button {
                        model.openGameFile()
                    } label: {
                        Label("Open Game", systemImage: "play.rectangle")
                    }
                    .help("Start a game file that is not in your library")

                    ProfileMenu()
                }
            }
            .onKeyPress(.return) {
                guard let game = model.selectedGame else {
                    return .ignored
                }

                model.launch(game)
                return .handled
            }
            .onKeyPress(.escape) {
                guard model.selectedGameID != nil else {
                    return .ignored
                }

                model.selectedGameID = nil
                return .handled
            }
    }

    private var subtitle: String {
        if model.libraryLoading {
            return "Scanning…"
        }

        return model.games.count == 1 ? "1 game" : "\(model.games.count) games"
    }
}

/// The game grid and the selected game's detail pane in the inspector.
private struct LibraryContent: View {
    @Environment(AppModel.self) private var model

    // Fixed-size tiles: opening the detail pane narrows the grid on every frame of its animation, and tiles that
    // stretched with the width resized (and stuttered) all the way through it.
    private let columns = [GridItem(.adaptive(minimum: 188, maximum: 188), spacing: 24, alignment: .top)]

    var body: some View {
        library
            .inspector(isPresented: Binding(get: { model.selectedGame != nil }, set: { if !$0 { model.selectedGameID = nil } })) {
                Group {
                    if let game = model.selectedGame {
                        GameDetailView(game: game)
                    }
                }
                .inspectorColumnWidth(min: 300, ideal: 340, max: 440)
            }
    }

    private var library: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                if model.needsSetup {
                    SetupBanner()
                }

                if model.libraryLoading && model.games.isEmpty {
                    ProgressView(scanningText)
                        .frame(maxWidth: .infinity, minHeight: 280)
                } else if model.visibleGames.isEmpty {
                    emptyState
                        .frame(maxWidth: .infinity, minHeight: 360)
                } else {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 28) {
                        ForEach(model.visibleGames) { game in
                            GameTile(game: game, isSelected: model.selectedGameID == game.id)
                                .onTapGesture(count: 2) { model.launch(game) }
                                // Select on the first click without waiting to see whether a second one follows.
                                .simultaneousGesture(TapGesture().onEnded { model.selectedGameID = game.id })
                                .contextMenu { contextMenu(for: game) }
                        }
                    }
                }
            }
            .padding(28)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background {
            // Clicking empty space closes the detail pane.
            Color.clear
                .contentShape(.rect)
                .onTapGesture { model.selectedGameID = nil }
        }
    }

    private var scanningText: String {
        model.libraryFound > 0 ? "Scanning \(model.libraryLoaded) of \(model.libraryFound) games…" : "Scanning game folders…"
    }

    @ViewBuilder
    private var emptyState: some View {
        if !model.searchText.isEmpty {
            ContentUnavailableView.search(text: model.searchText)
        } else {
            ContentUnavailableView {
                Label("Your Library is Empty", systemImage: "gamecontroller")
            } description: {
                Text("Add a folder with your games, or drop a game file onto this window.")
            } actions: {
                Button("Add Game Folder…") { model.addGameFolder() }
                    .buttonStyle(.glassProminent)
                Button("Open Game File…") { model.openGameFile() }
                    .buttonStyle(.glass)
            }
        }
    }

    @ViewBuilder
    private func contextMenu(for game: Game) -> some View {
        Button("Play") { model.launch(game) }
        Button("Show Details") { model.selectedGameID = game.id }
        Divider()
        Button("Show in Finder") {
            NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: game.path)])
        }
        Button("Show Save Data in Finder") { model.gameCommand("game.revealFolder", ["id": game.id, "folder": "save"]) }
        Button("Show Mods Folder in Finder") { model.gameCommand("game.revealFolder", ["id": game.id, "folder": "mods"]) }
        Button("Show Shader Cache in Finder") { model.gameCommand("game.revealFolder", ["id": game.id, "folder": "cache"]) }
        Divider()
        Button("Copy Title ID") {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(game.id.uppercased(), forType: .string)
        }
    }
}

private struct GameTile: View {
    let game: Game
    let isSelected: Bool
    @ViewState private var isHovered = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            GameIcon(path: game.icon, name: game.name)
                .aspectRatio(1, contentMode: .fit)
                .clipShape(.rect(cornerRadius: 16))
                .overlay {
                    RoundedRectangle(cornerRadius: 16)
                        .strokeBorder(isSelected ? Color.accentColor : .white.opacity(0.08), lineWidth: isSelected ? 3 : 1)
                }
                .shadow(color: .black.opacity(isHovered ? 0.35 : 0.18), radius: isHovered ? 14 : 6, y: isHovered ? 8 : 3)
                .scaleEffect(isHovered ? 1.03 : 1)

            VStack(alignment: .leading, spacing: 2) {
                Text(game.name)
                    .font(.headline)
                    .lineLimit(2)
                Text(game.developer)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                if let played = game.timePlayedText {
                    Text(played)
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                }
            }
        }
        .contentShape(.rect)
        .onHover { hovering in
            withAnimation(.snappy(duration: 0.18)) {
                isHovered = hovering
            }
        }
        .help("\(game.name) · \(game.version) · \(game.id.uppercased())")
    }
}

struct GameIcon: View {
    let path: String?
    let name: String

    var body: some View {
        if let image = IconCache.shared.image(at: path) {
            Image(nsImage: image)
                .resizable()
                .interpolation(.high)
        } else {
            ZStack {
                LinearGradient(colors: [.indigo, .purple], startPoint: .topLeading, endPoint: .bottomTrailing)
                Text(String(name.prefix(1)))
                    .font(.system(size: 54, weight: .bold, design: .rounded))
                    .foregroundStyle(.white.opacity(0.9))
            }
        }
    }
}

/// Keeps decoded icons around so scrolling the grid doesn't decode them again.
@MainActor
final class IconCache {
    static let shared = IconCache()

    private let cache = NSCache<NSString, NSImage>()

    func image(at path: String?) -> NSImage? {
        guard let path, !path.isEmpty else {
            return nil
        }

        if let cached = cache.object(forKey: path as NSString) {
            return cached
        }

        guard let image = NSImage(contentsOfFile: path) else {
            return nil
        }

        cache.setObject(image, forKey: path as NSString)
        return image
    }
}

private struct SetupBanner: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.title2)
                .foregroundStyle(.yellow)

            VStack(alignment: .leading, spacing: 6) {
                Text(title)
                    .font(.headline)
                Text("Borgir uses the keys and firmware dumped from your own Switch. They are read from \(model.info?.dataDir ?? "the Ryujinx data folder").")
                    .font(.callout)
                    .foregroundStyle(.secondary)

                HStack {
                    if model.info?.keysInstalled == false {
                        Button("Install Keys…") { model.installKeys() }
                            .buttonStyle(.glassProminent)
                    }
                    if model.info?.firmwareVersion == nil {
                        Button("Install Firmware…") { model.installFirmware() }
                            .buttonStyle(.glass)
                    }
                }
                .padding(.top, 4)
            }

            Spacer()
        }
        .padding(18)
        .glassEffect(.regular, in: .rect(cornerRadius: 18))
    }

    private var title: String {
        switch (model.info?.keysInstalled ?? true, model.info?.firmwareVersion != nil) {
        case (false, false):
            return "Production keys and firmware are missing"
        case (false, true):
            return "Production keys are missing"
        default:
            return "Firmware is missing"
        }
    }
}

private struct ProfileMenu: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        Menu {
            Picker("Profile", selection: Binding(get: { model.info?.activeProfile }, set: { id in
                if let profile = model.info?.profiles.first(where: { $0.id == id }) {
                    model.selectProfile(profile)
                }
            })) {
                ForEach(model.info?.profiles ?? []) { profile in
                    Label {
                        Text(profile.name)
                    } icon: {
                        ProfilePicture(path: profile.image, size: 18)
                    }
                    .tag(Optional(profile.id))
                }
            }
            .pickerStyle(.inline)
            .labelsHidden()

            Divider()

            Button("Manage Profiles…") {
                UserDefaults.standard.set("profiles", forKey: "settingsTab")
                openSettings()
            }
        } label: {
            Label {
                Text(model.activeProfile?.name ?? "Profile")
            } icon: {
                ProfilePicture(path: model.activeProfile?.image, size: 24)
            }
        }
        .help("\(model.activeProfile?.name ?? "No profile") · Choose the profile games start with")
    }
}
