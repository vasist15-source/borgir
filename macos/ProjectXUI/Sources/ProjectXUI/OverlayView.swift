import AppKit
import Observation
import SwiftUI

/// When the overlay is on screen. It shows on mouse movement and hides again after a few quiet seconds,
/// unless a popover is open or the game is paused.
@MainActor
@Observable
final class OverlayState {
    var visible = true
    var popoverOpen = false
    /// A short confirmation shown at the top of the screen, such as "Screenshot saved".
    var toast: String?

    private var hideTask: Task<Void, Never>?
    private var toastTask: Task<Void, Never>?

    func toggle() {
        if visible {
            hideTask?.cancel()
            visible = false
        } else {
            noteActivity()
        }
    }

    func show(toast message: String) {
        toast = message
        toastTask?.cancel()
        toastTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(2))

            guard !Task.isCancelled else {
                return
            }

            self?.toast = nil
        }
    }

    func noteActivity(seconds: Double = 3) {
        if !visible {
            visible = true
        }

        scheduleHide(after: seconds)
    }

    func scheduleHide(after seconds: Double = 3) {
        hideTask?.cancel()
        hideTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))

            guard !Task.isCancelled, let self, !self.popoverOpen else {
                return
            }

            self.visible = false

            if AppModel.shared.settings.value("hideCursor", default: "OnIdle") != "Never" {
                NSCursor.setHiddenUntilMouseMoves(true)
            }
        }
    }
}

struct OverlayView: View {
    let session: GameSession
    @Bindable var state: OverlayState
    @Environment(AppModel.self) private var model
    @AppStorage("showGameOverlay") private var showOverlay = true

    private var paused: Bool { session.status?.paused == true }
    private var isVisible: Bool { showOverlay && session.phase == .running && (state.visible || paused || state.popoverOpen) }

    var body: some View {
        ZStack(alignment: .top) {
            VStack {
                HStack(alignment: .top) {
                    titleBadge
                    Spacer()
                    if let shaders = session.status?.shaders, shaders > 0 {
                        Label("Building \(shaders) shaders", systemImage: "cube.transparent")
                            .font(.callout.weight(.medium))
                            .padding(.horizontal, 14)
                            .padding(.vertical, 8)
                            .glassEffect(.regular, in: .capsule)
                    }
                }

                Spacer()

                controlBar
            }
            .opacity(isVisible ? 1 : 0)
            .allowsHitTesting(isVisible)
            .animation(.easeInOut(duration: 0.25), value: isVisible)

            // Confirmations show even while the controls are hidden.
            if let toast = state.toast {
                Label(toast, systemImage: "checkmark.circle.fill")
                    .font(.callout.weight(.medium))
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                    .glassEffect(.regular, in: .capsule)
                    .padding(.top, isVisible ? 64 : 0)
                    .transition(.move(edge: .top).combined(with: .opacity))
                    .allowsHitTesting(false)
            }
        }
        .padding(22)
        .animation(.snappy, value: state.toast)
        .onChange(of: state.popoverOpen) { _, open in
            if !open {
                state.scheduleHide()
            }
        }
    }

