import Foundation

/// Manages the list of saved macros (persisted locally as JSON) plus recording/playback state.
/// Recording itself is driven by InputSenderService, which timestamps every input event sent
/// while `isRecording` is true; this view model only handles the start/stop/save/play UI flow.
@MainActor
final class MacroViewModel: ObservableObject {
    @Published var macros: [Macro] = []
    @Published var isRecording = false

    private let connection: ConnectionViewModel
    private let storageURL: URL

    init(connection: ConnectionViewModel) {
        self.connection = connection
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        self.storageURL = docs.appendingPathComponent("macros.json")
        load()
    }

    func startRecording() {
        connection.inputSender.startRecordingMacro()
        isRecording = true
    }

    func stopRecordingAndSave(name: String) {
        guard let emulatorId = connection.activeEmulatorId,
              let macro = connection.inputSender.stopRecordingMacro(name: name, emulatorId: emulatorId) else {
            isRecording = false
            return
        }
        macros.append(macro)
        save()
        isRecording = false
    }

    func play(_ macro: Macro) {
        connection.inputSender.play(macro: macro)
    }

    func delete(_ macro: Macro) {
        macros.removeAll { $0.id == macro.id }
        save()
    }

    private func load() {
        guard let data = try? Data(contentsOf: storageURL),
              let decoded = try? JSONDecoder().decode([Macro].self, from: data) else { return }
        macros = decoded
    }

    private func save() {
        guard let data = try? JSONEncoder().encode(macros) else { return }
        try? data.write(to: storageURL)
    }
}
