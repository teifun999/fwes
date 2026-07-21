import Foundation

/// A recorded macro: a timestamped sequence of input events the user can replay.
/// Stored locally on-device (UserDefaults/JSON file) and optionally pushed to the server so
/// it can also be triggered from a custom keybinding.
struct Macro: Codable, Identifiable {
    var id: String = UUID().uuidString
    var name: String
    var emulatorId: String
    var events: [MacroEvent]
    var createdAt: Date = Date()
}

struct MacroEvent: Codable {
    var offsetMs: Int // time since macro start
    var kind: String  // "touch" | "key" | "text" | "button"
    var touch: TouchEventPayload?
    var key: KeyEventPayload?
    var text: TextInputPayload?
    var button: ButtonPayload?
}

/// A saved custom keybinding: maps a virtual gamepad button or keyboard key to an Android
/// keycode/tap coordinate, letting users play touch-only games with a physical controller.
struct KeyMapping: Codable, Identifiable {
    var id: String = UUID().uuidString
    var emulatorId: String
    var sourceControl: String   // e.g. "gamepad.buttonA", "keyboard.space"
    var targetAction: TouchEventPayload?
    var targetKeyCode: Int?
    var label: String
}

/// Discovered PC on the local network (from NetworkDiscoveryService's UDP broadcast reply).
struct DiscoveredServer: Identifiable, Equatable {
    var id: String { serverId }
    var hostname: String
    var ipAddress: String
    var port: Int
    var serverId: String
    var requiresPairing: Bool
}

enum ConnectionState: Equatable {
    case disconnected
    case connecting
    case awaitingPairing
    case connected
    case error(String)
}
