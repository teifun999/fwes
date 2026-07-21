import SwiftUI

/// Fullscreen live view of the emulator screen. Renders the decoded VideoToolbox frames as an
/// Image, overlays the invisible gesture-capture layer (ControlOverlayView) on top for
/// touch/multi-touch/mouse/keyboard input, and shows a collapsible toolbar with hardware
/// buttons, macro controls, and quality settings.
struct StreamView: View {
    @EnvironmentObject var connection: ConnectionViewModel
    @Environment(\.dismiss) private var dismiss
    @StateObject private var streamViewModelHolder = StreamViewModelHolder()
    @State private var showToolbar = true
    @State private var showMacroSheet = false
    @State private var macroNameInput = ""

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            if let image = connection.videoDecoder.currentFrame {
                Image(decorative: image, scale: 1.0, orientation: .up)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .ignoresSafeArea()
            } else {
                ProgressView("Warte auf Bildübertragung...")
                    .foregroundStyle(.white)
            }

            // Invisible gesture-capture surface sits above the image.
            ControlOverlayView(emulatorId: connection.activeEmulatorId ?? "")
                .environmentObject(connection)

            VStack {
                topBar
                Spacer()
                if showToolbar { bottomToolbar }
            }
        }
        .statusBarHidden()
        .onAppear {
            streamViewModelHolder.viewModel = StreamViewModel(connection: connection)
            streamViewModelHolder.viewModel?.startStream()
        }
        .onDisappear {
            streamViewModelHolder.viewModel?.stopStream()
        }
        .sheet(isPresented: $showMacroSheet) {
            MacroSaveSheet(name: $macroNameInput) { name in
                MacroViewModel(connection: connection).stopRecordingAndSave(name: name)
                showMacroSheet = false
            }
        }
    }

    private var topBar: some View {
        HStack {
            Button {
                streamViewModelHolder.viewModel?.stopStream()
                dismiss()
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .font(.title2)
                    .foregroundStyle(.white, .black.opacity(0.4))
            }
            Spacer()
            Text("\(Int(connection.stats.pingMs)) ms · \(Int(connection.videoDecoder.measuredFps)) FPS")
                .font(.caption.monospaced())
                .foregroundStyle(.white)
                .padding(.horizontal, 10).padding(.vertical, 4)
                .background(.black.opacity(0.4), in: Capsule())
            Spacer()
            Button {
                withAnimation { showToolbar.toggle() }
            } label: {
                Image(systemName: showToolbar ? "chevron.down.circle.fill" : "chevron.up.circle.fill")
                    .font(.title2)
                    .foregroundStyle(.white, .black.opacity(0.4))
            }
        }
        .padding()
    }

    private var bottomToolbar: some View {
        VStack(spacing: 10) {
            HStack(spacing: 20) {
                ForEach(HardwareButton.allCases) { button in
                    Button {
                        connection.inputSender.sendButton(emulatorId: connection.activeEmulatorId ?? "", button: button)
                    } label: {
                        Image(systemName: button.systemImageName)
                            .font(.title3)
                            .foregroundStyle(.white)
                    }
                }
            }
            HStack(spacing: 24) {
                Button {
                    macroNameInput = ""
                    let vm = MacroViewModel(connection: connection)
                    vm.isRecording ? nil : vm.startRecording()
                    showMacroSheet = true
                } label: {
                    Label("Makro", systemImage: "record.circle")
                        .foregroundStyle(.white)
                        .font(.footnote)
                }
            }
        }
        .padding()
        .background(.black.opacity(0.35))
    }
}

/// Small helper so StreamViewModel (which needs `connection` at init) can be created inside
/// onAppear without SwiftUI recreating it every redraw.
private final class StreamViewModelHolder: ObservableObject {
    var viewModel: StreamViewModel?
}

private struct MacroSaveSheet: View {
    @Binding var name: String
    var onSave: (String) -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                TextField("Makro-Name", text: $name)
            }
            .navigationTitle("Aufnahme beenden")
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Speichern") { onSave(name.isEmpty ? "Makro \(Date())" : name) }
                }
                ToolbarItem(placement: .cancellationAction) {
                    Button("Verwerfen") { dismiss() }
                }
            }
        }
    }
}
