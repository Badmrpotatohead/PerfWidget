using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PerfWidget;

/// <summary>
/// NVIDIA GPU temperature / power / clocks / throttle reasons / utilization via NVML (Windows: nvml.dll,
/// Linux: libnvidia-ml.so.1). Silently absent on other GPUs and on macOS.
/// </summary>
public sealed class GpuSensors
{
    IntPtr dev;
    bool ok;
    bool useEventReasons = true;
    readonly uint slowdownTemp;

    public bool Available => ok;

    public GpuSensors()
    {
        if (OperatingSystem.IsMacOS()) return;
        try
        {
            if (Nvml.nvmlInit_v2() == 0 && Nvml.nvmlDeviceGetHandleByIndex_v2(0, out dev) == 0)
            {
                ok = true;
                if (Nvml.nvmlDeviceGetTemperatureThreshold(dev, 1 /* SLOWDOWN */, out var s) == 0) slowdownTemp = s;
            }
        }
        catch { ok = false; } // library missing (non-NVIDIA)
    }

    public string? Name()
    {
        if (!ok) return null;
        try
        {
            var b = new byte[96];
            return Nvml.nvmlDeviceGetName(dev, b, (uint)b.Length) == 0 ? System.Text.Encoding.UTF8.GetString(b).TrimEnd('\0') : null;
        }
        catch { return null; }
    }

    /// <summary>Whole-GPU utilization and VRAM (used when the OS has no better per-engine source, i.e. Linux).</summary>
    public bool ReadUtilization(out double gpuPct, out long vramUsed, out long vramTotal)
    {
        gpuPct = 0; vramUsed = 0; vramTotal = 0;
        if (!ok) return false;
        try
        {
            if (Nvml.nvmlDeviceGetUtilizationRates(dev, out var u) != 0) return false;
            gpuPct = u.gpu;
            if (Nvml.nvmlDeviceGetMemoryInfo(dev, out var m) == 0) { vramUsed = (long)m.used; vramTotal = (long)m.total; }
            return true;
        }
        catch { return false; }
    }

    public void Fill(Snapshot s)
    {
        if (!ok) return;
        try
        {
            if (Nvml.nvmlDeviceGetTemperature(dev, 0, out var t) == 0) s.GpuTemp = t;
            if (slowdownTemp > 0) s.GpuSlowdownTemp = slowdownTemp;
            if (Nvml.nvmlDeviceGetPowerUsage(dev, out var mw) == 0) s.GpuPowerW = mw / 1000.0;
            if (Nvml.nvmlDeviceGetEnforcedPowerLimit(dev, out var lim) == 0) s.GpuPowerLimitW = lim / 1000.0;
            if (Nvml.nvmlDeviceGetClockInfo(dev, 0, out var clk) == 0) s.GpuClockMHz = clk;
            if (Nvml.nvmlDeviceGetMaxClockInfo(dev, 0, out var mclk) == 0) s.GpuMaxClockMHz = mclk;
            if (Nvml.nvmlDeviceGetFanSpeed(dev, out var fan) == 0) s.GpuFanPct = fan;
            ulong r = 0;
            int rc = -1;
            if (useEventReasons)
            {
                try { rc = Nvml.nvmlDeviceGetCurrentClocksEventReasons(dev, out r); }
                catch (EntryPointNotFoundException) { useEventReasons = false; }
            }
            if (!useEventReasons) rc = Nvml.nvmlDeviceGetCurrentClocksThrottleReasons(dev, out r);
            if (rc == 0) s.GpuThrottleReasons = r;
        }
        catch { ok = false; }
    }

    public const ulong PowerCap = 0x4, HwSlowdown = 0x8, SwThermal = 0x20, HwThermal = 0x40, PowerBrake = 0x80;
    public const ulong BadThrottle = HwSlowdown | SwThermal | HwThermal | PowerBrake;

    public static string? Describe(ulong r)
    {
        var list = new List<string>();
        if ((r & HwThermal) != 0) list.Add("hardware thermal slowdown");
        if ((r & SwThermal) != 0) list.Add("thermal limit");
        if ((r & PowerBrake) != 0) list.Add("power brake");
        if ((r & HwSlowdown) != 0 && (r & (HwThermal | PowerBrake)) == 0) list.Add("hardware slowdown");
        if ((r & PowerCap) != 0) list.Add("power limit");
        return list.Count == 0 ? null : string.Join(", ", list);
    }
}

/// <summary>NVML entry points. "nvml" resolves to nvml.dll on Windows and libnvidia-ml.so.1 on Linux.</summary>
static class Nvml
{
    const string Lib = "nvml";

    static Nvml()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Nvml).Assembly, (name, asm, path) =>
            {
                if (name != Lib) return IntPtr.Zero;
                string file = OperatingSystem.IsWindows() ? "nvml.dll" : "libnvidia-ml.so.1";
                return NativeLibrary.TryLoad(file, asm, path, out var h) ? h : IntPtr.Zero;
            });
        }
        catch (InvalidOperationException) { } // a resolver is already registered for this assembly
    }

    [StructLayout(LayoutKind.Sequential)] public struct Utilization { public uint gpu, memory; }
    [StructLayout(LayoutKind.Sequential)] public struct Memory { public ulong total, free, used; }

    [DllImport(Lib)] public static extern int nvmlInit_v2();
    [DllImport(Lib)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr dev);
    [DllImport(Lib)] public static extern int nvmlDeviceGetName(IntPtr d, byte[] name, uint len);
    [DllImport(Lib)] public static extern int nvmlDeviceGetTemperature(IntPtr d, int sensor, out uint t);
    [DllImport(Lib)] public static extern int nvmlDeviceGetTemperatureThreshold(IntPtr d, int type, out uint t);
    [DllImport(Lib)] public static extern int nvmlDeviceGetPowerUsage(IntPtr d, out uint mw);
    [DllImport(Lib)] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr d, out uint mw);
    [DllImport(Lib)] public static extern int nvmlDeviceGetClockInfo(IntPtr d, int type, out uint mhz);
    [DllImport(Lib)] public static extern int nvmlDeviceGetMaxClockInfo(IntPtr d, int type, out uint mhz);
    [DllImport(Lib)] public static extern int nvmlDeviceGetFanSpeed(IntPtr d, out uint pct);
    [DllImport(Lib)] public static extern int nvmlDeviceGetCurrentClocksEventReasons(IntPtr d, out ulong r);
    [DllImport(Lib)] public static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr d, out ulong r);
    [DllImport(Lib)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr d, out Utilization u);
    [DllImport(Lib)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr d, out Memory m);
}