    private var titleBadge: some View {
        HStack(spacing: 12) {
            GameIcon(path: session.game.icon, name: session.game.name)
                .frame(width: 34, height: 34)
                .clipShape(.rect(cornerRadius: 8))

            VStack(alignment: .leading, spacing: 1) {
                Text(session.game.name)
                    .font(.headline)
                    .lineLimit(1)
                Text(statusLine)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.leading, 8)
        .padding(.trailing, 16)
        .padding(.vertical, 8)
        .glassEffect(.regular, in: .capsule)
    }

    private var statusLine: String {
        guard let status = session.status else {
            return "Metal"
        }

        if status.paused {
            return "Paused"
        }

        let mode = status.docked ? "Docked" : "Handheld"
        let scale = status.resScale == 1 ? "" : String(format: " · %.1fx", status.resScale)

        return String(format: "%.0f FPS · %.1f ms · %@%@ · Metal", status.fps, status.frameTime, mode, scale)
    }

    private var controlBar: some View {
        GlassEffectContainer(spacing: 14) {
            HStack(spacing: 10) {
                overlayButton(paused ? "Resume" : "Pause", systemImage: paused ? "play.fill" : "pause.fill") {
                    model.togglePause()
                }

                overlayButton("Stop", systemImage: "stop.fill") {
                    confirmStop()
                }

                Divider()
                    .frame(height: 22)

                overlayButton(session.status?.docked == false ? "Handheld" : "Docked", systemImage: session.status?.docked == false ? "iphone.landscape" : "tv") {
                    model.settings.set("dockedMode", !(session.status?.docked ?? true), forGame: true)
                }

                overlayButton(vsyncLabel, systemImage: "rectangle.on.rectangle") {
                    model.gameCommand("game.toggleVSync")
                }

                VolumeButton(state: state)

                GraphicsButton(state: state)

                overlayButton("Screenshot", systemImage: "camera") {
                    model.gameCommand("game.screenshot")
                }

                overlayButton("Full Screen", systemImage: "arrow.up.left.and.arrow.down.right") {
                    NSApp.keyWindow?.toggleFullScreen(nil)
                }
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .glassEffect(.regular, in: .capsule)
        }
    }

    private var vsyncLabel: String {
        switch session.status?.vsync {
        case "Unbounded":
            return "V-Sync Off"
        case "Custom":
            return "Custom Rate"
        default:
            return "V-Sync On"
        }
    }

    private func overlayButton(_ title: String, systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 17, weight: .semibold))
                .frame(width: 30, height: 30)
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help(title)
        .accessibilityLabel(title)
    }

    private func confirmStop() {
        guard model.settings.value("confirmExit", default: true) else {
            model.stopGame()
            return
        }

        let alert = NSAlert()
        alert.messageText = "Stop \(session.game.name)?"
        alert.informativeText = "Progress since your last save will be lost."
        alert.addButton(withTitle: "Stop")
        alert.addButton(withTitle: "Cancel")

        if alert.runModal() == .alertFirstButtonReturn {
            model.stopGame()
        }
    }
}

private struct VolumeButton: View {
    @Bindable var state: OverlayState
    @Environment(AppModel.self) private var model
    @ViewState private var open = false

    var body: some View {
        let volume = model.settings.double("volume")

        Button {
            open.toggle()
        } label: {
            Image(systemName: volume.wrappedValue <= 0 ? "speaker.slash.fill" : volume.wrappedValue < 0.5 ? "speaker.wave.1.fill" : "speaker.wave.3.fill")
                .font(.system(size: 17, weight: .semibold))
                .frame(width: 30, height: 30)
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help("Volume")
        .popover(isPresented: $open, arrowEdge: .top) {
            HStack(spacing: 12) {
                Image(systemName: "speaker.fill")
                    .foregroundStyle(.secondary)
                Slider(value: volume, in: 0...1)
                    .frame(width: 180)
                Image(systemName: "speaker.wave.3.fill")
                    .foregroundStyle(.secondary)
            }
            .padding(16)
        }
        .onChange(of: open) { _, isOpen in state.popoverOpen = isOpen }
    }
}

/// The graphics options that apply to a running game without restarting it.
private struct GraphicsButton: View {
    @Bindable var state: OverlayState
    @Environment(AppModel.self) private var model
    @ViewState private var open = false

    var body: some View {
        Button {
            open.toggle()
        } label: {
            Image(systemName: "slider.horizontal.3")
                .font(.system(size: 17, weight: .semibold))
                .frame(width: 30, height: 30)
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help("Graphics")
        .popover(isPresented: $open, arrowEdge: .top) {
            Form {
                GraphicsQuickSettings(forGame: true)
            }
            .formStyle(.columns)
            .padding(18)
            .frame(width: 320)
        }
        .onChange(of: open) { _, isOpen in state.popoverOpen = isOpen }
    }
}

/// The graphics options that apply while a game runs, as rows for a Form (Settings and the overlay's popover).
struct GraphicsQuickSettings: View {
    /// In the in-game overlay: changes become the running game's own settings.
    var forGame = false
    @Environment(AppModel.self) private var model

