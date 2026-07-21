import SwiftUI

/// App-wide settings: video quality/codec/fps, appearance (dark/light/system), security
/// (trusted-device management prompt text, session info), saved macros list, and custom
/// keybindings for gamepad support.
struct SettingsView: View {
    @EnvironmentObject var connection: ConnectionViewModel
    @AppStorage("preferredColorScheme") private var preferredColorScheme: String = "system"
    @AppStorage("streamCodec") private var codec: String = "h264"
    @AppStorage("streamFps") private var fps: Int = 30
    @AppStorage("streamQuality") private var quality: Double = 80
    @AppStorage("streamMaxWidth") private var maxWidth: Int = 1280
    @StateObject private var macroViewModel: MacroViewModelHolder = MacroViewModelHolder()

    var body: some View {
        Form {
            Section("Erscheinungsbild") {
                Picker("Design", selection: $preferredColorScheme) {
                    Text("System").tag("system")
                    Text("Hell").tag("light")
                    Text("Dunkel").tag("dark")
                }
                .pickerStyle(.segmented)
            }

            Section("Bildqualität") {
                Picker("Codec", selection: $codec) {
                    Text("H.264").tag("h264")
                    Text("HEVC").tag("hevc")
                }
                Picker("Bildrate", selection: $fps) {
                    Text("30 FPS").tag(30)
                    Text("60 FPS").tag(60)
                }
                VStack(alignment: .leading) {
                    Text("Qualität: \(Int(quality))%")
                    Slider(value: $quality, in: (10...100), step: 5)
                }
                Picker("Max. Auflösung", selection: $maxWidth) {
                    Text("720p").tag(1280)
                    Text("1080p").tag(1920)
                }
            }

            Section("Verbindung") {
                LabeledContent("Status", value: connection.isPairedAndConnected ? "Verbunden" : "Getrennt")
                Button("Verbindung trennen", role: .destructive) {
                    connection.webSocket.disconnect()
                }
            }

            Section("Makros") {
                if macroViewModel.viewModel?.macros.isEmpty ?? true {
                    Text("Keine gespeicherten Makros.").foregroundStyle(.secondary)
                } else {
                    ForEach(macroViewModel.viewModel?.macros ?? []) { macro in
                        HStack {
                            VStack(alignment: .leading) {
                                Text(macro.name)
                                Text("\(macro.events.count) Ereignisse").font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Button("Abspielen") { macroViewModel.viewModel?.play(macro) }
                                .font(.caption)
                        }
                    }
                    .onDelete { indexSet in
                        indexSet.forEach { index in
                            if let macro = macroViewModel.viewModel?.macros[index] {
                                macroViewModel.viewModel?.delete(macro)
                            }
                        }
                    }
                }
            }

            Section("Extras") {
                NavigationLink("Eigene Tastenbelegung", destination: KeyMappingView())
                NavigationLink("Dateiübertragung", destination: FileTransferView())
                NavigationLink("Zwischenablage synchronisieren", destination: ClipboardSyncView())
            }

            Section {
                LabeledContent("Version", value: Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0")
            }
        }
        .navigationTitle("Einstellungen")
        .onAppear {
            macroViewModel.viewModel = MacroViewModel(connection: connection)
        }
    }
}

/// Wraps MacroViewModel so it can be lazily created with `connection` inside onAppear.
private final class MacroViewModelHolder: ObservableObject {
    @Published var viewModel: MacroViewModel?
}

/// Placeholder screens for the "extras" - gamepad key-mapping editor, file transfer picker, and
/// clipboard sync toggle. Each wires into the existing protocol messages
/// (input.key / file.transfer / clipboard.sync) already defined in ProtocolModels.swift.
struct KeyMappingView: View {
    var body: some View {
        List {
            Text("Ordne Gamepad-Tasten oder Tastaturkürzel Android-Tastencodes oder Bildschirmkoordinaten zu.")
                .font(.footnote)
                .foregroundStyle(.secondary)
            // Production: list of KeyMapping entries with add/edit sheet, persisted like Macro.
        }
        .navigationTitle("Tastenbelegung")
    }
}

struct FileTransferView: View {
    @EnvironmentObject var connection: ConnectionViewModel
    var body: some View {
        List {
            Text("Wähle eine Datei oder APK zum Übertragen an den aktiven Emulator aus.")
                .font(.footnote)
                .foregroundStyle(.secondary)
            // Production: UIDocumentPickerViewController -> base64 chunks over file.transfer messages,
            // or apk.install for .apk files specifically.
        }
        .navigationTitle("Dateiübertragung")
    }
}

struct ClipboardSyncView: View {
    @AppStorage("clipboardSyncEnabled") private var clipboardSyncEnabled = false
    var body: some View {
        Form {
            Toggle("Zwischenablage automatisch synchronisieren", isOn: $clipboardSyncEnabled)
            Text("Wenn aktiviert, wird kopierter Text zwischen iPhone und PC/Emulator automatisch geteilt.")
                .font(.footnote)
                .foregroundStyle(.secondary)
        }
        .navigationTitle("Zwischenablage")
    }
}
