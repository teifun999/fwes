using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using RemoteControlServer.Core;
using RemoteControlServer.Emulators;
using RemoteControlServer.Utils;

namespace RemoteControlServer;

/// <summary>
/// The server's real main window - replaces the old "tray icon + separate console window"
/// approach. It shows the pairing QR/PIN, a live emulator list with Start/Stop/Restart buttons,
/// and an embedded debug log at the bottom (fed from Console.WriteLine via UiConsoleWriter), so
/// the whole thing behaves like one normal Windows application instead of an app plus a cmd
/// window running alongside it. Closing the window minimizes to the tray; use the tray icon's
/// "Exit" to actually quit.
/// </summary>
public partial class MainWindow : Window
{
    private readonly PairingService _pairing;
    private readonly EmulatorManager _emulatorManager;
    private bool _allowClose;

    public MainWindow(PairingService pairing, EmulatorManager emulatorManager)
    {
        InitializeComponent();

        _pairing = pairing;
        _emulatorManager = emulatorManager;

        // Redirect Console.Out/Error into the log box as early as possible so every
        // Console.WriteLine already sprinkled through the codebase shows up here instead of
        // needing a separate console window.
        var writer = new UiConsoleWriter(LogBox);
        Console.SetOut(writer);
        Console.SetError(writer);

        _emulatorManager.OnEmulatorListChanged += list =>
        {
            Dispatcher.BeginInvoke(new Action(() => EmulatorListView.ItemsSource = list));
        };

        RefreshPairingInfo();
        EmulatorListView.ItemsSource = _emulatorManager.GetAll();

        Console.WriteLine("RemoteEmuControl Server gestartet.");
    }

    /// <summary>Call once after the tray icon exists, so "Exit" in the tray menu can actually close this window.</summary>
    public void AllowClose() => _allowClose = true;

    private void RefreshPairingInfo()
    {
        string localIp = GetLocalIPv4() ?? "127.0.0.1";
        byte[] qrPng = _pairing.GenerateQrCodePng(localIp, 7887);

        var bitmap = new BitmapImage();
        using (var ms = new MemoryStream(qrPng))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
        }
        QrImage.Source = bitmap;
        PinText.Text = _pairing.CurrentPin;
        IpText.Text = $"{localIp}:7887";
    }

    private void RegenerateButton_Click(object sender, RoutedEventArgs e)
    {
        _pairing.GeneratePin();
        RefreshPairingInfo();
        Console.WriteLine("PIN wurde neu generiert.");
    }

    private async void RefreshEmulatorsButton_Click(object sender, RoutedEventArgs e)
    {
        Console.WriteLine("Suche Emulatoren erneut...");
        await _emulatorManager.RefreshAsync();
    }

    private async void StartEmulator_Click(object sender, RoutedEventArgs e)
    {
        if (GetEmulatorFromTag(sender) is { } info)
        {
            Console.WriteLine($"Starte {info.Name}...");
            await _emulatorManager.StartAsync(info.Id);
        }
    }

    private async void StopEmulator_Click(object sender, RoutedEventArgs e)
    {
        if (GetEmulatorFromTag(sender) is { } info)
        {
            Console.WriteLine($"Stoppe {info.Name}...");
            await _emulatorManager.StopAsync(info.Id);
        }
    }

    private async void RestartEmulator_Click(object sender, RoutedEventArgs e)
    {
        if (GetEmulatorFromTag(sender) is { } info)
        {
            Console.WriteLine($"Starte {info.Name} neu...");
            await _emulatorManager.RestartAsync(info.Id);
        }
    }

    private static EmulatorInfo? GetEmulatorFromTag(object sender) =>
        (sender as FrameworkElement)?.Tag as EmulatorInfo;

    /// <summary>Closing the X button minimizes to tray instead of exiting, so the server keeps running.</summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    private static string? GetLocalIPv4()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return addr.Address.ToString();
            }
        }
        return null;
    }
}
