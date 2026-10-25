import AppKit
import PXBridge
import QuartzCore
import SwiftUI

/// The window content while a game runs: the Metal view, the loading screen and the overlay.
struct GameScreen: View {
    let session: GameSession

    var body: some View {
        ZStack {
            Color.black

            MetalGameView(session: session) {
                session.overlay.noteActivity()
            }

            if session.phase != .running {
                LoadingView(session: session)
                    .transition(.opacity)
            }

            OverlayView(session: session, state: session.overlay)
        }
        .ignoresSafeArea()
        .animation(.easeOut(duration: 0.35), value: session.phase)
        .onAppear {
            session.overlay.noteActivity()

            // Next turn of the run loop: a game opened at launch can appear before the window is attached.
            DispatchQueue.main.async {
                session.windowWasFullScreen = MainWindow.shared.targetFullScreen

                if AppModel.shared.settings.value("startFullscreen", default: false) {
                    MainWindow.shared.setFullScreen(true)
                }
            }
        }
    }
}

/// Shows the title's icon and what the emulator is preparing until the first frame arrives.
private struct LoadingView: View {
    let session: GameSession

    var body: some View {
        ZStack {
            GameIcon(path: session.game.icon, name: session.game.name)
                .scaledToFill()
                .blur(radius: 60)
                .opacity(0.45)
                .clipped()

            VStack(spacing: 22) {
                GameIcon(path: session.game.icon, name: session.game.name)
                    .frame(width: 168, height: 168)
                    .clipShape(.rect(cornerRadius: 28))
                    .shadow(color: .black.opacity(0.5), radius: 24, y: 10)

                VStack(spacing: 6) {
                    Text(session.game.name)
                        .font(.title.bold())
                    Text(stageText)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                }

                Group {
                    if let fraction = session.loadingFraction {
                        ProgressView(value: fraction)
                    } else {
                        ProgressView()
                            .progressViewStyle(.linear)
                    }
                }
                .frame(width: 260)
            }
            .padding(36)
            .glassEffect(.regular, in: .rect(cornerRadius: 32))
        }
        .foregroundStyle(.white)
    }

    private var stageText: String {
        switch session.phase {
        case .stopping:
            return "Stopping…"
        case .starting:
            return "Starting…"
        default:
            break
        }

        if session.loadingTotal > 0 {
            return "\(session.loadingStage) \(session.loadingCurrent) of \(session.loadingTotal)"
        }

        return session.loadingStage
    }
}

/// Hosts the CAMetalLayer the emulator renders into and forwards keyboard and mouse input to it.
struct MetalGameView: NSViewRepresentable {
    let session: GameSession
    var onActivity: () -> Void

    func makeNSView(context: Context) -> GameLayerView {
        let view = GameLayerView()

        view.onLayerReady = { layer, size, scale in
            // The core takes over one reference to the layer and releases it when the renderer is disposed.
            let pointer = Unmanaged.passRetained(layer).toOpaque()
            AppModel.shared.startEmulation(session, layer: pointer, width: size.width, height: size.height, scale: scale)
        }
        view.onActivity = onActivity

        return view
    }

    func updateNSView(_ view: GameLayerView, context: Context) {
        view.onActivity = onActivity
    }
}

final class GameLayerView: NSView {
    let metalLayer = CAMetalLayer()

    var onLayerReady: ((CAMetalLayer, CGSize, CGFloat) -> Void)?
    var onActivity: (() -> Void)?

