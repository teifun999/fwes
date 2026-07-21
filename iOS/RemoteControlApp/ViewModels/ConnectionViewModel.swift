import Foundation
import Combine
import UserNotifications

/// Top-level view model owning the WebSocket connection, pairing flow, and emulator list.
/// Injected into the environment so every screen can react to connection state changes.
@MainActor
final class ConnectionViewModel: ObservableObject {
    @Published var connectionState: ConnectionState = .disconnected
    @Published var emulators: [EmulatorInfo] = []
    @Published var activeEmulatorId: String?
    @Published var stats = StreamStatsPayload(pingMs: 0, fps: 0, bitrateKbps: 0, cpuPercent: 0, ramPercent: 0, activeEmulatorId: nil)
    @Published var lastError: String?

    let webSocket = WebSocketService()
    let discovery = DiscoveryService()
    lazy var inputSender = InputSenderService(webSocket: webSocket)
    let videoDecoder = VideoDecoderService()

    private var cancellables = Set<AnyCancellable>()
    private var pingSentAt: Date?

    var isPairedAndConnected: Bool {
        if case .connected = connectionState { return true }
        return false
    }

    init() {
        webSocket.$state
            .receive(on: DispatchQueue.main)
            .sink { [weak self] state in
                self?.connectionState = state
                if case .error(let message) = state {
                    self?.lastError = message
                    NotificationCenterService.shared.notifyConnectionLost()
                }
            }
            .store(in: &cancellables)

        webSocket.messageSubject
            .receive(on: DispatchQueue.main)
            .sink { [weak self] envelope, _ in
                self?.handleIncoming(envelope)
            }
            .store(in: &cancellables)
    }

    func connect(to server: DiscoveredServer, pin: String?) {
        webSocket.connect(host: server.ipAddress, port: server.port, pin: pin)
    }

    func connectManually(host: String, port: Int, pin: String) {
        webSocket.connect(host: host, port: port, pin: pin)
    }

    func refreshEmulatorList() {
        webSocket.send(type: MessageType.emulatorList, payload: EmptyPayload())
    }

    func selectEmulator(_ emulator: EmulatorInfo) {
        activeEmulatorId = emulator.id
        webSocket.send(type: MessageType.emulatorSelect, payload: emulator)
    }

    func startEmulator(_ emulator: EmulatorInfo) {
        webSocket.send(type: MessageType.emulatorStart, payload: emulator)
    }

    func stopEmulator(_ emulator: EmulatorInfo) {
        webSocket.send(type: MessageType.emulatorStop, payload: emulator)
    }

    func restartEmulator(_ emulator: EmulatorInfo) {
        webSocket.send(type: MessageType.emulatorRestart, payload: emulator)
    }

    private func handleIncoming(_ envelope: Envelope) {
        switch envelope.type {
        case MessageType.emulatorList:
            do {
                emulators = try webSocket.decrypt(envelope, as: [EmulatorInfo].self)
            } catch {
                print("[!] Failed to decode emulator.list: \(error)")
            }

        case MessageType.streamStats:
            if let stats = try? webSocket.decrypt(envelope, as: StreamStatsPayload.self) {
                self.stats = stats
            }

        case MessageType.videoFrame:
            if let frame = try? webSocket.decrypt(envelope, as: VideoFramePayload.self),
               let data = Data(base64Encoded: frame.data) {
                videoDecoder.decode(chunk: data, codec: frame.codec)
            }

        case MessageType.error:
            if let error = try? webSocket.decrypt(envelope, as: ErrorPayload.self) {
                lastError = error.message
            }

        case MessageType.pong:
            if let sentAt = pingSentAt {
                stats.pingMs = Date().timeIntervalSince(sentAt) * 1000
            }

        default:
            break
        }
    }
}

/// Thin wrapper around UNUserNotificationCenter for the "connection lost" push notification requirement.
enum NotificationCenterService {
    static let shared = NotificationCenterServiceImpl()
}

final class NotificationCenterServiceImpl {
    func notifyConnectionLost() {
        let content = UNMutableNotificationContent()
        content.title = "Verbindung getrennt"
        content.body = "Die Verbindung zum PC wurde unterbrochen."
        content.sound = .default
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request)
    }
}
