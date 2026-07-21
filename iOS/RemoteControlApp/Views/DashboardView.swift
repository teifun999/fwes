import SwiftUI

/// Main hub shown once paired/connected: connection health, active emulator, quick stats
/// (ping/fps/CPU/RAM), and the list of detected emulators with start/stop/switch controls.
/// Tapping an emulator (or the "Live-Bild öffnen" button) pushes into StreamView.
struct DashboardView: View {
    @EnvironmentObject var connection: ConnectionViewModel
    @State private var showStream = false

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 16) {
                    statusCard
                    statsGrid
                    emulatorList
                }
                .padding()
            }
            .navigationTitle("Dashboard")
            .toolbar {
                ToolbarItem(placement: .navigationBarTrailing) {
                    NavigationLink(destination: SettingsView()) {
                        Image(systemName: "gearshape")
                    }
                }
            }
            .onAppear { connection.refreshEmulatorList() }
            .fullScreenCover(isPresented: $showStream) {
                StreamView()
            }
        }
    }

    private var statusCard: some View {
        HStack {
            Circle()
                .fill(connection.isPairedAndConnected ? Color.green : Color.red)
                .frame(width: 12, height: 12)
            Text(connection.isPairedAndConnected ? "Verbunden" : "Getrennt")
                .font(.headline)
            Spacer()
            if let active = connection.emulators.first(where: { $0.isActive }) {
                Text(active.name)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
        }
        .padding()
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
    }

    private var statsGrid: some View {
        LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 12) {
            StatTile(title: "Ping", value: "\(Int(connection.stats.pingMs)) ms", icon: "bolt.fill")
            StatTile(title: "FPS", value: "\(Int(connection.videoDecoder.measuredFps))", icon: "speedometer")
            StatTile(title: "CPU (PC)", value: "\(Int(connection.stats.cpuPercent))%", icon: "cpu")
            StatTile(title: "RAM (PC)", value: "\(Int(connection.stats.ramPercent))%", icon: "memorychip")
        }
    }

    private var emulatorList: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Emulatoren").font(.headline)
                Spacer()
                Button { connection.refreshEmulatorList() } label: {
                    Image(systemName: "arrow.clockwise")
                }
            }

            if connection.emulators.isEmpty {
                Text("Keine Emulatoren gefunden.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }

            ForEach(connection.emulators) { emulator in
                EmulatorRow(emulator: emulator, onSelect: {
                    connection.selectEmulator(emulator)
                    showStream = true
                }, onStart: { connection.startEmulator(emulator) },
                   onStop: { connection.stopEmulator(emulator) },
                   onRestart: { connection.restartEmulator(emulator) })
            }
        }
    }
}

private struct StatTile: View {
    let title: String
    let value: String
    let icon: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Label(title, systemImage: icon)
                .font(.caption)
                .foregroundStyle(.secondary)
            Text(value)
                .font(.title3.bold())
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding()
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
    }
}

private struct EmulatorRow: View {
    let emulator: EmulatorInfo
    var onSelect: () -> Void
    var onStart: () -> Void
    var onStop: () -> Void
    var onRestart: () -> Void

    var body: some View {
        HStack {
            VStack(alignment: .leading) {
                Text(emulator.name).font(.subheadline.bold())
                Text("\(emulator.type) · \(statusText)")
                    .font(.caption)
                    .foregroundStyle(statusColor)
            }
            Spacer()
            Menu {
                Button("Live-Bild öffnen", action: onSelect)
                Button("Starten", action: onStart)
                Button("Beenden", action: onStop)
                Button("Neu starten", action: onRestart)
            } label: {
                Image(systemName: "ellipsis.circle")
                    .font(.title3)
            }
        }
        .padding()
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        .onTapGesture(perform: onSelect)
    }

    private var statusText: String {
        switch emulator.status {
        case "running": return "läuft"
        case "starting": return "startet..."
        case "stopping": return "beendet..."
        default: return "gestoppt"
        }
    }

    private var statusColor: Color {
        emulator.status == "running" ? .green : .secondary
    }
}
