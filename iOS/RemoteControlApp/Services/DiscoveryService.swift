import Foundation
import Darwin

/// Sends a UDP broadcast on the local network looking for RemoteControlServer instances
/// (mirrors RemoteControlServer.Utils.NetworkDiscoveryService on the Windows side) and collects
/// replies for a few seconds so the pairing screen can show a tappable list of found PCs instead
/// of requiring the user to type an IP address.
///
/// IMPORTANT: this intentionally uses a raw BSD/POSIX socket (via Darwin) instead of
/// Network.framework's NWConnection. NWConnection models a connection to *one specific* remote
/// endpoint; even though you can technically send a broadcast datagram through it, the socket
/// underneath is "connected" to 255.255.255.255 and the kernel silently drops any reply that
/// doesn't come from that exact address. The PC's reply always comes from its *real* LAN IP
/// (e.g. 192.168.1.23), never from 255.255.255.255, so with NWConnection the app would send the
/// request just fine but never see any answer - which is exactly the "PC wird nicht gefunden"
/// symptom. A plain unconnected UDP socket has no such filter, so it can send a broadcast and
/// receive replies from any address.
final class DiscoveryService: ObservableObject {
    @Published private(set) var discoveredServers: [DiscoveredServer] = []
    @Published private(set) var isScanning = false

    private let discoveryPort: UInt16 = 47887
    private let magic = "REMOTEEMU_DISCOVER_V1"

    private var socketFd: Int32 = -1
    private var readSource: DispatchSourceRead?
    private let queue = DispatchQueue(label: "com.remoteemucontrol.discovery")

    func startScan(duration: TimeInterval = 4.0) {
        stopScan()

        queue.async { [weak self] in
            guard let self else { return }
            guard self.openSocket() else {
                DispatchQueue.main.async { self.isScanning = false }
                return
            }

            DispatchQueue.main.async {
                self.discoveredServers.removeAll()
                self.isScanning = true
            }

            self.sendBroadcastRequest()
            self.startReading()
        }

        DispatchQueue.main.asyncAfter(deadline: .now() + duration) { [weak self] in
            self?.stopScan()
        }
    }

    func stopScan() {
        readSource?.cancel()
        readSource = nil
        if socketFd >= 0 {
            close(socketFd)
            socketFd = -1
        }
        isScanning = false
    }

    /// Re-sends the broadcast request without tearing down/rebuilding the socket - useful for a
    /// "search again" tap while a scan window is already open.
    func resendRequest() {
        guard socketFd >= 0 else { return }
        queue.async { [weak self] in self?.sendBroadcastRequest() }
    }

    private func openSocket() -> Bool {
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else {
            print("[!] Discovery: failed to create socket (errno \(errno))")
            return false
        }

        var broadcastEnable: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &broadcastEnable, socklen_t(MemoryLayout<Int32>.size))

        var reuseEnable: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &reuseEnable, socklen_t(MemoryLayout<Int32>.size))

        // Bind to an ephemeral local port so we can *receive* replies too (a socket that only
        // ever calls sendto() without binding may not reliably get replies routed back to it).
        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = 0 // let the OS choose a free port
        addr.sin_addr.s_addr = INADDR_ANY.bigEndian

        let bindResult = withUnsafePointer(to: &addr) { ptr -> Int32 in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPtr in
                bind(fd, sockaddrPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard bindResult == 0 else {
            print("[!] Discovery: bind failed (errno \(errno))")
            close(fd)
            return false
        }

        socketFd = fd
        return true
    }

    private func sendBroadcastRequest() {
        guard socketFd >= 0, let data = magic.data(using: .utf8) else { return }

        var destAddr = sockaddr_in()
        destAddr.sin_family = sa_family_t(AF_INET)
        destAddr.sin_port = discoveryPort.bigEndian
        destAddr.sin_addr.s_addr = INADDR_BROADCAST.bigEndian

        let sent: Int = data.withUnsafeBytes { rawBuf in
            withUnsafePointer(to: &destAddr) { ptr -> Int in
                ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPtr in
                    sendto(socketFd, rawBuf.baseAddress, data.count, 0, sockaddrPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
        if sent < 0 {
            print("[!] Discovery broadcast failed (errno \(errno))")
        }
    }

    private func startReading() {
        let source = DispatchSource.makeReadSource(fileDescriptor: socketFd, queue: queue)
        source.setEventHandler { [weak self] in
            self?.readAvailableDatagrams()
        }
        source.setCancelHandler { }
        source.resume()
        readSource = source
    }

    private func readAvailableDatagrams() {
        var buffer = [UInt8](repeating: 0, count: 2048)
        var fromAddr = sockaddr_in()
        var fromLen = socklen_t(MemoryLayout<sockaddr_in>.size)

        let bytesRead = withUnsafeMutablePointer(to: &fromAddr) { addrPtr -> Int in
            addrPtr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPtr in
                recvfrom(socketFd, &buffer, buffer.count, 0, sockaddrPtr, &fromLen)
            }
        }
        guard bytesRead > 0 else { return }

        let senderIp = ipString(from: fromAddr)
        let data = Data(bytes: buffer, count: bytesRead)

        guard let response = try? JSONDecoder().decode(DiscoveryResponse.self, from: data) else { return }

        let server = DiscoveredServer(
            hostname: response.hostname,
            ipAddress: senderIp,
            port: response.port,
            serverId: response.serverId,
            requiresPairing: response.requiresPairing
        )

        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            if !self.discoveredServers.contains(where: { $0.serverId == server.serverId }) {
                self.discoveredServers.append(server)
            }
        }
    }

    private func ipString(from addr: sockaddr_in) -> String {
        var addr = addr
        var buffer = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
        inet_ntop(AF_INET, &addr.sin_addr, &buffer, socklen_t(INET_ADDRSTRLEN))
        return String(cString: buffer)
    }
}

private struct DiscoveryResponse: Codable {
    var hostname: String
    var port: Int
    var serverId: String
    var requiresPairing: Bool
}
