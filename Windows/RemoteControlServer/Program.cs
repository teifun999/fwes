using System;
using System.IO;
using RemoteControlServer.Adb;
using RemoteControlServer.Capture;
using RemoteControlServer.Core;
using RemoteControlServer.Emulators;
using RemoteControlServer.Input;
using RemoteControlServer.Utils;

namespace RemoteControlServer;

/// <summary>
/// Composition root. Wires up every subsystem and starts the WebSocket + discovery services,
/// then hands off to the WPF MainWindow, which is the app's real UI: pairing QR/PIN, emulator
/// list with Start/Stop/Restart, and an embedded debug log at the bottom fed from Console output.
///
/// This is a real windowed .exe (OutputType=WinExe in the .csproj) - no separate console/cmd
/// window is allocated. Every Console.WriteLine call anywhere in the codebase is captured by
/// MainWindow's UiConsoleWriter and shown live in the "Debug-Log" panel at the bottom of the
/// window instead.
/// </summary>
public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RemoteEmuControl");
        Directory.CreateDirectory(configDir);

        var pairing = new PairingService(configDir);
        var adb = new AdbClient();
        var emulatorManager = new EmulatorManager(adb);
        var input = new InputSimulator();
        var capture = new ScreenCaptureService(adb);
        var discovery = new NetworkDiscoveryService(pairing, wsPort: 7887);
        var stats = new SystemStatsService();

        var server = new RemoteControlWebSocketServer(pairing, emulatorManager, adb, input, capture);

        var app = new System.Windows.Application();
        var mainWindow = new MainWindow(pairing, emulatorManager);
        var trayApp = new TrayApp(pairing, emulatorManager, stats, mainWindow);

        // MainWindow's constructor already redirected Console.Out/Error into the embedded log
        // box, so everything from here on shows up in the window instead of a cmd popup.
        emulatorManager.StartPolling(TimeSpan.FromSeconds(3));
        discovery.Start();
        server.Start();

        Console.WriteLine("=======================================================");
        Console.WriteLine(" RemoteControlServer running");
        Console.WriteLine($" Pairing PIN: {pairing.CurrentPin}");
        Console.WriteLine($" Server ID:   {pairing.ServerId}");
        Console.WriteLine("=======================================================");

        mainWindow.Show();
        app.Run();

        server.Stop();
        discovery.Stop();
        emulatorManager.Dispose();
    }
}
