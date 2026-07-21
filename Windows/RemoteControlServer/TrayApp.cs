using System;
using System.Windows;
using System.Windows.Forms; // NotifyIcon lives here even in a WPF app - add reference to System.Windows.Forms
using RemoteControlServer.Core;
using RemoteControlServer.Emulators;
using RemoteControlServer.Utils;
using Application = System.Windows.Application;

namespace RemoteControlServer;

/// <summary>
/// System-tray presence for the server. The real UI (pairing QR/PIN, emulator list with
/// Start/Stop/Restart, embedded debug log) lives in MainWindow - this class just gives the user
/// a way to bring that window back after closing it (closing the window minimizes to tray, it
/// does not exit the app) and a proper "Exit" to actually quit.
/// </summary>
public class TrayApp
{
    private readonly NotifyIcon _notifyIcon;
    private readonly MainWindow _mainWindow;

    public TrayApp(PairingService pairing, EmulatorManager emulatorManager, SystemStatsService stats, MainWindow mainWindow)
    {
        _mainWindow = mainWindow;

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application, // replace with a branded .ico in production
            Visible = true,
            Text = "RemoteEmuControl Server",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Fenster anzeigen", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) =>
        {
            _mainWindow.AllowClose();
            Application.Current.Shutdown();
        });
        _notifyIcon.ContextMenuStrip = menu;

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        // Surface a Windows toast/balloon if all client sessions drop unexpectedly, mirroring
        // the "push notification on connection loss" requirement on the desktop side too.
    }

    private void ShowMainWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }
}
