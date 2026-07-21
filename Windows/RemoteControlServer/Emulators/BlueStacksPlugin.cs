using System.Threading.Tasks;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace RemoteControlServer.Emulators;

/// <summary>
/// BlueStacks integration. Unlike LDPlayer, BlueStacks does not ship one unified CLI console;
/// instead each instance is a separate "HD-Player.exe" process and configuration lives in
/// bluestacks.conf (typically under %ProgramData%\BlueStacks_nxt). We parse that file to
/// enumerate configured instances, use registry/install-path lookup for the binary, and control
/// instances by starting/killing the corresponding HD-Player.exe process
/// (e.g. "HD-Player.exe" --instance "Rvc64").
///
/// Each instance also runs an ADB bridge; BlueStacks5/BlueStacks Air5 default to a
/// per-instance ADB port stored in bluestacks.conf as "bst.instance.&lt;name&gt;.status.adb_port".
/// </summary>
public class BlueStacksPlugin : IEmulatorPlugin
{
    public string EmulatorType => "BlueStacks";

    private string? _installPath;
    private string? _configPath;

    public bool IsInstalled()
    {
        _installPath = FindInstallPath();
        _configPath = FindConfigPath();

        // Even if we couldn't resolve an install folder (unusual custom setup), BlueStacks
        // clearly exists on this PC if it's running right now - don't hide the plugin in that case.
        bool processRunning = Process.GetProcessesByName("HD-Player").Length > 0;

        return _installPath != null || _configPath != null || processRunning;
    }

    // BlueStacks ships several product lines over the years, each with its own registry key
    // and default folder name (5 / 5 Air / X / "nxt"), and a LOT of users install it to a custom
    // drive/folder to save space on C:. The previous version only checked one registry key and
    // two hardcoded C:\ paths, which is why it silently found nothing on many real setups.
    // This version checks every known key (both the 64-bit and 32-bit registry views), then every
    // known default folder name on every fixed drive, and finally falls back to the currently
    // running HD-Player.exe process's own path if the app is open right now.
    private static readonly string[] RegistryKeyNames =
    {
        @"SOFTWARE\BlueStacks_nxt",
        @"SOFTWARE\WOW6432Node\BlueStacks_nxt",
        @"SOFTWARE\BlueStacks_nxt_cn",
        @"SOFTWARE\WOW6432Node\BlueStacks_nxt_cn",
        @"SOFTWARE\Bluestacks",
        @"SOFTWARE\WOW6432Node\Bluestacks",
    };

    private static readonly string[] FolderNames =
    {
        "BlueStacks_nxt",
        "BlueStacks_nxt_cn",
        "BlueStacks",
        "BlueStacks5",
        "BlueStacksX",
    };

