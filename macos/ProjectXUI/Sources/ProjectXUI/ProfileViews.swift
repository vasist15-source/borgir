import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// A profile's picture in a circle, or a placeholder when it has none.
struct ProfilePicture: View {
    let path: String?
    let size: CGFloat

    var body: some View {
        Group {
            if let image = IconCache.shared.image(at: path) {
                Image(nsImage: image)
                    .renderingMode(.original)
                    .resizable()
                    .interpolation(.high)
                    .scaledToFill()
            } else {
                Image(systemName: "person.crop.circle.fill")
                    .resizable()
                    .foregroundStyle(.secondary)
            }
        }
        .frame(width: size, height: size)
        .clipShape(.circle)
    }
}

/// Settings › Profiles.
struct ProfileSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        let profiles = model.info?.profiles ?? []

        Form {
            Section {
                ForEach(profiles) { profile in
                    ProfileRow(profile: profile, isActive: profile.id == model.info?.activeProfile) {
                        model.avatarPickerProfile = profile
                    }
                }
            } header: {
                Text("Profiles")
            } footer: {
                if model.session != nil {
                    Text("Stop the game to change profiles.")
                        .foregroundStyle(.orange)
                } else {
                    Text("Games start with the profile in use. Each profile has its own save data. Borgir shares these profiles with other Ryujinx builds.")
                }
            }

            Section {
                Button("Add Profile") {
                    model.addProfile(existing: profiles)
                }
            }
        }
        .formStyle(.grouped)
        .disabled(model.session != nil)
        .sheet(item: $model.avatarPickerProfile) { profile in
            AvatarPicker(profile: profile)
        }
    }
}

private struct ProfileRow: View {
    let profile: Profile
    let isActive: Bool
    let chooseAvatar: () -> Void
    @Environment(AppModel.self) private var model

