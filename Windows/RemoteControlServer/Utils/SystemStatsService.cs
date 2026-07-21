using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteControlServer.Utils;

/// <summary>Reports host PC CPU/RAM utilization for the iOS dashboard.</summary>
public class SystemStatsService
{
    private readonly PerformanceCounter _cpuCounter;

    public SystemStatsService()
    {
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _cpuCounter.NextValue(); // first call always returns 0, prime it
    }

    public double GetCpuPercent() => Math.Round(_cpuCounter.NextValue(), 1);

    public double GetRamPercent()
    {
        // GlobalMemoryStatusEx gives a true system-wide reading rather than just this process.
        var status = new MEMORYSTATUSEX();
        return GlobalMemoryStatusEx(status) ? status.dwMemoryLoad : 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
