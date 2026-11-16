using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PerfWidget;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class Native
{
    public const int SystemProcessInformation = 5;
    public const int SystemPageFileInformation = 18;
    public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("ntdll.dll")]
    public static extern uint NtQuerySystemInformation(int cls, IntPtr buf, int len, out int retLen);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
                       SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pi, uint cb);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder sb, ref int size);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint QueryDosDevice(string device, StringBuilder target, int max);

    public static string? ImagePath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("ntdll.dll")]
    static extern uint NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);

    /// <summary>Full command line of another process (ProcessCommandLineInformation, no admin needed).</summary>
    public static string? CommandLine(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int len = 4096;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr b = Marshal.AllocHGlobal(len);
                try
                {
                    uint st = NtQueryInformationProcess(h, 60, b, len, out int ret);
                    if (st == 0)
                    {
                        int l = (ushort)Marshal.ReadInt16(b, 0);
                        IntPtr p = Marshal.ReadIntPtr(b, 8);
                        return p == IntPtr.Zero || l == 0 ? null : Marshal.PtrToStringUni(p, l / 2);
                    }
                    if (st is 0xC0000004 or 0x80000005 or 0xC0000023) { len = Math.Max(ret + 64, len * 2); continue; }
                    return null;
                }
                finally { Marshal.FreeHGlobal(b); }
            }
            return null;
        }
        finally { CloseHandle(h); }
    }

    // ---------------- PDH ----------------
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQuery(string? src, IntPtr user, out IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
    [DllImport("pdh.dll")]
    public static extern uint PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll")]
    public static extern uint PdhCloseQuery(IntPtr q);
    [DllImport("pdh.dll")]
    public static extern uint PdhGetFormattedCounterValue(IntPtr c, uint fmt, IntPtr type, out PDH_FMT_COUNTERVALUE v);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArray(IntPtr c, uint fmt, ref uint size, out uint count, IntPtr buf);

    [StructLayout(LayoutKind.Explicit)]
    public struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double Double;
    }

    public const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;
}

/// <summary>Thin wrapper over a single PDH query. Only touched from the collector thread.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
sealed class Pdh : IDisposable
{
    readonly IntPtr q;
    IntPtr buf;
    uint bufSize = 256 * 1024;
    const uint Fmt = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;

    public Pdh()
    {
        Native.PdhOpenQuery(null, IntPtr.Zero, out q);
        buf = Marshal.AllocHGlobal((int)bufSize);
    }

    public IntPtr Add(string path) =>
        q != IntPtr.Zero && Native.PdhAddEnglishCounter(q, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;

    public void Collect() { if (q != IntPtr.Zero) Native.PdhCollectQueryData(q); }

    public double Get(IntPtr c)
    {
        if (c == IntPtr.Zero) return 0;
        return Native.PdhGetFormattedCounterValue(c, Fmt, IntPtr.Zero, out var v) == 0 && v.CStatus <= 1 ? v.Double : 0;
    }

    public List<(string Name, double Value)> GetArray(IntPtr c)
    {
        var list = new List<(string, double)>();
        if (c == IntPtr.Zero) return list;
        uint size = bufSize;
        uint st = Native.PdhGetFormattedCounterArray(c, Fmt, ref size, out uint count, buf);
        if (st == Native.PDH_MORE_DATA)
        {
            Marshal.FreeHGlobal(buf);
            bufSize = size + 64 * 1024;
            buf = Marshal.AllocHGlobal((int)bufSize);
            size = bufSize;
            st = Native.PdhGetFormattedCounterArray(c, Fmt, ref size, out count, buf);
        }
        if (st != 0) return list;
        for (int i = 0; i < count; i++)
        {
            IntPtr item = buf + i * 24; // PDH_FMT_COUNTERVALUE_ITEM_W on x64
            string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
            uint status = (uint)Marshal.ReadInt32(item, 8);
            double val = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
            if (status <= 1) list.Add((name, val));
        }
        return list;
    }

    public void Dispose()
    {
        if (q != IntPtr.Zero) Native.PdhCloseQuery(q);
        Marshal.FreeHGlobal(buf);
    }
}
