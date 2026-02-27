using System.Runtime.InteropServices;

namespace Oxdaed.Agent.SystemInfo;

public static class Metrics
{
    private static ulong _prevIdle, _prevKernel, _prevUser;
    private static bool _cpuInit;

    public static double TryGetCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        ulong idleTicks = ToUInt64(idle);
        ulong kernelTicks = ToUInt64(kernel);
        ulong userTicks = ToUInt64(user);

        if (!_cpuInit)
        {
            _prevIdle = idleTicks; _prevKernel = kernelTicks; _prevUser = userTicks;
            _cpuInit = true;
            return 0;
        }

        ulong idleDiff = idleTicks - _prevIdle;
        ulong kernelDiff = kernelTicks - _prevKernel;
        ulong userDiff = userTicks - _prevUser;

        _prevIdle = idleTicks; _prevKernel = kernelTicks; _prevUser = userTicks;

        ulong total = kernelDiff + userDiff;
        if (total == 0) return 0;

        return (1.0 - ((double)idleDiff / total)) * 100.0;
    }

    public static double GetRamPercent()
    {
        MEMORYSTATUSEX mem = default;
        mem.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();

        if (!GlobalMemoryStatusEx(ref mem) || mem.ullTotalPhys == 0)
            return 0;

        double used = mem.ullTotalPhys - mem.ullAvailPhys;
        return (used / mem.ullTotalPhys) * 100.0;
    }

    public static double GetDiskPercent(string driveRoot)
    {
        try
        {
            var d = new DriveInfo(driveRoot);
            if (!d.IsReady || d.TotalSize == 0) return 0;
            return (double)(d.TotalSize - d.TotalFreeSpace) / d.TotalSize * 100.0;
        }
        catch { return 0; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static ulong ToUInt64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

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
}
