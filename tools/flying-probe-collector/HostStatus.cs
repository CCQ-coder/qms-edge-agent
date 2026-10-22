using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

internal struct CpuTimes
{
    internal readonly ulong Idle;
    internal readonly ulong Kernel;
    internal readonly ulong User;

    internal CpuTimes(ulong idle, ulong kernel, ulong user)
    {
        Idle = idle;
        Kernel = kernel;
        User = user;
    }
}

internal sealed class HostStatusSample
{
    internal readonly string HostName;
    internal readonly DateTime SampledAtUtc;
    internal readonly double? CpuPercent;
    internal readonly ulong MemoryTotalBytes;
    internal readonly ulong MemoryAvailableBytes;
    internal readonly long SystemDiskTotalBytes;
    internal readonly long SystemDiskFreeBytes;
    internal readonly ulong UptimeSeconds;

    internal HostStatusSample(string hostName, DateTime sampledAtUtc, double? cpuPercent,
        ulong memoryTotalBytes, ulong memoryAvailableBytes, long systemDiskTotalBytes,
        long systemDiskFreeBytes, ulong uptimeSeconds)
    {
        HostName = hostName;
        SampledAtUtc = sampledAtUtc;
        CpuPercent = cpuPercent;
        MemoryTotalBytes = memoryTotalBytes;
        MemoryAvailableBytes = memoryAvailableBytes;
        SystemDiskTotalBytes = systemDiskTotalBytes;
        SystemDiskFreeBytes = systemDiskFreeBytes;
        UptimeSeconds = uptimeSeconds;
    }
}

internal sealed class HostStatusSampler
{
    private CpuTimes? previous;

    internal HostStatusSample Capture()
    {
        FileTime idle;
        FileTime kernel;
        FileTime user;
        if (!GetSystemTimes(out idle, out kernel, out user)) throw new Win32Exception(Marshal.GetLastWin32Error());
        CpuTimes current = new CpuTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
        double? cpuPercent = CalculateCpuPercent(previous, current);
        previous = current;

        MemoryStatus memory = new MemoryStatus();
        memory.Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
        if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(Marshal.GetLastWin32Error());

        DriveInfo disk = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
        return new HostStatusSample(Environment.MachineName, DateTime.UtcNow, cpuPercent,
            memory.TotalPhysical, memory.AvailablePhysical, disk.TotalSize, disk.AvailableFreeSpace,
            GetTickCount64() / 1000);
    }

    internal static double? CalculateCpuPercent(CpuTimes? previous, CpuTimes current)
    {
        if (!previous.HasValue) return null;
        CpuTimes before = previous.Value;
        if (current.Idle < before.Idle || current.Kernel < before.Kernel || current.User < before.User) return null;
        ulong idle = current.Idle - before.Idle;
        ulong total = current.Kernel - before.Kernel + current.User - before.User;
        if (total == 0 || idle > total) return null;
        return Math.Round(100.0 * (total - idle) / total, 2);
    }

    private static ulong ToUInt64(FileTime value)
    {
        return ((ulong)value.High << 32) | value.Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
