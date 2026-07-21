using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System;
using System.IO;
using System.Diagnostics;
using RemoteControlServer.Core;

namespace RemoteControlServer.Adb;

/// <summary>
/// Thin wrapper around the "adb" executable. This is the preferred control path for every
/// emulator: it's the same official Android tool regardless of vendor, doesn't require window
/// focus, and gives pixel-accurate touch/key injection. The server falls back to
/// Input.InputSimulator (raw Windows SendInput against the emulator window) only when adb.exe
/// can't be found or a device isn't reachable over the ADB bridge.
/// </summary>
public class AdbClient
{
    private string? _adbPath;
    private readonly Dictionary<string, bool> _lastConnectResult = new();

    public bool IsAvailable => _adbPath != null;

    public AdbClient()
    {
        _adbPath = FindAdbExecutable();
        if (_adbPath == null)
            Console.WriteLine("[!] adb.exe not found on PATH or common install locations - falling back to Windows input simulation only.");
        else
            Console.WriteLine($"[i] Using adb at: {_adbPath}");
    }

    private static string? FindAdbExecutable()
    {
        // 1. PATH
        string? fromPath = Environment.GetEnvironmentVariable("PATH")
            ?.Split(Path.PathSeparator)
            .Select(p => Path.Combine(p, "adb.exe"))
            .FirstOrDefault(File.Exists);
        if (fromPath != null) return fromPath;

        // 2. Android Studio SDK default location
        string sdkAdb = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Android\Sdk\platform-tools\adb.exe");
        if (File.Exists(sdkAdb)) return sdkAdb;

        // 3. Every emulator vendor bundles its own copy of adb, but the folder name/version and
        //    drive letter varies a lot (custom install locations, older/newer LDPlayer versions,
        //    etc.) - scan every fixed drive for the known folder patterns instead of one hardcoded
        //    C:\ path per vendor.
        string[] relativeCandidates =
        {
            @"LDPlayer\LDPlayer9\adb.exe",
            @"LDPlayer\LDPlayer4\adb.exe",
            @"Program Files\LDPlayer\LDPlayer9\adb.exe",
            @"Program Files\BlueStacks_nxt\HD-Adb.exe",
            @"Program Files (x86)\BlueStacks_nxt\HD-Adb.exe",
            @"Program Files\BlueStacks_nxt_cn\HD-Adb.exe",
            @"Program Files\BlueStacks\HD-Adb.exe",
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

            foreach (var relative in relativeCandidates)
            {
                string candidate = Path.Combine(drive.Name, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Called by EmulatorManager if adb.exe wasn't found at startup (custom install folder we
    /// don't guess by default) but a plugin later reports a concrete install path that does
    /// contain an adb binary - so the server doesn't need a restart once it's found.
    /// </summary>
    public bool TryDiscoverFromInstallPath(string installPath)
    {
        if (_adbPath != null) return true;

        foreach (var exeName in new[] { "adb.exe", "HD-Adb.exe" })
        {
            string candidate = Path.Combine(installPath, exeName);
            if (File.Exists(candidate))
            {
                _adbPath = candidate;
                Console.WriteLine($"[i] adb gefunden über Emulator-Installationspfad: {_adbPath}");
                return true;
            }
        }
        return false;
    }

    public async Task<List<string>> ListDevicesAsync()
    {
        string output = await RunAsync("devices");
        var serials = new List<string>();
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length == 2 && parts[1].Trim() == "device")
                serials.Add(parts[0]);
        }
        return serials;
    }

    /// <summary>
    /// LDPlayer/BlueStacks instances expose their ADB bridge over TCP (127.0.0.1:port), but unlike
    /// a USB device, adb has no idea that address exists until you explicitly tell it to via
    /// "adb connect host:port". Without this, every "-s 127.0.0.1:5555 shell ..." command below
    /// fails with "device not found" even though adb.exe itself was located fine - the plugins
    /// only *guess* the port, they never registered it. This is called for every instance that has
    /// an AdbSerial each time the emulator list is refreshed (see EmulatorManager.RefreshAsync),
    /// so a freshly (re)started emulator gets (re)connected automatically without restarting the
    /// server. It's idempotent - calling it on an already-connected serial is a harmless no-op.
    /// Serials without a ':' (e.g. "emulator-5554" from GenericAdbPlugin) are already-attached
    /// console/USB devices and are skipped - see the check below.
    /// </summary>
    public async Task<bool> EnsureConnectedAsync(string serial)
    {
        if (_adbPath == null) return false;

        // "adb connect" only makes sense for TCP endpoints (host:port, e.g. LDPlayer/BlueStacks'
        // 127.0.0.1:5555). Devices like "emulator-5554" (classic Android console serials) or USB
        // serials are already attached as soon as `adb devices` lists them - calling "adb connect"
        // on them is invalid ("cannot resolve host ..."), and doing that every 3s in the poll loop
        // was previously confusing the local adb server enough to make `adb devices` briefly return
        // empty (the flickering in the emulator list). Skip anything that isn't host:port.
        if (!serial.Contains(':'))
        {
            _lastConnectResult[serial] = true;
            return true;
        }

        string output = await RunAsync($"connect {serial}");
        bool ok = output.Contains("connected to", StringComparison.OrdinalIgnoreCase)
                  || output.Contains("already connected", StringComparison.OrdinalIgnoreCase);

        // Only log on state changes so this doesn't spam the debug log every 3-second poll.
        if (!_lastConnectResult.TryGetValue(serial, out bool wasOk) || wasOk != ok)
        {
            if (ok) Console.WriteLine($"[i] ADB verbunden: {serial}");
            else Console.WriteLine($"[!] ADB-Verbindung zu {serial} fehlgeschlagen: {output.Trim()}");
        }
        _lastConnectResult[serial] = ok;

        return ok;
    }

    public async Task<string> GetPropAsync(string serial, string prop) =>
        (await RunAsync($"-s {serial} shell getprop {prop}")).Trim();

    /// <summary>Sends a tap/swipe based on a normalized touch event. Coordinates are scaled using the device's real screen size.</summary>
    public async void SendTap(string serial, TouchEventPayload touch)
    {
        var (w, h) = await GetScreenSizeAsync(serial);
        int x = (int)(touch.X * w);
        int y = (int)(touch.Y * h);

        switch (touch.Action)
        {
            case "tap":
            case "down": // treat a lone "down" without matching "move" as a tap for simplicity;
                         // true drag sequences are handled by the caller sending down->move->up
                         // and are converted into a single `input swipe` covering the path in InputSimulator instead.
                await RunAsync($"-s {serial} shell input tap {x} {y}");
                break;
            case "doubletap":
                await RunAsync($"-s {serial} shell input tap {x} {y}");
                await Task.Delay(80);
                await RunAsync($"-s {serial} shell input tap {x} {y}");
                break;
            case "longpress":
                await RunAsync($"-s {serial} shell input swipe {x} {y} {x} {y} 600");
                break;
        }
    }

    public async void SendKeyEvent(string serial, int androidKeyCode) =>
        await RunAsync($"-s {serial} shell input keyevent {androidKeyCode}");

    public async void SendText(string serial, string text)
    {
        // ADB's `input text` needs spaces escaped and does not support all unicode; for full
        // unicode support consider pushing an IME broadcast intent instead (ADBKeyboard app).
        string escaped = text.Replace(" ", "%s").Replace("'", "\\'");
        await RunAsync($"-s {serial} shell input text \"{escaped}\"");
    }

    public async void SendHardwareButton(string serial, string button)
    {
        int keyCode = button.ToLowerInvariant() switch
        {
            "home" => 3,
            "back" => 4,
            "menu" => 82,
            "recents" => 187,
            "volumeup" => 24,
            "volumedown" => 25,
            "power" => 26,
            "rotate" => -1, // handled specially below
            _ => -1,
        };

        if (button.ToLowerInvariant() == "rotate")
        {
            // Rotation is a settings toggle, not a keyevent.
            await RunAsync($"-s {serial} shell settings put system accelerometer_rotation 0");
            string current = await RunAsync($"-s {serial} shell settings get system user_rotation");
            int next = current.Trim() == "0" ? 1 : 0;
            await RunAsync($"-s {serial} shell settings put system user_rotation {next}");
            return;
        }

        if (button.ToLowerInvariant() == "screenshot")
        {
            await RunAsync($"-s {serial} shell screencap -p /sdcard/rc_screenshot.png");
            return;
        }

        if (keyCode > 0)
            await RunAsync($"-s {serial} shell input keyevent {keyCode}");
    }

    public async Task<string> RunRawCommandAsync(string serial, string command) =>
        await RunAsync($"-s {serial} {command}");

    public async Task<byte[]> CaptureScreenshotPngAsync(string serial)
    {
        // `adb exec-out` streams the PNG directly to stdout without touching the device's storage.
        return await RunBinaryAsync($"-s {serial} exec-out screencap -p");
    }

    public async Task InstallApkAsync(string serial, string localApkPath) =>
        await RunAsync($"-s {serial} install -r \"{localApkPath}\"");

    private readonly Dictionary<string, (int w, int h)> _screenSizeCache = new();

    private async Task<(int w, int h)> GetScreenSizeAsync(string serial)
    {
        if (_screenSizeCache.TryGetValue(serial, out var cached)) return cached;

        string output = await RunAsync($"-s {serial} shell wm size");
        // Output like: "Physical size: 1280x720"
        var match = System.Text.RegularExpressions.Regex.Match(output, @"(\d+)x(\d+)");
        var size = match.Success
            ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value))
            : (1280, 720); // sane fallback

        _screenSizeCache[serial] = size;
        return size;
    }

    private async Task<string> RunAsync(string args)
    {
        if (_adbPath == null) return string.Empty;

        var psi = new ProcessStartInfo(_adbPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null) return string.Empty;
        string stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return stdout;
    }

    private async Task<byte[]> RunBinaryAsync(string args)
    {
        if (_adbPath == null) return Array.Empty<byte>();

        var psi = new ProcessStartInfo(_adbPath, args)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null) return Array.Empty<byte>();

        using var ms = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(ms);
        await process.WaitForExitAsync();
        return ms.ToArray();
    }
}
