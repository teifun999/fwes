import Foundation
import Combine
#if canImport(UIKit)
import UIKit
#endif

/// Low-level WebSocket transport. Owns the URLSessionWebSocketTask, handles the handshake/pairing
/// handshake sequence, automatic reconnection with backoff, and exposes a Combine publisher of
/// decoded, decrypted envelopes for higher-level services (StreamViewModel, DashboardViewModel)
/// to subscribe to by message type.
final class WebSocketService: NSObject, ObservableObject {
    @Published private(set) var state: ConnectionState = .disconnected

    /// Fires for every message once decrypted (or immediately for the two unencrypted
    /// handshake messages). Consumers filter by `envelope.type`.
    let messageSubject = PassthroughSubject<(Envelope, Data), Never>()

    private var task: URLSessionWebSocketTask?
    private var session: URLSession!
    private let crypto = EncryptionService()
    private var reconnectAttempt = 0
    private var reconnectWorkItem: DispatchWorkItem?
    private var pingTimer: Timer?
    private var lastPingSentAt: Date?

    private var host: String = ""
    private var port: Int = 7887
    private var deviceId: String
    private var pendingPin: String?

    override init() {
        self.deviceId = Self.loadOrCreateDeviceId()
        super.init()
        self.session = URLSession(configuration: .default, delegate: self, delegateQueue: nil)
    }

    // MARK: - Public API

    func connect(host: String, port: Int, pin: String?) {
        self.host = host
        self.port = port
        self.pendingPin = pin
        reconnectAttempt = 0
        openSocket()
    }

    func disconnect() {
        pingTimer?.invalidate()
        reconnectWorkItem?.cancel()
        task?.cancel(with: .goingAway, reason: nil)
        state = .disconnected
    }

    /// Encrypts and sends a payload under the given message type. No-op if the session key
    /// hasn't been established yet (i.e. pairing hasn't completed).
    func send<T: Encodable>(type: String, payload: T) {
        guard crypto.isSessionEstablished else { return }
        do {
            let (iv, tag, cipherPayload) = try crypto.encrypt(payload)
            let envelope = Envelope(type: type, iv: iv, tag: tag, payload: cipherPayload)
            sendEnvelope(envelope)
        } catch {
            print("[!] Encrypt/send failed for \(type): \(error)")
        }
    }

    func decrypt<T: Decodable>(_ envelope: Envelope, as type: T.Type) throws -> T {
        try crypto.decrypt(envelope, as: type)
    }

    // MARK: - Connection lifecycle

    private func openSocket() {
        state = .connecting
        guard let url = URL(string: "ws://\(host):\(port)") else { return }
        task = session.webSocketTask(with: url)
        task?.resume()
        listen()
        sendHello()
    }

    private func sendHello() {
        let payload = HelloRequestPayload(
            deviceName: UIDevice.current.name,
            deviceId: deviceId,
            appVersion: Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0"
        )
        let envelope = Envelope(type: MessageType.helloRequest, payload: encodeUnencrypted(payload))
        sendEnvelope(envelope)
    }

    private func sendPairRequest() {
        let clientPublicKey = crypto.generateClientKeyPair()
        let payload = PairRequestPayload(
            pin: pendingPin ?? "",
            clientPublicKey: clientPublicKey.base64EncodedString(),
            deviceId: deviceId
        )
        let envelope = Envelope(type: MessageType.pairRequest, payload: encodeUnencrypted(payload))
        sendEnvelope(envelope)
        state = .awaitingPairing
    }

    private func handleEnvelope(_ envelope: Envelope) {
        switch envelope.type {
        case MessageType.helloResponse:
            guard let response: HelloResponsePayload = try? decodeUnencrypted(envelope.payload) else { return }
            if response.requiresPairing {
                sendPairRequest()
            } else {
                // Already trusted - server still expects the ECDH exchange to establish a fresh
                // session key for this connection, so send the pair request with an empty PIN;
                // the server accepts it automatically for already-trusted device IDs.
                sendPairRequest()
            }

        case MessageType.pairResponse:
            guard let response: PairResponsePayload = try? decodeUnencrypted(envelope.payload) else { return }
            guard response.success, let serverKeyData = Data(base64Encoded: response.serverPublicKey) else {
                state = .error(response.reason ?? "Pairing failed")
                return
            }
            do {
                try crypto.deriveSessionKey(serverPublicKeyData: serverKeyData, salt: deviceId)
                state = .connected
                startPingLoop()
            } catch {
                state = .error("Key exchange failed: \(error.localizedDescription)")
            }

        default:
            // Every other message type is encrypted; forward the raw envelope + a marker so
            // downstream subscribers call decrypt(_:as:) with their own expected payload type.
            messageSubject.send((envelope, Data()))
        }
    }

    private func startPingLoop() {
        pingTimer?.invalidate()
        pingTimer = Timer.scheduledTimer(withTimeInterval: 3.0, repeats: true) { [weak self] _ in
            self?.lastPingSentAt = Date()
            self?.send(type: MessageType.ping, payload: EmptyPayload())
        }
    }

    // MARK: - Reconnection

    private func scheduleReconnect() {
        reconnectWorkItem?.cancel()
        reconnectAttempt += 1
        let delay = min(pow(2.0, Double(reconnectAttempt)), 30.0) // exponential backoff, capped at 30s

        let workItem = DispatchWorkItem { [weak self] in
            self?.openSocket()
        }
        reconnectWorkItem = workItem
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: workItem)
    }

    // MARK: - Wire I/O

    private func listen() {
        task?.receive { [weak self] result in
            guard let self else { return }
            switch result {
            case .failure(let error):
                print("[!] WebSocket receive error: \(error)")
                self.state = .error(error.localizedDescription)
                self.scheduleReconnect()
                return

            case .success(let message):
                if case .string(let text) = message,
                   let data = text.data(using: .utf8),
                   let envelope = try? JSONDecoder().decode(Envelope.self, from: data) {
                    if envelope.type == MessageType.pong {
                        // Latency measured by DashboardViewModel via lastPingSentAt.
                    }
                    self.handleEnvelope(envelope)
                }
                self.listen() // keep listening
            }
        }
    }

    private func sendEnvelope(_ envelope: Envelope) {
        guard let data = try? JSONEncoder().encode(envelope),
              let text = String(data: data, encoding: .utf8) else { return }
        task?.send(.string(text)) { error in
            if let error { print("[!] Send failed: \(error)") }
        }
    }

    private func encodeUnencrypted<T: Encodable>(_ payload: T) -> String {
        guard let data = try? JSONEncoder().encode(payload),
              let json = String(data: data, encoding: .utf8) else { return "{}" }
        return json
    }

    private func decodeUnencrypted<T: Decodable>(_ json: String) throws -> T {
        try JSONDecoder().decode(T.self, from: Data(json.utf8))
    }

    private static func loadOrCreateDeviceId() -> String {
        let key = "remoteemu.deviceId"
        if let existing = UserDefaults.standard.string(forKey: key) { return existing }
        let newId = UUID().uuidString
        UserDefaults.standard.set(newId, forKey: key)
        return newId
    }
}

struct EmptyPayload: Codable {}

extension WebSocketService: URLSessionWebSocketDelegate {
    func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        DispatchQueue.main.async {
            self.state = .disconnected
            self.scheduleReconnect()
        }
    }
}

#if canImport(UIKit)
import UIKit
#endif