    private var announced = false
    private var lastReportedSize = CGSize.zero
    private var trackingArea: NSTrackingArea?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)

        wantsLayer = true
        layerContentsRedrawPolicy = .never
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("GameLayerView is created in code")
    }

    override func makeBackingLayer() -> CALayer {
        metalLayer.isOpaque = true
        metalLayer.backgroundColor = NSColor.black.cgColor
        metalLayer.framebufferOnly = true
        return metalLayer
    }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    private static let hiddenCursor = NSCursor(image: NSImage(size: NSSize(width: 1, height: 1)), hotSpot: .zero)

    override func resetCursorRects() {
        super.resetCursorRects()

        // "Always" keeps the pointer hidden over the game; "When idle" hides it with the overlay.
        if AppModel.shared.settings.value("hideCursor", default: "OnIdle") == "Always" {
            addCursorRect(bounds, cursor: Self.hiddenCursor)
        }
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()

        guard let window else {
            return
        }

        window.makeFirstResponder(self)
        NotificationCenter.default.addObserver(self, selector: #selector(windowFocusChanged), name: NSWindow.didResignKeyNotification, object: window)
        NotificationCenter.default.addObserver(self, selector: #selector(windowFocusChanged), name: NSWindow.didBecomeKeyNotification, object: window)
        updateDrawableSize()
    }

    override func viewWillMove(toWindow newWindow: NSWindow?) {
        super.viewWillMove(toWindow: newWindow)

        if newWindow == nil {
            NotificationCenter.default.removeObserver(self)
        }
    }

    override func layout() {
        super.layout()
        updateDrawableSize()
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        updateDrawableSize()
    }

    private func updateDrawableSize() {
        guard let window else {
            return
        }

        let scale = window.backingScaleFactor
        metalLayer.contentsScale = scale

        let size = CGSize(width: (bounds.width * scale).rounded(), height: (bounds.height * scale).rounded())

        guard size.width >= 1, size.height >= 1, size != lastReportedSize else {
            return
        }

        lastReportedSize = size

        if announced {
            Core.viewResized(width: size.width, height: size.height, scale: scale)
        } else {
            announced = true
            onLayerReady?(metalLayer, size, scale)
        }
    }

    @objc private func windowFocusChanged() {
        Core.focusChanged(window?.isKeyWindow == true)
    }

    // MARK: Keyboard

    override func keyDown(with event: NSEvent) {
        // Leave ⌘ shortcuts to the menus.
        if event.modifierFlags.contains(.command) {
            super.keyDown(with: event)
            return
        }

        if !event.isARepeat {
            Core.key(event.keyCode, pressed: true)
        }
    }

    override func keyUp(with event: NSEvent) {
        Core.key(event.keyCode, pressed: false)
    }

    override func flagsChanged(with event: NSEvent) {
        // Device-dependent modifier bits from IOKit's NX_DEVICE*KEYMASK, so left and right keys are told apart.
        let masks: [UInt16: UInt] = [
            0x38: 0x0002, // left shift
            0x3C: 0x0004, // right shift
            0x3B: 0x0001, // left control
            0x3E: 0x2000, // right control
            0x3A: 0x0020, // left option
            0x3D: 0x0040, // right option
            0x37: 0x0008, // left command
            0x36: 0x0010, // right command
        ]

        if let mask = masks[event.keyCode] {
            Core.key(event.keyCode, pressed: event.modifierFlags.rawValue & mask != 0)
        } else if event.keyCode == 0x39 {
            Core.key(event.keyCode, pressed: event.modifierFlags.contains(.capsLock))
        }
    }

    // MARK: Mouse

    override func updateTrackingAreas() {
        super.updateTrackingAreas()

        if let trackingArea {
            removeTrackingArea(trackingArea)
        }

        let area = NSTrackingArea(rect: bounds, options: [.mouseMoved, .activeInKeyWindow, .inVisibleRect, .mouseEnteredAndExited], owner: self)
        addTrackingArea(area)
        trackingArea = area
    }

    private func send(_ kind: Int32, _ event: NSEvent, button: Int32 = 0) {
        let point = convert(event.locationInWindow, from: nil)
        Core.mouse(kind, x: point.x, y: point.y, button: button)
        onActivity?()
    }

    override func mouseMoved(with event: NSEvent) { send(Int32(PX_MOUSE_MOVE), event) }
    override func mouseDragged(with event: NSEvent) { send(Int32(PX_MOUSE_MOVE), event) }
    override func rightMouseDragged(with event: NSEvent) { send(Int32(PX_MOUSE_MOVE), event) }
    override func otherMouseDragged(with event: NSEvent) { send(Int32(PX_MOUSE_MOVE), event) }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        send(Int32(PX_MOUSE_DOWN), event, button: 0)
    }

    override func mouseUp(with event: NSEvent) { send(Int32(PX_MOUSE_UP), event, button: 0) }
    override func rightMouseDown(with event: NSEvent) { send(Int32(PX_MOUSE_DOWN), event, button: 1) }
    override func rightMouseUp(with event: NSEvent) { send(Int32(PX_MOUSE_UP), event, button: 1) }
    override func otherMouseDown(with event: NSEvent) { send(Int32(PX_MOUSE_DOWN), event, button: 2) }
    override func otherMouseUp(with event: NSEvent) { send(Int32(PX_MOUSE_UP), event, button: 2) }

    override func scrollWheel(with event: NSEvent) {
        Core.mouse(Int32(PX_MOUSE_SCROLL), x: event.scrollingDeltaX, y: event.scrollingDeltaY)
        onActivity?()
    }
}