    private string? FindInstallPath()
    {
        // 1) Registry - try every known key name, checking both hive views explicitly since the
        //    process this server runs as (32 vs 64 bit) changes which view "Registry.LocalMachine"
        //    resolves to by default.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            foreach (var keyName in RegistryKeyNames)
            {
                try
                {
                    using var key = baseKey.OpenSubKey(keyName);
                    var path = (key?.GetValue("InstallDir") ?? key?.GetValue("DataDir")) as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "HD-Player.exe")))
                        return path;
                }
                catch { /* ignore this key, try the next one */ }
            }
        }

        // 2) Common install locations on every fixed drive (not just C:), since BlueStacks is a
        //    large install and a lot of people point it at D:/E: during setup.
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

            foreach (var folder in FolderNames)
            {
                foreach (var programFiles in new[] { "Program Files", "Program Files (x86)" })
                {
                    string candidate = Path.Combine(drive.Name, programFiles, folder);
                    if (File.Exists(Path.Combine(candidate, "HD-Player.exe")))
                        return candidate;
                }
            }
        }

        // 3) Last resort: BlueStacks is simply already running - ask Windows where that .exe lives.
        try
        {
            var running = Process.GetProcessesByName("HD-Player").FirstOrDefault();
            string? modulePath = running?.MainModule?.FileName;
            if (!string.IsNullOrEmpty(modulePath))
                return Path.GetDirectoryName(modulePath);
        }
        catch { /* access to MainModule can be denied depending on process elevation; ignore */ }

        return null;
    }

    private static string? FindConfigPath()
    {
        foreach (var folder in FolderNames)
        {
            string candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                folder, "bluestacks.conf");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public async Task<List<EmulatorInstance>> DiscoverInstancesAsync()
    {
        var result = new List<EmulatorInstance>();

        // No bluestacks.conf found (different data folder than we know about, or a fresh install
        // with no instance created yet) - if HD-Player.exe is running anyway, still surface it so
        // it's not simply invisible. It just won't have a resolved ADB serial, so control falls
        // back to Windows input simulation (see RemoteControlWebSocketServer.DispatchTouch etc.).
        if (_configPath == null || !File.Exists(_configPath))
        {
            foreach (var proc in Process.GetProcessesByName("HD-Player"))
            {
                string name = string.IsNullOrWhiteSpace(proc.MainWindowTitle) ? "BlueStacks" : proc.MainWindowTitle;
                result.Add(new EmulatorInstance
                {
                    Id = $"bluestacks-{proc.Id}",
                    Name = name,
                    Type = EmulatorType,
                    Status = "running",
                    WindowHandle = proc.MainWindowHandle,
                    AdbSerial = null,
                    InstallPath = _installPath,
                });
            }
            return result;
        }

        var lines = await File.ReadAllLinesAsync(_configPath);
        var instanceNames = new HashSet<string>();

        // Config lines look like: bst.instance.Rvc64.status.display_name="Rvc64"
        foreach (var line in lines)
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"bst\.instance\.([^\.]+)\.");
            if (match.Success) instanceNames.Add(match.Groups[1].Value);
        }

        var runningProcesses = Process.GetProcessesByName("HD-Player");

        foreach (var name in instanceNames)
        {
            string adbPortKey = $"bst.instance.{name}.status.adb_port";
            string? adbPort = lines.FirstOrDefault(l => l.StartsWith(adbPortKey))
                ?.Split('=').ElementAtOrDefault(1)?.Trim('"');

            bool isRunning = runningProcesses.Any(p =>
                p.MainWindowTitle.Contains(name, StringComparison.OrdinalIgnoreCase));

            var proc = runningProcesses.FirstOrDefault(p =>
                p.MainWindowTitle.Contains(name, StringComparison.OrdinalIgnoreCase));

            result.Add(new EmulatorInstance
            {
                Id = $"bluestacks-{name}",
                Name = name,
                Type = EmulatorType,
                Status = isRunning ? "running" : "stopped",
                WindowHandle = proc?.MainWindowHandle,
                AdbSerial = isRunning && !string.IsNullOrEmpty(adbPort) ? $"127.0.0.1:{adbPort}" : null,
                InstallPath = _installPath,
            });
        }

        return result;
    }

    public Task StartAsync(string instanceId)
    {
        if (_installPath == null) return Task.CompletedTask;
        string name = ExtractName(instanceId);
        string exe = Path.Combine(_installPath, "HD-Player.exe");

        Process.Start(new ProcessStartInfo(exe, $"--instance \"{name}\"")
        {
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(string instanceId)
    {
        string name = ExtractName(instanceId);
        foreach (var proc in Process.GetProcessesByName("HD-Player"))
        {
            if (proc.MainWindowTitle.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                proc.CloseMainWindow();
                if (!proc.WaitForExit(5000)) proc.Kill();
            }
        }
        return Task.CompletedTask;
    }

    public async Task RestartAsync(string instanceId)
    {
        await StopAsync(instanceId);
        await Task.Delay(2000);
        await StartAsync(instanceId);
    }

    public IntPtr? GetWindowHandle(string instanceId) => null;

    private static string ExtractName(string instanceId) => instanceId.Replace("bluestacks-", "");
}
