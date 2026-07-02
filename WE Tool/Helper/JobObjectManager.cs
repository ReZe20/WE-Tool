using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WE_Tool.Helper;

public static class JobObjectManager
{
    private static readonly IntPtr _jobHandle;

    static JobObjectManager()
    {
        _jobHandle = CreateJobObject(IntPtr.Zero, null);

        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = 0x2000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };

        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = info
        };

        int length = Marshal.SizeOf(extendedInfo);
        IntPtr ptr = Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(extendedInfo, ptr, false);

            if (!SetInformationJobObject(_jobHandle, 9, ptr, (uint)length))
            {
                int error = Marshal.GetLastWin32Error();
                Serilog.Log.Warning("[JobObject] SetInformationJobObject failed: {Error}", error);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static void AddProcess(IntPtr processHandle)
    {
        if (_jobHandle == IntPtr.Zero) return;

        if (!AssignProcessToJobObject(_jobHandle, processHandle))
        {
            int error = Marshal.GetLastWin32Error();
            // 5 = Access Denied (process already exited)
            // 87 = Already in a job
            if (error != 5 && error != 87)
                Serilog.Log.Warning("[JobObject] AssignProcessToJobObject failed: {Error}", error);
        }
    }

    #region Win32 API

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass,
        IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryLimit;
        public UIntPtr PeakJobMemoryLimit;
    }

    #endregion
}
