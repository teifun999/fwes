using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using RemoteControlServer.Adb;

namespace RemoteControlServer.Emulators;

/// <summary>
/// Fallback/auto-detection plugin for emulators that don't have a dedicated plugin yet
/// (MuMu Player, NoxPlayer, MEmu, Genymotion, ...). Rather than special-casing every vendor's
/// install layout, this plugin:
///   1. Runs `adb devices -l` and treats every connected device that is NOT already claimed by
///      LDPlayerPlugin/BlueStacksPlugin as a "generic" instance.
///   2. Identifies the emulator brand via `adb shell getprop ro.product.manufacturer` /
///      `ro.build.product`, which most emulator vendors set to their own name
///      (e.g. "Nox", "MuMu", "MEmu"), purely for display purposes.
///   3. Matches the ADB device's window by scanning top-level windows whose process image
///      matches known emulator executable names, so Windows-input fallback still works.
///
/// New "real" plugins can always be added later (see IEmulatorPlugin) to give a vendor first-class
/// treatment (native start/stop instead of only ADB reboot, config-file parsing, etc.) - this
/// plugin only needs to stop reporting an instance once a dedicated plugin claims its serial.
/// </summary>
public class GenericAdbPlugin : IEmulatorPlugin
{
    public string EmulatorType => "Generic";

    private readonly AdbClient _adb;
    private readonly Func<string, bool> _isSerialClaimed;

    private static readonly Dictionary<string, string[]> KnownProcessNames = new()
    {
        ["MuMu"] = new[] { "MuMuPlayer", "NemuPlayer" },
        ["NoxPlayer"] = new[] { "Nox", "NoxVMHandle" },
        ["MEmu"] = new[] { "MEmu", "MEmuHeadless" },
        ["Genymotion"] = new[] { "player" },
    };

    /// <param name="isSerialClaimed">Callback so this plugin can skip serials already owned by a dedicated plugin (LDPlayer/BlueStacks).</param>
    public GenericAdbPlugin(AdbClient adb, Func<string, bool> isSerialClaimed)
    {
        _adb = adb;
        _isSerialClaimed = isSerialClaimed;
    }

    public bool IsInstalled() => _adb.IsAvailable; // "installed" here just means ADB is usable at all

    public async Task<List<EmulatorInstance>> DiscoverInstancesAsync()
    {
        var result = new List<EmulatorInstance>();
        if (!_adb.IsAvailable) return result;

        var devices = await _adb.ListDevicesAsync();
        foreach (var serial in devices)
        {
            if (_isSerialClaimed(serial)) continue;

            string manufacturer = await _adb.GetPropAsync(serial, "ro.product.manufacturer");
            string brand = GuessBrand(manufacturer, serial);

            var proc = FindMatchingProcess(brand);

            result.Add(new EmulatorInstance
            {
                Id = $"generic-{serial.Replace(":", "_").Replace(".", "_")}",
                Name = $"{brand} ({serial})",
                Type = brand,
                Status = "running", // if adb sees it, it's running
                AdbSerial = serial,
                WindowHandle = proc?.MainWindowHandle,
            });
        }
        return result;
    }

    private static string GuessBrand(string manufacturer, string serial)
    {
        foreach (var kvp in KnownProcessNames)
        {
            if (manufacturer.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Key;
        }
        return string.IsNullOrWhiteSpace(manufacturer) ? "Unknown Emulator" : manufacturer;
    }

    private static Process? FindMatchingProcess(string brand)
    {
        if (!KnownProcessNames.TryGetValue(brand, out var names)) return null;
        foreach (var name in names)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) return procs[0];
        }
        return null;
    }

    // Generic instances are ADB-only: lifecycle management for unrecognized emulators is
    // intentionally not implemented (we don't know their install layout). Users get full
    // touch/key/adb-command control, just not start/stop/restart from this app.
    public Task StartAsync(string instanceId) =>
        throw new NotSupportedException("Lifecycle control isn't available for auto-detected emulators. Add a dedicated plugin for this vendor.");

    public Task StopAsync(string instanceId) =>
        throw new NotSupportedException("Lifecycle control isn't available for auto-detected emulators. Add a dedicated plugin for this vendor.");

    public Task RestartAsync(string instanceId) =>
        throw new NotSupportedException("Lifecycle control isn't available for auto-detected emulators. Add a dedicated plugin for this vendor.");

    public IntPtr? GetWindowHandle(string instanceId) => null;
}
