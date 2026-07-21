using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using Microsoft.Win32;

namespace RemoteControlServer.Emulators;

/// <summary>
/// LDPlayer integration. LDPlayer ships a CLI tool "ldconsole.exe" in its install directory
/// that supports listing/launching/quitting instances - we shell out to it rather than reverse
/// engineering its internals, which keeps this plugin robust across LDPlayer updates.
///
/// Reference commands (as of LDPlayer 9.x):
///   ldconsole.exe list2                     -> list all instances with index/name/pid/status
///   ldconsole.exe launch --index N          -> start instance N
///   ldconsole.exe quit --index N            -> stop instance N
///   ldconsole.exe reboot --index N          -> restart instance N
/// Each running instance also exposes an ADB endpoint at 127.0.0.1:5555+N*2 (index-dependent),
/// which EmulatorManager cross-references via `adb devices` to fill in AdbSerial.
/// </summary>
public class LDPlayerPlugin : IEmulatorPlugin
{
    public string EmulatorType => "LDPlayer";

    private string? _installPath;

    public bool IsInstalled()
    {
        _installPath = FindInstallPath();
        return _installPath != null;
    }

    private string? FindInstallPath()
    {
        // 1. Registry uninstall key (most reliable)
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\LDPlayer9");
            var path = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "ldconsole.exe")))
                return path;
        }
        catch { /* registry access can fail under restricted policies - fall through */ }

        // 2. Common default install locations
        string[] candidates =
        {
            @"C:\LDPlayer\LDPlayer9",
            @"C:\LDPlayer\LDPlayer4",
            @"C:\Program Files\LDPlayer\LDPlayer9",
        };
        return candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "ldconsole.exe")));
    }

    public async Task<List<EmulatorInstance>> DiscoverInstancesAsync()
    {
        var result = new List<EmulatorInstance>();
        if (_installPath == null) return result;

        string output = await RunLdConsoleAsync("list2");
        // Format per line: index,name,topWindowHandle,bindWindowHandle,isRunning,pid,vboxPid
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length < 5) continue;

            string index = parts[0];
            string name = parts[1];
            bool isRunning = parts[4] == "1";
            IntPtr topHwnd = long.TryParse(parts[2], out var h) ? new IntPtr(h) : IntPtr.Zero;

            result.Add(new EmulatorInstance
            {
                Id = $"ldplayer-{index}",
                Name = string.IsNullOrWhiteSpace(name) ? $"LDPlayer-{index}" : name,
                Type = EmulatorType,
                Status = isRunning ? "running" : "stopped",
                WindowHandle = topHwnd != IntPtr.Zero ? topHwnd : null,
                // LDPlayer's ADB bridge port convention: 5555 + index*2 (subject to user config in emulator settings)
                AdbSerial = isRunning ? $"127.0.0.1:{5555 + int.Parse(index) * 2}" : null,
                InstallPath = _installPath,
            });
        }
        return result;
    }

    public async Task StartAsync(string instanceId) =>
        await RunLdConsoleAsync($"launch --index {ExtractIndex(instanceId)}");

    public async Task StopAsync(string instanceId) =>
        await RunLdConsoleAsync($"quit --index {ExtractIndex(instanceId)}");

    public async Task RestartAsync(string instanceId) =>
        await RunLdConsoleAsync($"reboot --index {ExtractIndex(instanceId)}");

    public IntPtr? GetWindowHandle(string instanceId)
    {
        // Populated lazily via DiscoverInstancesAsync; EmulatorManager caches and passes through.
        return null;
    }

    private static string ExtractIndex(string instanceId) => instanceId.Replace("ldplayer-", "");

    private async Task<string> RunLdConsoleAsync(string args)
    {
        if (_installPath == null) return string.Empty;
        string exe = Path.Combine(_installPath, "ldconsole.exe");

        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null) return string.Empty;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}
