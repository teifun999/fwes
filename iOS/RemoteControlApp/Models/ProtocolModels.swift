import Foundation

/// Mirrors RemoteControlServer.Core.Envelope on the Windows side. This is the outer, unencrypted
/// transport frame; `payload` is base64 AES-256-GCM ciphertext once a session key exists.
struct Envelope: Codable {
    var v: Int = 1
    var type: String
    var id: String = UUID().uuidString
    var ts: Int64 = Int64(Date().timeIntervalSince1970 * 1000)
    var iv: String?
    var tag: String?
    var payload: String
}

/// Message type constants - keep in lockstep with RemoteControlServer.Core.MessageType.
enum MessageType {
    static let helloRequest = "hello.request"
    static let helloResponse = "hello.response"
    static let pairRequest = "pair.request"
    static let pairResponse = "pair.response"
    static let ping = "ping"
    static let pong = "pong"
    static let ack = "ack"
    static let error = "error"

    static let emulatorList = "emulator.list"
    static let emulatorSelect = "emulator.select"
    static let emulatorStart = "emulator.start"
    static let emulatorStop = "emulator.stop"
    static let emulatorRestart = "emulator.restart"
    static let emulatorStatus = "emulator.status"

    static let streamStart = "stream.start"
    static let streamStop = "stream.stop"
    static let streamConfig = "stream.config"
    static let videoFrame = "stream.frame"
    static let streamStats = "stream.stats"

    static let inputTouch = "input.touch"
    static let inputMultiTouch = "input.multitouch"
    static let inputKey = "input.key"
    static let inputText = "input.text"
    static let inputMouse = "input.mouse"
    static let inputButton = "input.button"

    static let apkInstall = "apk.install"
    static let adbCommand = "adb.command"
    static let screenshotRequest = "screenshot.request"
    static let screenshotResponse = "screenshot.response"
    static let macroRecordStart = "macro.record.start"
    static let macroRecordStop = "macro.record.stop"
    static let macroPlay = "macro.play"
    static let clipboardSync = "clipboard.sync"
}

struct HelloRequestPayload: Codable {
    var deviceName: String
    var deviceId: String
    var appVersion: String
}

struct HelloResponsePayload: Codable {
    var serverName: String
    var requiresPairing: Bool
    var protocolVersion: Int
    var supportsHevc: Bool
}

struct PairRequestPayload: Codable {
    var pin: String
    var clientPublicKey: String
    var deviceId: String
}

struct PairResponsePayload: Codable {
    var success: Bool
    var serverPublicKey: String
    var sessionToken: String
    var reason: String?
}

struct EmulatorInfo: Codable, Identifiable, Equatable {
    var id: String
    var name: String
    var type: String
    var status: String // stopped|starting|running|stopping
    var adbSerial: String?
    var resolutionW: Int
    var resolutionH: Int
    var isActive: Bool
}

struct TouchEventPayload: Codable {
    var emulatorId: String
    var x: Double
    var y: Double
    var action: String // down|move|up|tap|doubletap|longpress
    var pointerId: Int
}

struct TouchPoint: Codable {
    var id: Int
    var x: Double
    var y: Double
    var phase: String // began|moved|ended|cancelled
}

struct MultiTouchPayload: Codable {
    var emulatorId: String
    var points: [TouchPoint]
}

struct KeyEventPayload: Codable {
    var emulatorId: String
    var keyCode: Int
    var action: String // down|up
}

struct TextInputPayload: Codable {
    var emulatorId: String
    var text: String
}

struct ButtonPayload: Codable {
    var emulatorId: String
    var button: String // home|back|menu|recents|volumeup|volumedown|power|screenshot|rotate
}

struct StreamConfigPayload: Codable {
    var emulatorId: String
    var codec: String = "h264" // h264|hevc
    var fps: Int = 30
    var quality: Int = 80
    var maxWidth: Int = 1280
}

struct StreamStatsPayload: Codable {
    var pingMs: Double
    var fps: Double
    var bitrateKbps: Double
    var cpuPercent: Double
    var ramPercent: Double
    var activeEmulatorId: String?
}

struct VideoFramePayload: Codable {
    var emulatorId: String
    var codec: String
    var data: String // base64 encoded NAL units
    var keyFrame: Bool
}

struct ErrorPayload: Codable {
    var code: String
    var message: String
}
