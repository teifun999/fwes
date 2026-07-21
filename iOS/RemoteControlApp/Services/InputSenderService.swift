import Foundation
import CoreGraphics

/// Translates SwiftUI gesture callbacks (tap, drag, long-press, multi-touch) into normalized
/// (0...1) protocol messages sent to the server. Normalization means the same gesture works
/// correctly regardless of the emulator's actual resolution or the phone's screen size -
/// the server rescales using the real device/window dimensions.
final class InputSenderService {
    private let webSocket: WebSocketService
    private var activeMacroRecorder: MacroRecorder?

    init(webSocket: WebSocketService) {
        self.webSocket = webSocket
    }

    func sendTap(emulatorId: String, normalizedPoint: CGPoint) {
        let payload = TouchEventPayload(emulatorId: emulatorId, x: normalizedPoint.x, y: normalizedPoint.y, action: "tap", pointerId: 0)
        webSocket.send(type: MessageType.inputTouch, payload: payload)
        activeMacroRecorder?.record(touch: payload)
    }

    func sendDoubleTap(emulatorId: String, normalizedPoint: CGPoint) {
        let payload = TouchEventPayload(emulatorId: emulatorId, x: normalizedPoint.x, y: normalizedPoint.y, action: "doubletap", pointerId: 0)
        webSocket.send(type: MessageType.inputTouch, payload: payload)
        activeMacroRecorder?.record(touch: payload)
    }

    func sendLongPress(emulatorId: String, normalizedPoint: CGPoint) {
        let payload = TouchEventPayload(emulatorId: emulatorId, x: normalizedPoint.x, y: normalizedPoint.y, action: "longpress", pointerId: 0)
        webSocket.send(type: MessageType.inputTouch, payload: payload)
        activeMacroRecorder?.record(touch: payload)
    }

    func sendDrag(emulatorId: String, normalizedPoint: CGPoint, phase: DragPhase) {
        let action: String
        switch phase {
        case .began: action = "down"
        case .changed: action = "move"
        case .ended: action = "up"
        }
        let payload = TouchEventPayload(emulatorId: emulatorId, x: normalizedPoint.x, y: normalizedPoint.y, action: action, pointerId: 0)
        webSocket.send(type: MessageType.inputTouch, payload: payload)
        activeMacroRecorder?.record(touch: payload)
    }

    /// Sends a full pinch/multi-finger gesture as one multi-touch message with all active points.
    func sendMultiTouch(emulatorId: String, points: [(id: Int, point: CGPoint, phase: TouchPhaseWire)]) {
        let touchPoints = points.map {
            TouchPoint(id: $0.id, x: $0.point.x, y: $0.point.y, phase: $0.phase.rawValue)
        }
        let payload = MultiTouchPayload(emulatorId: emulatorId, points: touchPoints)
        webSocket.send(type: MessageType.inputMultiTouch, payload: payload)
    }

    func sendKey(emulatorId: String, androidKeyCode: Int, isDown: Bool) {
        let payload = KeyEventPayload(emulatorId: emulatorId, keyCode: androidKeyCode, action: isDown ? "down" : "up")
        webSocket.send(type: MessageType.inputKey, payload: payload)
        activeMacroRecorder?.record(key: payload)
    }

    func sendText(emulatorId: String, text: String) {
        let payload = TextInputPayload(emulatorId: emulatorId, text: text)
        webSocket.send(type: MessageType.inputText, payload: payload)
        activeMacroRecorder?.record(text: payload)
    }

    func sendButton(emulatorId: String, button: HardwareButton) {
        let payload = ButtonPayload(emulatorId: emulatorId, button: button.rawValue)
        webSocket.send(type: MessageType.inputButton, payload: payload)
        activeMacroRecorder?.record(button: payload)
    }

    // MARK: - Macro recording

    func startRecordingMacro() {
        activeMacroRecorder = MacroRecorder()
    }

    func stopRecordingMacro(name: String, emulatorId: String) -> Macro? {
        defer { activeMacroRecorder = nil }
        return activeMacroRecorder?.finish(name: name, emulatorId: emulatorId)
    }

    /// Replays a saved macro by sending its events with their original relative timing.
    func play(macro: Macro) {
        for event in macro.events {
            DispatchQueue.main.asyncAfter(deadline: .now() + .milliseconds(event.offsetMs)) { [weak self] in
                guard let self else { return }
                if let touch = event.touch { self.webSocket.send(type: MessageType.inputTouch, payload: touch) }
                if let key = event.key { self.webSocket.send(type: MessageType.inputKey, payload: key) }
                if let text = event.text { self.webSocket.send(type: MessageType.inputText, payload: text) }
                if let button = event.button { self.webSocket.send(type: MessageType.inputButton, payload: button) }
            }
        }
    }
}

enum DragPhase { case began, changed, ended }
enum TouchPhaseWire: String { case began, moved, ended, cancelled }

enum HardwareButton: String, CaseIterable, Identifiable {
    case home, back, menu, recents
    case volumeup, volumedown, power, screenshot, rotate
    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .home: return "Home"
        case .back: return "Zurück"
        case .menu: return "Menü"
        case .recents: return "Letzte Apps"
        case .volumeup: return "Lauter"
        case .volumedown: return "Leiser"
        case .power: return "Power"
        case .screenshot: return "Screenshot"
        case .rotate: return "Drehen"
        }
    }

    var systemImageName: String {
        switch self {
        case .home: return "house.fill"
        case .back: return "arrow.left"
        case .menu: return "line.3.horizontal"
        case .recents: return "square.on.square"
        case .volumeup: return "speaker.wave.3.fill"
        case .volumedown: return "speaker.wave.1.fill"
        case .power: return "power"
        case .screenshot: return "camera.fill"
        case .rotate: return "rotate.right.fill"
        }
    }
}

/// Records timestamped input events while a macro recording session is active.
private final class MacroRecorder {
    private let startedAt = Date()
    private var events: [MacroEvent] = []

    func record(touch: TouchEventPayload) {
        events.append(MacroEvent(offsetMs: offset(), kind: "touch", touch: touch, key: nil, text: nil, button: nil))
    }
    func record(key: KeyEventPayload) {
        events.append(MacroEvent(offsetMs: offset(), kind: "key", touch: nil, key: key, text: nil, button: nil))
    }
    func record(text: TextInputPayload) {
        events.append(MacroEvent(offsetMs: offset(), kind: "text", touch: nil, key: nil, text: text, button: nil))
    }
    func record(button: ButtonPayload) {
        events.append(MacroEvent(offsetMs: offset(), kind: "button", touch: nil, key: nil, text: nil, button: button))
    }

    func finish(name: String, emulatorId: String) -> Macro {
        Macro(name: name, emulatorId: emulatorId, events: events)
    }

    private func offset() -> Int {
        Int(Date().timeIntervalSince(startedAt) * 1000)
    }
}
