import Foundation
import Combine

/// Controls the live-stream lifecycle for the currently active emulator: start/stop, and
/// pushing quality/fps/codec changes chosen in SettingsView down to the server in real time.
@MainActor
final class StreamViewModel: ObservableObject {
    @Published var codec: String = "h264" // "h264" | "hevc"
    @Published var fps: Int = 30           // 30 | 60
    @Published var quality: Int = 80       // 0-100
    @Published var maxWidth: Int = 1280
    @Published var isFullscreen: Bool = false
    @Published var isStreaming: Bool = false

    private let connection: ConnectionViewModel

    init(connection: ConnectionViewModel) {
        self.connection = connection
    }

    func startStream() {
        guard let emulatorId = connection.activeEmulatorId else { return }
        let config = StreamConfigPayload(emulatorId: emulatorId, codec: codec, fps: fps, quality: quality, maxWidth: maxWidth)
        connection.webSocket.send(type: MessageType.streamStart, payload: config)
        isStreaming = true
    }

    func stopStream() {
        connection.webSocket.send(type: MessageType.streamStop, payload: EmptyPayload())
        isStreaming = false
    }

    /// Called whenever the user changes a quality/fps/codec slider while already streaming -
    /// pushes the new config without restarting the whole stream.
    func applyConfigChange() {
        guard isStreaming, let emulatorId = connection.activeEmulatorId else { return }
        let config = StreamConfigPayload(emulatorId: emulatorId, codec: codec, fps: fps, quality: quality, maxWidth: maxWidth)
        connection.webSocket.send(type: MessageType.streamConfig, payload: config)
    }
}
