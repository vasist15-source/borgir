import AppKit
import Foundation

/// Answers questions a running game asks the player (the Switch's software keyboard, error and controller
/// applets). The core calls these from emulation threads and waits for the reply.
enum Dialogs {
    static func answer(_ request: Data) -> String {
        guard let object = try? JSONSerialization.jsonObject(with: request) as? [String: Any],
              let kind = object["kind"] as? String
        else {
            return #"{"ok":false}"#
        }

        if kind == "controllers", !Thread.isMainThread {
            return controllers(object) ? #"{"ok":true}"# : #"{"ok":false}"#
        }

        let reply: [String: Any] = onMain {
            switch kind {
            case "input":
                return softwareKeyboard(object)
            case "error":
                return errorApplet(object)
            default:
                return message(object)
            }
        }

        guard let data = try? JSONSerialization.data(withJSONObject: reply),
              let text = String(data: data, encoding: .utf8)
        else {
            return #"{"ok":false}"#
        }

        return text
    }

    private static func onMain<T>(_ body: () -> T) -> T {
        if Thread.isMainThread {
            return body()
        }

        return DispatchQueue.main.sync(execute: body)
    }

    /// The controller applet, as a sheet rather than a modal alert: the player can open Settings › Input while it
    /// waits. Blocks the calling emulation thread until they answer.
    private static func controllers(_ request: [String: Any]) -> Bool {
        let answered = DispatchSemaphore(value: 0)
        let answer = Answer()

        DispatchQueue.main.async {
            AppModel.shared.controllerRequest?.answer(false)
            AppModel.shared.controllerRequest = ControllerRequest(request) { checkAgain in
                answer.checkAgain = checkAgain
                answered.signal()
            }
        }

        answered.wait()

        return answer.checkAgain
    }

    private final class Answer: @unchecked Sendable {
        var checkAgain = false
    }

    private static func message(_ request: [String: Any]) -> [String: Any] {
        let alert = NSAlert()
        alert.messageText = request["title"] as? String ?? "Borgir"
        alert.informativeText = request["message"] as? String ?? ""
        alert.addButton(withTitle: "OK")

        if request["cancellable"] as? Bool == true {
            alert.addButton(withTitle: "Cancel")
        }

        return ["ok": alert.runModal() == .alertFirstButtonReturn]
    }

    private static func errorApplet(_ request: [String: Any]) -> [String: Any] {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = request["title"] as? String ?? "Error"
        alert.informativeText = request["message"] as? String ?? ""

        let buttons = request["buttons"] as? [String] ?? []

        for title in buttons.isEmpty ? ["OK"] : buttons {
            alert.addButton(withTitle: title)
        }

        let index = alert.runModal().rawValue - NSApplication.ModalResponse.alertFirstButtonReturn.rawValue

        return ["ok": true, "button": index]
    }

    private static func softwareKeyboard(_ request: [String: Any]) -> [String: Any] {
        let alert = NSAlert()
        alert.messageText = (request["header"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? "Enter Text"
        alert.informativeText = request["sub"] as? String ?? ""

        let minimum = request["minLength"] as? Int ?? 0
        let maximum = request["maxLength"] as? Int ?? 0

        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 320, height: 24))
        field.stringValue = request["initial"] as? String ?? ""
        field.placeholderString = request["guide"] as? String
        alert.accessoryView = field
        alert.addButton(withTitle: request["submit"] as? String ?? "OK")
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = field

        while true {
            guard alert.runModal() == .alertFirstButtonReturn else {
                return ["ok": false, "text": ""]
            }

            let text = field.stringValue

            if text.count >= minimum && (maximum <= 0 || text.count <= maximum) {
                return ["ok": true, "text": text]
            }

            alert.informativeText = maximum > 0
                ? "Enter between \(minimum) and \(maximum) characters."
                : "Enter at least \(minimum) characters."
        }
    }
}
