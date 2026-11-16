using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;

namespace PerfWidget;

/// <summary>NVIDIA GPU temperature / power / clocks / throttle reasons via NVML. Silently absent on other GPUs.</summary>
sealed class GpuSensors
{
    IntPtr dev;
    bool ok;
    bool useEventReasons = true;
    readonly uint slowdownTemp;

    public GpuSensors()
    {
        try
        {
            if (Native.nvmlInit_v2() == 0 && Native.nvmlDeviceGetHandleByIndex_v2(0, out dev) == 0)
            {
                ok = true;
                if (Native.nvmlDeviceGetTemperatureThreshold(dev, 1 /* SLOWDOWN */, out var s) == 0) slowdownTemp = s;
            }
        }
        catch { ok = false; } // nvml.dll missing (non-NVIDIA)
    }

    public void Fill(Snapshot s)
    {
        if (!ok) return;
        try
        {
            if (Native.nvmlDeviceGetTemperature(dev, 0, out var t) == 0) s.GpuTemp = t;
            if (slowdownTemp > 0) s.GpuSlowdownTemp = slowdownTemp;
            if (Native.nvmlDeviceGetPowerUsage(dev, out var mw) == 0) s.GpuPowerW = mw / 1000.0;
            if (Native.nvmlDeviceGetEnforcedPowerLimit(dev, out var lim) == 0) s.GpuPowerLimitW = lim / 1000.0;
            if (Native.nvmlDeviceGetClockInfo(dev, 0, out var clk) == 0) s.GpuClockMHz = clk;
            if (Native.nvmlDeviceGetMaxClockInfo(dev, 0, out var mclk) == 0) s.GpuMaxClockMHz = mclk;
            if (Native.nvmlDeviceGetFanSpeed(dev, out var fan) == 0) s.GpuFanPct = fan;
            ulong r = 0;
            int rc = -1;
            if (useEventReasons)
            {
                try { rc = Native.nvmlDeviceGetCurrentClocksEventReasons(dev, out r); }
                catch (EntryPointNotFoundException) { useEventReasons = false; }
            }
            if (!useEventReasons) rc = Native.nvmlDeviceGetCurrentClocksThrottleReasons(dev, out r);
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

/// <summary>
/// CPU temperature. Windows has no driver-free way to read Ryzen temps, so this reads LibreHardwareMonitor's
/// WMI sensors when that app is running. Returns null otherwise; retries every minute.
/// </summary>
sealed class CpuTempReader
{
    long nextTry;
    double? last;
    public string? Source { get; private set; }

    public double? Read()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < nextTry) return last;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\LibreHardwareMonitor",
                "SELECT Identifier, Name, Value FROM Sensor WHERE SensorType='Temperature'");
            double? best = null, any = null;
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                using (mo)
                {
                    string id = (mo["Identifier"] as string ?? "").ToLowerInvariant();
                    string name = mo["Name"] as string ?? "";
                    if (!id.Contains("cpu")) continue;
                    double v = Convert.ToDouble(mo["Value"]);
                    if (v <= 0 || v > 125) continue;
                    if (name.Contains("Tctl") || name.Contains("Tdie") || name.Contains("Package")) best = Math.Max(best ?? 0, v);
                    any = Math.Max(any ?? 0, v);
                }
            }
            last = best ?? any;
            Source = last != null ? "LibreHardwareMonitor" : null;
            nextTry = now + Stopwatch.Frequency * (last != null ? 3 : 60);
        }
        catch
        {
            last = null;
            Source = null;
            nextTry = now + Stopwatch.Frequency * 60;
        }
        return last;
    }
}

/// <summary>Turns a raw command line into something a human recognises ("gpu-process", "Orbit/server.js").</summary>
static class CmdLine
{
    static readonly string[] ScriptExts = { ".js", ".mjs", ".cjs", ".ts", ".py", ".pyw", ".jar", ".ps1", ".bat", ".cmd", ".rb", ".lua" };

    public static List<string> Split(string cmd)
    {
        var args = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char c in cmd)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) { args.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) args.Add(sb.ToString());
        return args;
    }

    static bool IsScript(string a) =>
        ScriptExts.Any(e => a.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && !a.StartsWith("-");

    /// <summary>"@scope/pkg" for anything under node_modules, else "folder/file.ext".</summary>
    public static string ScriptLabel(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int nm = Array.FindLastIndex(parts, p => p.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
        if (nm >= 0 && nm + 1 < parts.Length)
        {
            var pkg = parts[nm + 1];
            if (pkg.StartsWith('@') && nm + 2 < parts.Length) pkg += "/" + parts[nm + 2];
            return pkg;
        }
        return parts.Length >= 2 ? parts[^2] + "/" + parts[^1] : parts.LastOrDefault() ?? path;
    }

    /// <summary>The script a runtime host (node/python/…) is running, if any.</summary>
    public static string? Script(string? cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return null;
        var args = Split(cmd).Skip(1).ToList();
        var s = args.FirstOrDefault(IsScript);
        if (s != null) return ScriptLabel(s);
        int m = args.IndexOf("-m");
        if (m >= 0 && m + 1 < args.Count) return "-m " + args[m + 1];
        return null;
    }

    public static string Summarize(string? cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return "";
        var args = Split(cmd).Skip(1).ToList();
        var type = args.FirstOrDefault(a => a.StartsWith("--type=", StringComparison.Ordinal));
        if (type != null)
        {
            var t = type[7..];
            var sub = args.FirstOrDefault(a => a.StartsWith("--utility-sub-type=", StringComparison.Ordinal));
            if (sub != null) t += ": " + sub[19..].Split('.').Last();
            return t;
        }
        var script = Script(cmd);
        if (script != null) return script;
        var interesting = args.Where(a => a.Length > 0).Take(3).Select(a => a.Contains('\\') ? Path.GetFileName(a.TrimEnd('\\')) : a);
        var s = string.Join(" ", interesting);
        return s.Length > 60 ? s[..60] + "…" : s;
    }
}
