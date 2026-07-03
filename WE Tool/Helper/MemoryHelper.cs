using System;
using System.Runtime.InteropServices;

namespace WE_Tool.Helper;

public static class MemoryHelper
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>总物理内存（MB）</summary>
    public static long GetTotalPhysicalMemoryMB()
    {
        var memStatus = GetMemoryStatus();
        return (long)(memStatus.ullTotalPhys / (1024 * 1024));
    }

    /// <summary>当前已用物理内存（MB）</summary>
    public static long GetUsedPhysicalMemoryMB()
    {
        var memStatus = GetMemoryStatus();
        ulong used = memStatus.ullTotalPhys - memStatus.ullAvailPhys;
        return (long)(used / (1024 * 1024));
    }

    private static MEMORYSTATUSEX GetMemoryStatus()
    {
        var memStatus = new MEMORYSTATUSEX();
        memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));

        if (!GlobalMemoryStatusEx(ref memStatus))
            throw new InvalidOperationException("GlobalMemoryStatusEx failed.");

        return memStatus;
    }
}