    var body: some View {
        let settings = model.settings

        Group {
            Picker("Resolution", selection: settings.double("resScale", forGame: forGame)) {
                Text("Native (720p/1080p)").tag(1.0)
                Text("2x (1440p/2160p)").tag(2.0)
                Text("3x (2160p/3240p)").tag(3.0)
                Text("4x (2880p/4320p)").tag(4.0)
            }

            Picker("Aspect Ratio", selection: settings.string("aspectRatio", forGame: forGame)) {
                ForEach(settings.choices("aspectRatio"), id: \.self) { choice in
                    Text(SettingNames.aspectRatio(choice)).tag(choice)
                }
            }

            Picker("Scaling Filter", selection: settings.string("scalingFilter", forGame: forGame)) {
                Text("Bilinear").tag("Bilinear")
                Text("Nearest").tag("Nearest")
                Text("AMD FSR Sharpening").tag("Fsr")
            }

            if settings.value("scalingFilter", default: "") == "Fsr" {
                LabeledContent("Sharpness") {
                    Slider(value: Binding(get: { Double(settings.value("scalingFilterLevel", default: 80)) }, set: { settings.set("scalingFilterLevel", Int($0), forGame: forGame) }), in: 0...100)
                }
            }

            Picker("Anisotropic Filtering", selection: settings.double("maxAnisotropy", forGame: forGame)) {
                Text("Auto").tag(-1.0)
                Text("2x").tag(2.0)
                Text("4x").tag(4.0)
                Text("8x").tag(8.0)
                Text("16x").tag(16.0)
            }
        }
    }
}

/// Readable names for the values the core's settings use.
enum SettingNames {
    static func label(_ key: String, _ value: String) -> String {
        switch (key, value) {
        case ("aspectRatio", _):
            return aspectRatio(value)
        case ("audioBackend", "AudioToolbox"):
            return "Core Audio"
        case ("audioBackend", "SDL3"):
            return "SDL"
        case ("audioBackend", "SoundIo"):
            return "libsoundio"
        case ("audioBackend", "OpenAl"):
            return "OpenAL"
        case ("audioBackend", "Dummy"):
            return "Off (no sound)"
        case ("memoryManagerMode", "SoftwarePageTable"):
            return "Software (slowest, most compatible)"
        case ("memoryManagerMode", "HostMapped"):
            return "Host"
        case ("memoryManagerMode", "HostMappedUnsafe"):
            return "Host, unchecked (fastest)"
        case ("dramSize", _):
            return dramSize(value)
        default:
            return spaced(value)
        }
    }

    static func aspectRatio(_ value: String) -> String {
        switch value {
        case "Fixed4x3": return "4:3"
        case "Fixed16x9": return "16:9"
        case "Fixed16x10": return "16:10"
        case "Fixed21x9": return "21:9"
        case "Fixed32x9": return "32:9"
        case "Stretched": return "Stretch to Window"
        default: return value
        }
    }

    /// "MemoryConfiguration6GiBAppletDev" -> "6 GiB (applet dev)".
    private static func dramSize(_ value: String) -> String {
        var name = value.replacingOccurrences(of: "MemoryConfiguration", with: "")
        var note = ""

        for (suffix, text) in [("AppletDev", " (applet dev)"), ("SystemDev", " (system dev)")] where name.hasSuffix(suffix) {
            name = String(name.dropLast(suffix.count))
            note = text
        }

        name = name.replacingOccurrences(of: "GiB", with: " GiB")

        return (name == "4 GiB" && note.isEmpty ? "4 GiB (Switch)" : name) + note
    }

    /// "AmericanEnglish" -> "American English".
    static func spaced(_ value: String) -> String {
        var result = ""

        for character in value {
            if character.isUppercase, let last = result.last, last.isLowercase {
                result.append(" ")
            }

            result.append(character)
        }

        return result
    }
}