    var body: some View {
        let isDefault = profile.id == AppModel.defaultProfileID

        HStack(spacing: 12) {
            Button(action: chooseAvatar) {
                ProfilePicture(path: profile.image, size: 44)
            }
            .buttonStyle(.plain)
            .help("Choose a Switch avatar")

            VStack(alignment: .leading, spacing: 2) {
                Text(profile.name)
                    .font(.body.weight(isActive ? .semibold : .regular))
                if isDefault {
                    Text("Default profile")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Spacer(minLength: 8)

            if isActive {
                Label("In Use", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(.secondary)
            } else {
                Button("Use") {
                    model.selectProfile(profile)
                }
                .help("Start games with this profile")
            }

            Menu {
                Button("Rename…") {
                    model.renameProfileWithPrompt(profile)
                }
                Button("Choose Switch Avatar…", action: chooseAvatar)
                Button("Choose Image File…") {
                    model.chooseProfileImageFile(for: profile)
                }
                Divider()
                Button("Delete…", role: .destructive) {
                    model.deleteProfile(profile)
                }
                .disabled(isDefault)
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help("Rename, change the picture or delete")
        }
        .padding(.vertical, 2)
    }
}

/// The Switch's avatars from the installed firmware, on a background colour of the player's choice.
private struct AvatarPicker: View {
    let profile: Profile
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @ViewState private var avatars: [String] = []
    @ViewState private var error: String?
    @ViewState private var selected: String?
    @ViewState private var background = Color.white

    var body: some View {
        VStack(spacing: 16) {
            Text("Choose an Avatar for \(profile.name)")
                .font(.headline)

            Group {
                if let error {
                    ContentUnavailableView("No Avatars", systemImage: "person.crop.circle.badge.questionmark", description: Text(error))
                } else if avatars.isEmpty {
                    ProgressView("Reading the firmware…")
                } else {
                    ScrollView {
                        LazyVGrid(columns: [GridItem(.adaptive(minimum: 76), spacing: 12)], spacing: 12) {
                            ForEach(avatars, id: \.self) { path in
                                avatar(path)
                            }
                        }
                        .padding(4)
                    }
                }
            }
            .frame(height: 380)

            HStack {
                ColorPicker("Background", selection: $background, supportsOpacity: false)
                    .fixedSize()

                Spacer()

                Button("Cancel") { dismiss() }
                    .keyboardShortcut(.cancelAction)

                Button("Use Avatar", action: apply)
                    .keyboardShortcut(.defaultAction)
                    .disabled(selected == nil)
            }
        }
        .padding(20)
        .frame(width: 600)
        .task {
            let reply = await Task.detached(priority: .userInitiated) {
                Core.command("profiles.avatars")
            }.value

            if let paths = reply["avatars"] as? [String], !paths.isEmpty {
                avatars = paths
            } else {
                error = reply["error"] as? String ?? "The firmware's avatars could not be read."
            }
        }
    }

    private func avatar(_ path: String) -> some View {
        let isSelected = selected == path

        return ZStack {
            Circle().fill(background)

            if let image = IconCache.shared.image(at: path) {
                Image(nsImage: image)
                    .resizable()
                    .interpolation(.high)
                    .scaledToFit()
                    .clipShape(.circle)
            }
        }
        .frame(width: 76, height: 76)
        .overlay {
            Circle()
                .strokeBorder(isSelected ? Color.accentColor : .secondary.opacity(0.25), lineWidth: isSelected ? 3 : 1)
        }
        .contentShape(.circle)
        // Double-click uses the avatar right away; the double tap has to come first to be seen.
        .onTapGesture(count: 2) {
            selected = path
            apply()
        }
        .onTapGesture { selected = path }
    }

    private func apply() {
        if let selected, let image = NSImage(contentsOfFile: selected),
           let jpeg = ProfileImage.jpeg(from: image, background: NSColor(background), fill: false) {
            model.setProfilePicture(profile, jpeg: jpeg)
        }

        dismiss()
    }
}

/// Profile pictures as games read them: a 256-pixel square JPEG.
enum ProfileImage {
    /// Draws `image` on `background`, centred and scaled to fill the square (`fill`) or to fit inside it, and encodes
    /// it as a JPEG small enough for games to load.
    static func jpeg(from image: NSImage, background: NSColor, fill: Bool) -> Data? {
        let side = 256
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: side, pixelsHigh: side, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                         bytesPerRow: 0, bitsPerPixel: 0),
              let source = image.cgImage(forProposedRect: nil, context: nil, hints: nil)
        else {
            return nil
        }

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        let context = NSGraphicsContext.current!.cgContext
        context.interpolationQuality = .high

        context.setFillColor((background.usingColorSpace(.deviceRGB) ?? .white).cgColor)
        context.fill(CGRect(x: 0, y: 0, width: side, height: side))

        let width = CGFloat(source.width), height = CGFloat(source.height)
        let scale = fill ? max(CGFloat(side) / width, CGFloat(side) / height) : min(CGFloat(side) / width, CGFloat(side) / height)
        let drawn = CGSize(width: width * scale, height: height * scale)
        context.draw(source, in: CGRect(x: (CGFloat(side) - drawn.width) / 2, y: (CGFloat(side) - drawn.height) / 2,
                                        width: drawn.width, height: drawn.height))

        NSGraphicsContext.restoreGraphicsState()

        // Games read the picture into 128 KiB; a 256-pixel JPEG is far below that, but don't count on it.
        for quality in [0.9, 0.75, 0.6] {
            if let data = rep.representation(using: .jpeg, properties: [.compressionFactor: quality]), data.count < 0x20000 {
                return data
            }
        }

        return nil
    }
}

// MARK: - Profile actions

extension AppModel {
    /// The profile Ryujinx creates on every start; it can't be deleted.
    static let defaultProfileID = "00000000000000010000000000000000"

    func addProfile(existing: [Profile]) {
        let names = Set(existing.map(\.name))
        let suggestion = (2...).lazy.map { "Player \($0)" }.first { !names.contains($0) }!

        guard let name = askForProfileName(title: "Add a Profile", button: "Add", initial: suggestion) else {
            return
        }

        if reportModally(Core.command("profiles.add", ["name": name]), title: "Unable to Add a Profile") {
            reloadInfo()
        }
    }

    func renameProfileWithPrompt(_ profile: Profile) {
        guard let name = askForProfileName(title: "Rename “\(profile.name)”", button: "Rename", initial: profile.name), name != profile.name else {
            return
        }

        reportModally(Core.command("profiles.rename", ["id": profile.id, "name": name]), title: "Unable to Rename the Profile")
        reloadInfo()
    }

    private func askForProfileName(title: String, button: String, initial: String) -> String? {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = "Games show this name. It can be up to 32 letters long."
        let field = NSTextField(string: initial)
        field.frame = NSRect(x: 0, y: 0, width: 260, height: 24)
        alert.accessoryView = field
        alert.addButton(withTitle: button)
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = field

        guard alert.runModal() == .alertFirstButtonReturn else {
            return nil
        }

        let name = field.stringValue.trimmingCharacters(in: .whitespaces)
        return name.isEmpty ? nil : name
    }

    func setProfilePicture(_ profile: Profile, jpeg: Data) {
        if reportModally(Core.command("profiles.setImage", ["id": profile.id, "image": jpeg.base64EncodedString()]), title: "Unable to Change the Picture") {
            reloadInfo()
        }
    }

    func chooseProfileImageFile(for profile: Profile) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.message = "Choose a picture for \(profile.name). It is cropped to a square."

        guard panel.runModal() == .OK, let url = panel.url else {
            return
        }

        guard let image = NSImage(contentsOf: url), let jpeg = ProfileImage.jpeg(from: image, background: .white, fill: true) else {
            reportModally(["ok": false, "error": "\(url.lastPathComponent) is not a picture Borgir can read."], title: "Unable to Change the Picture")
            return
        }

        setProfilePicture(profile, jpeg: jpeg)
    }

    func deleteProfile(_ profile: Profile) {
        let confirm = NSAlert()
        confirm.alertStyle = .warning
        confirm.messageText = "Delete “\(profile.name)”?"
        confirm.informativeText = "This also deletes the profile's save data for every game. It can't be undone."
        let delete = confirm.addButton(withTitle: "Delete")
        delete.hasDestructiveAction = true
        confirm.addButton(withTitle: "Cancel")

        guard confirm.runModal() == .alertFirstButtonReturn else {
            return
        }

        reportModally(Core.command("profiles.delete", ["id": profile.id]), title: "Unable to Delete the Profile")
        reloadInfo()
    }

    /// Like `report`, but as its own dialog: profile changes happen in the Settings window, in front of the main one.
    @discardableResult
    private func reportModally(_ reply: [String: Any], title: String) -> Bool {
        guard reply["ok"] as? Bool != true else {
            return true
        }

        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = reply["error"] as? String ?? "The emulator core reported an error."
        alert.runModal()
        return false
    }
}
