import AppKit
import Foundation
import PXBridge

/// Calls into the emulator core through the table it passed to `px_ui_run`.
enum Core {
    nonisolated(unsafe) static var callbacks = PXCoreCallbacks()

    /// Runs a core command and returns its reply as a JSON object. Replies always carry `"ok"`;
    /// failures add `"error"` with a message for the player.
    @discardableResult
    static func command(_ name: String, _ arguments: [String: Any] = [:]) -> [String: Any] {
        guard let data = commandData(name, arguments),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else {
            return ["ok": false, "error": "The emulator core did not answer \(name)."]
        }

        return object
    }

    /// Runs a core command and decodes its reply.
    static func command<T: Decodable>(_ name: String, _ arguments: [String: Any] = [:], as type: T.Type) -> T? {
        guard let data = commandData(name, arguments) else {
            return nil
        }

        // A refusal (a game that left the library during a rescan, say) has nothing to decode.
        if let reply = try? JSONDecoder().decode(ReplyStatus.self, from: data), reply.ok == false {
            return nil
        }

        do {
            return try JSONDecoder().decode(T.self, from: data)
        } catch {
            NSLog("Borgir: could not decode the reply to \(name): \(error)")
            return nil
        }
    }

    private static func commandData(_ name: String, _ arguments: [String: Any]) -> Data? {
        guard let run = callbacks.command else {
            return nil
        }

        var payload = arguments
        payload["cmd"] = name

        guard JSONSerialization.isValidJSONObject(payload),
              let request = try? JSONSerialization.data(withJSONObject: payload),
              let json = String(data: request, encoding: .utf8)
        else {
            return nil
        }

        guard let reply = json.withCString({ run($0) }) else {
            return nil
        }

        defer { callbacks.free_string?(reply) }

        return Data(String(cString: reply).utf8)
    }

    static func key(_ keyCode: UInt16, pressed: Bool) {
        callbacks.key_event?(keyCode, pressed)
    }

    static func mouse(_ kind: Int32, x: Double, y: Double, button: Int32 = 0) {
        callbacks.mouse_event?(kind, x, y, button)
    }

    static func viewResized(width: Double, height: Double, scale: Double) {
        callbacks.view_resized?(width, height, scale)
    }

    static func focusChanged(_ focused: Bool) {
        callbacks.focus_changed?(focused)
    }
}

private struct ReplyStatus: Decodable {
    var ok: Bool?
}

// MARK: - Exports

@_cdecl("px_ui_run")
public func pxUIRun(_ callbacks: UnsafePointer<PXCoreCallbacks>?) -> Int32 {
    guard let callbacks, callbacks.pointee.version == UInt32(PX_BRIDGE_VERSION) else {
        NSLog("Borgir: the emulator core and UI library are from different builds.")
        return 1
    }

    Core.callbacks = callbacks.pointee

    // The core parses the command line itself (a game path, --fullscreen, ...). Without this AppKit would also
    // try to open every argument it doesn't know as a file.
    UserDefaults.standard.register(defaults: ["NSTreatUnknownArgumentsAsOpen": "NO"])

    carryOverPreferences()

    ProjectXApp.main()

    return 0
}

@_cdecl("px_ui_post_event")
public func pxUIPostEvent(_ json: UnsafePointer<CChar>?) {
    guard let json else {
        return
    }

    let data = Data(String(cString: json).utf8)

    DispatchQueue.main.async {
        AppModel.shared.handle(event: data)
    }
}

@_cdecl("px_ui_request")
public func pxUIRequest(_ json: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>? {
    guard let json else {
        return nil
    }

    let request = Data(String(cString: json).utf8)
    let reply = Dialogs.answer(request)

    return strdup(reply)
}

@_cdecl("px_ui_free")
public func pxUIFree(_ string: UnsafeMutablePointer<CChar>?) {
    free(string)
}

/// Borgir was called Project X, with the bundle ID app.projectx.ProjectX. Its interface preferences (the Settings tab,
/// the overlay, window frames) carry over the first time it runs under the new ID.
private func carryOverPreferences() {
    let previous = "app.projectx.ProjectX"
    let defaults = UserDefaults.standard

    guard let current = Bundle.main.bundleIdentifier, current != previous,
          defaults.persistentDomain(forName: current)?.isEmpty ?? true,
          let old = defaults.persistentDomain(forName: previous), !old.isEmpty
    else {
        return
    }

    defaults.setPersistentDomain(old, forName: current)
}
