using System;
using System.Threading.Tasks;
using System.Collections.Generic;
namespace RemoteControlServer.Emulators;

/// <summary>
/// Contract every emulator integration must implement. New emulators (MuMu, NoxPlayer, MEmu, ...)
/// are added by dropping in a new class that implements this interface and registering it in
/// EmulatorManager - no changes to the WebSocket server or iOS app are required, since everything
/// downstream only talks to the common EmulatorInfo/IEmulatorPlugin abstraction.
/// </summary>
public interface IEmulatorPlugin
{
    /// <summary>Display name, e.g. "LDPlayer", "BlueStacks", "MuMu Player".</summary>
    string EmulatorType { get; }

    /// <summary>
    /// Returns true if this emulator is installed on the system (checked via registry keys,
    /// known install paths, or running processes).
    /// </summary>
    bool IsInstalled();

    /// <summary>Enumerates every instance of this emulator currently known (running or configured).</summary>
    Task<List<EmulatorInstance>> DiscoverInstancesAsync();

    Task StartAsync(string instanceId);
    Task StopAsync(string instanceId);
    Task RestartAsync(string instanceId);

    /// <summary>
    /// The window handle (HWND) hosting the emulator's rendered Android screen, used as a
    /// fallback capture/input target when ADB is unavailable.
    /// </summary>
    IntPtr? GetWindowHandle(string instanceId);
}

/// <summary>Internal representation of a single running/known emulator instance, before being converted to the wire DTO.</summary>
public class EmulatorInstance
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = "stopped";
    public string? AdbSerial { get; set; }
    public IntPtr? WindowHandle { get; set; }
    public int ResolutionW { get; set; }
    public int ResolutionH { get; set; }
    public string? InstallPath { get; set; }
    public string? LaunchArgs { get; set; }
}
