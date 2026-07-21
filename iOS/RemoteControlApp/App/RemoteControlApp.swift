import SwiftUI

/// App entry point. Holds the top-level ConnectionViewModel as a StateObject so its
/// WebSocket/session state survives view redraws and is injected into the whole view tree
/// via environmentObject.
@main
struct RemoteControlApp: App {
    @StateObject private var connectionViewModel = ConnectionViewModel()
    @AppStorage("preferredColorScheme") private var preferredColorScheme: String = "system"

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(connectionViewModel)
                .preferredColorScheme(colorScheme)
        }
    }

    private var colorScheme: ColorScheme? {
        switch preferredColorScheme {
        case "dark": return .dark
        case "light": return .light
        default: return nil // follow system
        }
    }
}

/// Decides whether to show the pairing flow or the main dashboard based on connection state.
struct RootView: View {
    @EnvironmentObject var connection: ConnectionViewModel

    var body: some View {
        Group {
            if connection.isPairedAndConnected {
                DashboardView()
            } else {
                PairingView()
            }
        }
        .animation(.easeInOut, value: connection.isPairedAndConnected)
    }
}
