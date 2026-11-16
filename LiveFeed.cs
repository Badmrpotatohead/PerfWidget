using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PerfWidget;

/// <summary>
/// Machine-readable live snapshot for other tools (Orbit, scripts, Claude).
/// The running widget rewrites %APPDATA%\PerfWidget\live.json every 2 s (atomic replace).
/// Readers should treat the file as stale when "t" is older than ~10 s (widget closed).
/// Schema is versioned by "v"; fields are only ever added within a version.
/// </summary>
static class LiveFeed
{
    public const int SchemaVersion = 1;
    public const int IntervalSec = 2;
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfWidget", "live.json");

    static DateTime lastWrite = DateTime.MinValue;

    public static void MaybeWrite(Snapshot s, string? cpuTempSource)
    {
        if ((s.Time - lastWrite).TotalSeconds < IntervalSec - 0.1) return;
        lastWrite = s.Time;
        try
        {
            string path = FilePath, tmp = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(tmp, Json(s, cpuTempSource));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    static double R(double v, int digits = 1) => Math.Round(v, digits);

    public static byte[] Json(Snapshot s, string? cpuTempSource)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", SchemaVersion);
            w.WriteString("t", s.Time.ToUniversalTime().ToString("o"));
            w.WriteNumber("intervalSec", IntervalSec);
            w.WriteString("source", "PerfWidget");
            w.WriteNumber("pid", Environment.ProcessId);

            // verdict = worst issue, or all clear
            var top = s.Issues.FirstOrDefault();
            w.WriteStartObject("verdict");
            w.WriteNumber("severity", top?.Severity ?? 0);
            w.WriteString("title", top?.Title ?? "All clear");
            w.WriteString("detail", top?.Detail ?? "");
            w.WriteString("bottleneck", s.Bottleneck);
            w.WriteEndObject();

            w.WriteStartArray("issues");
            foreach (var i in s.Issues)
            {
                w.WriteStartObject();
                w.WriteNumber("severity", i.Severity);
                w.WriteString("resource", i.Resource);
                w.WriteString("title", i.Title);
                w.WriteString("detail", i.Detail);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartObject("cpu");
            w.WriteNumber("pct", R(s.CpuPct));
            w.WriteNumber("logical", s.LogicalCpus);
            w.WriteNumber("mhz", R(s.CpuMHz, 0));
            w.WriteNumber("baseMhz", R(s.CpuBaseMHz, 0));
            w.WriteNumber("perfLimitPct", R(s.CpuPerfLimit));
            if (s.CpuTemp is double ct) w.WriteNumber("tempC", R(ct)); else w.WriteNull("tempC");
            w.WriteString("tempSource", cpuTempSource);
            w.WriteNumber("processes", s.ProcessCount);
            w.WriteNumber("threads", s.ThreadCount);
            w.WriteEndObject();

            w.WriteStartObject("memory");
            w.WriteNumber("totalBytes", s.RamTotal);
            w.WriteNumber("usedBytes", s.RamUsed);
            w.WriteNumber("pct", s.RamTotal > 0 ? R(100.0 * s.RamUsed / s.RamTotal) : 0);
            w.WriteNumber("cacheBytes", s.Cache);
            w.WriteNumber("commitBytes", s.CommitTotal);
            w.WriteNumber("commitLimitBytes", s.CommitLimit);
            w.WriteNumber("pagefileTotalBytes", s.PagefileTotal);
            w.WriteNumber("pagefileUsedBytes", s.PagefileUsed);
            w.WriteNumber("pagingInBps", R(s.PagesInBps, 0));
            w.WriteNumber("pagingOutBps", R(s.PagesOutBps, 0));
            w.WriteEndObject();

            w.WriteStartObject("gpu");
            w.WriteString("name", s.GpuName);
            w.WriteNumber("pct", R(s.GpuPct));
            w.WriteNumber("vramUsedBytes", s.VramUsed);
            w.WriteNumber("vramTotalBytes", s.VramTotal);
            Opt(w, "tempC", s.GpuTemp);
            Opt(w, "slowdownTempC", s.GpuSlowdownTemp);
            Opt(w, "powerW", s.GpuPowerW);
            Opt(w, "powerLimitW", s.GpuPowerLimitW);
            Opt(w, "clockMhz", s.GpuClockMHz);
            Opt(w, "maxClockMhz", s.GpuMaxClockMHz);
            Opt(w, "fanPct", s.GpuFanPct);
            w.WriteString("throttle", GpuSensors.Describe(s.GpuThrottleReasons & GpuSensors.BadThrottle));
            w.WriteEndObject();

            w.WriteStartArray("disks");
            foreach (var d in s.Disks)
            {
                w.WriteStartObject();
                w.WriteString("name", d.Name);
                w.WriteNumber("busyPct", R(d.BusyPct));
                w.WriteNumber("bps", R(d.Bps, 0));
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("drives");
            foreach (var d in s.Drives)
            {
                w.WriteStartObject();
                w.WriteString("name", d.Name);
                w.WriteNumber("freeBytes", d.Free);
                w.WriteNumber("totalBytes", d.Total);
                w.WriteBoolean("hasPagefile", d.HasPagefile);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // apps that matter: top by RAM, CPU, GPU and I/O, merged, capped at 20
            var apps = s.Groups.OrderByDescending(g => g.Ram).Take(10)
                .Concat(s.Groups.OrderByDescending(g => g.CpuPct).Take(8))
                .Concat(s.Groups.OrderByDescending(g => g.GpuPct).Take(4))
                .Concat(s.Groups.OrderByDescending(g => g.IoBps).Take(4))
                .Distinct().OrderByDescending(g => g.Ram).Take(20);
            w.WriteStartArray("apps");
            foreach (var g in apps)
            {
                w.WriteStartObject();
                w.WriteString("name", g.Name);
                w.WriteNumber("processes", g.Members.Count);
                w.WriteNumber("cpuPct", R(g.CpuPct));
                w.WriteNumber("maxThreadPct", R(g.MaxThreadPct));
                w.WriteNumber("ramBytes", g.Ram);
                w.WriteNumber("commitBytes", g.Commit);
                w.WriteNumber("gpuPct", R(g.GpuPct));
                w.WriteNumber("vramBytes", g.Vram);
                w.WriteNumber("ioBps", R(g.IoBps, 0));
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    static void Opt(Utf8JsonWriter w, string name, double? v)
    {
        if (v is double d) w.WriteNumber(name, R(d)); else w.WriteNull(name);
    }
}
