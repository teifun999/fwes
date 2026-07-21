import SwiftUI
import AVFoundation

/// First screen the user sees: shows PCs found via local-network discovery, offers QR-code
/// scanning as a one-tap alternative, and a manual IP/PIN entry fallback for cross-subnet or
/// port-forwarded/VPN connections.
struct PairingView: View {
    @EnvironmentObject var connection: ConnectionViewModel
    @StateObject private var discovery = DiscoveryService()
    @State private var showQrScanner = false
    @State private var showManualEntry = false
    @State private var manualHost = ""
    @State private var manualPort = "7887"
    @State private var manualPin = ""

    var body: some View {
        NavigationStack {
            VStack(spacing: 24) {
                Image(systemName: "display")
                    .font(.system(size: 64))
                    .foregroundStyle(.blue)
                    .padding(.top, 40)

                Text("Mit PC verbinden")
                    .font(.title2.bold())

                if discovery.isScanning {
                    ProgressView("Suche im Netzwerk...")
                } else if discovery.discoveredServers.isEmpty {
                    Text("Kein PC gefunden. Stelle sicher, dass RemoteControlServer läuft und dein iPhone im selben WLAN ist.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal)
                }

                List(discovery.discoveredServers) { server in
                    Button {
                        connection.connect(to: server, pin: nil)
                    } label: {
                        HStack {
                            Image(systemName: "desktopcomputer")
                            VStack(alignment: .leading) {
                                Text(server.hostname).font(.headline)
                                Text("\(server.ipAddress):\(server.port)").font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            if server.requiresPairing {
                                Text("PIN erforderlich").font(.caption2).foregroundStyle(.orange)
                            }
                        }
                    }
                }
                .listStyle(.plain)
                .frame(maxHeight: 240)

                VStack(spacing: 12) {
                    Button {
                        showQrScanner = true
                    } label: {
                        Label("QR-Code scannen", systemImage: "qrcode.viewfinder")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)

                    Button {
                        discovery.startScan()
                    } label: {
                        Label("Erneut suchen", systemImage: "arrow.clockwise")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.bordered)

                    Button("Manuell eingeben") { showManualEntry = true }
                        .font(.footnote)
                }
                .padding(.horizontal)

                Spacer()

                if let error = connection.lastError {
                    Text(error).font(.footnote).foregroundStyle(.red).padding(.horizontal)
                }
            }
            .onAppear { discovery.startScan() }
            .sheet(isPresented: $showQrScanner) {
                QrScannerView { payload in
                    handleScannedPayload(payload)
                    showQrScanner = false
                }
            }
            .sheet(isPresented: $showManualEntry) {
                ManualConnectSheet(host: $manualHost, port: $manualPort, pin: $manualPin) {
                    connection.connectManually(host: manualHost, port: Int(manualPort) ?? 7887, pin: manualPin)
                    showManualEntry = false
                }
            }
        }
    }

    /// Parses a scanned "remoteemu://pair?host=...&port=...&sid=...&pin=..." URL from
    /// PairingService.GenerateQrCodePng on the Windows side.
    private func handleScannedPayload(_ payload: String) {
        guard let components = URLComponents(string: payload),
              let host = components.queryItems?.first(where: { $0.name == "host" })?.value,
              let portString = components.queryItems?.first(where: { $0.name == "port" })?.value,
              let port = Int(portString),
              let pin = components.queryItems?.first(where: { $0.name == "pin" })?.value else { return }

        connection.connectManually(host: host, port: port, pin: pin)
    }
}

private struct ManualConnectSheet: View {
    @Binding var host: String
    @Binding var port: String
    @Binding var pin: String
    var onConnect: () -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                Section("PC-Adresse") {
                    TextField("IP-Adresse", text: $host)
                        .keyboardType(.decimalPad)
                        .autocorrectionDisabled()
                    TextField("Port", text: $port)
                        .keyboardType(.numberPad)
                }
                Section("PIN") {
                    TextField("6-stellige PIN", text: $pin)
                        .keyboardType(.numberPad)
                }
            }
            .navigationTitle("Manuell verbinden")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Abbrechen") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Verbinden") { onConnect() }.disabled(host.isEmpty || pin.count != 6)
                }
            }
        }
    }
}

/// Thin AVFoundation QR scanner wrapped for SwiftUI. Full camera-session setup lives in
/// QrScannerViewController (UIKit) - kept separate since AVCaptureSession has no native SwiftUI API.
struct QrScannerView: UIViewControllerRepresentable {
    var onScan: (String) -> Void

    func makeUIViewController(context: Context) -> QrScannerViewController {
        let controller = QrScannerViewController()
        controller.onScan = onScan
        return controller
    }

    func updateUIViewController(_ uiViewController: QrScannerViewController, context: Context) {}
}
