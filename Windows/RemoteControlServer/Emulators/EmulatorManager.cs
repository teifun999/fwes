using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using RemoteControlServer.Adb;
using RemoteControlServer.Core;

namespace RemoteControlServer.Emulators;

/// <summary>
/// Central registry that polls every registered IEmulatorPlugin, merges their results into one
/// flat list of EmulatorInfo DTOs, and dispatches lifecycle calls (start/stop/restart) to the
/// plugin that owns a given instance ID. Runs a background poll loop so the iOS app's dashboard
/// stays in sync with newly launched/closed emulator windows without needing a manual refresh.
/// </summary>
public class EmulatorManager : IDisposable
{
    private readonly List<IEmulatorPlugin> _plugins = new();
    private readonly List<IEmulatorPlugin> _pendingPlugins = new(); // detected as "not installed yet" - re-checked every poll
    private readonly Dictionary<string, EmulatorInstance> _instances = new();
    private readonly Dictionary<string, IEmulatorPlugin> _ownerByInstanceId = new();
    private readonly object _lock = new();
    private System.Threading.Timer? _pollTimer;
    private string? _activeInstanceId;
    private readonly GenericAdbPlugin _genericPlugin;
    private readonly AdbClient _adb;

    public event Action<List<EmulatorInfo>>? OnEmulatorListChanged;

    public EmulatorManager(AdbClient adb)
    {
        _adb = adb;
        var ldPlayer = new LDPlayerPlugin();
        var blueStacks = new BlueStacksPlugin();

        if (ldPlayer.IsInstalled()) _plugins.Add(ldPlayer); else _pendingPlugins.Add(ldPlayer);
        if (blueStacks.IsInstalled()) _plugins.Add(blueStacks); else _pendingPlugins.Add(blueStacks);

        // Generic plugin always added last so it only picks up serials the dedicated plugins didn't claim.
        _genericPlugin = new GenericAdbPlugin(adb, serial =>
        {
            lock (_lock) return _instances.Values.Any(i => i.AdbSerial == serial);
        });
        _plugins.Add(_genericPlugin);
    }

    public void StartPolling(TimeSpan interval)
    {
        _pollTimer = new System.Threading.Timer(async _ => await RefreshAsync(), null, TimeSpan.Zero, interval);
    }

    public async Task RefreshAsync()
    {
        // Emulators that weren't found at server startup (e.g. BlueStacks installed to a custom
        // location we couldn't detect yet, or simply not installed at that moment) get promoted
        // as soon as they become detectable, instead of requiring a server restart.
        for (int i = _pendingPlugins.Count - 1; i >= 0; i--)
        {
            if (_pendingPlugins[i].IsInstalled())
            {
                var plugin = _pendingPlugins[i];
                _pendingPlugins.RemoveAt(i);
                _plugins.Insert(Math.Max(0, _plugins.Count - 1), plugin); // keep generic plugin last
                Console.WriteLine($"[i] Emulator plugin now detected: {plugin.EmulatorType}");
            }
        }

        var merged = new Dictionary<string, EmulatorInstance>();
        var owners = new Dictionary<string, IEmulatorPlugin>();

        foreach (var plugin in _plugins)
        {
            List<EmulatorInstance> instances;
            try
            {
                instances = await plugin.DiscoverInstancesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Plugin {plugin.EmulatorType} discovery failed: {ex.Message}");
                continue;
            }

            foreach (var instance in instances)
            {
                merged[instance.Id] = instance;
                owners[instance.Id] = plugin;
            }
        }

        lock (_lock)
        {
            _instances.Clear();
            foreach (var kvp in merged) _instances[kvp.Key] = kvp.Value;

            _ownerByInstanceId.Clear();
            foreach (var kvp in owners) _ownerByInstanceId[kvp.Key] = kvp.Value;
        }

        // Register every TCP-based ADB endpoint (LDPlayer/BlueStacks) with the local adb server.
        // Without this, "-s 127.0.0.1:port shell ..." commands fail even though adb.exe itself
        // works fine - see AdbClient.EnsureConnectedAsync for details.
        if (!_adb.IsAvailable)
        {
            // adb.exe wasn't on PATH/known locations at server startup - now that we actually know
            // where an emulator is installed, try that folder too instead of requiring a restart.
            foreach (var instance in merged.Values)
            {
                if (instance.InstallPath != null && _adb.TryDiscoverFromInstallPath(instance.InstallPath))
                    break;
            }
        }

        if (_adb.IsAvailable)
        {
            var serials = merged.Values.Select(i => i.AdbSerial).Where(s => s != null).Distinct().ToList();
            await Task.WhenAll(serials.Select(s => _adb.EnsureConnectedAsync(s!)));
        }

        OnEmulatorListChanged?.Invoke(GetAll());
    }

    public List<EmulatorInfo> GetAll()
    {
        lock (_lock)
        {
            return _instances.Values.Select(i => new EmulatorInfo
            {
                Id = i.Id,
                Name = i.Name,
                Type = i.Type,
                Status = i.Status,
                AdbSerial = i.AdbSerial,
                ResolutionW = i.ResolutionW,
                ResolutionH = i.ResolutionH,
                IsActive = i.Id == _activeInstanceId,
            }).ToList();
        }
    }

    public EmulatorInstance? Get(string id)
    {
        lock (_lock) return _instances.GetValueOrDefault(id);
    }

    public void SetActive(string id)
    {
        lock (_lock) _activeInstanceId = id;
    }

    public async Task StartAsync(string id)
    {
        if (!_ownerByInstanceId.TryGetValue(id, out var plugin)) return;
        await plugin.StartAsync(id);
        await RefreshAsync();
    }

    public async Task StopAsync(string id)
    {
        if (!_ownerByInstanceId.TryGetValue(id, out var plugin)) return;
        await plugin.StopAsync(id);
        await RefreshAsync();
    }

    public async Task RestartAsync(string id)
    {
        if (!_ownerByInstanceId.TryGetValue(id, out var plugin)) return;
        await plugin.RestartAsync(id);
        await RefreshAsync();
    }

    public void Dispose() => _pollTimer?.Dispose();
}
