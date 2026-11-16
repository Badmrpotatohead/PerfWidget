using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PerfWidget;

sealed record HistApp(string N, double Cpu, long Ram, double Gpu, double Io, double Thr);

sealed record HistLine(
    DateTime T,
    double Cpu, double CpuMax, double CpuMHz, double CpuMHzMin, double? CpuTemp,
    long RamUsed, long RamTotal, long Commit, long CommitLimit,
    double PageIn, double PageInMax,
    double Gpu, double GpuMax, long Vram, double? GpuTemp, double? GpuW,
    double Disk, string? DiskName,
    string[] Issues, HistApp[] Apps);

/// <summary>
/// Rolling on-disk history: one JSON line per 10 s window in %APPDATA%\PerfWidget\history\yyyy-MM-dd.jsonl,
/// kept for 7 days (~6-10 MB/day). Each line holds averages/peaks plus the apps that mattered in that window.
/// </summary>
sealed class HistoryLog
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfWidget", "history");
    const int WindowSec = 10, KeepDays = 7;

    static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly List<Snapshot> window = new();
    DateTime lastPrune = DateTime.MinValue;

    public void Add(Snapshot s)
    {
        window.Add(s);
        if ((s.Time - window[0].Time).TotalSeconds < WindowSec - 0.5) return;
        try { Write(Build(window)); } catch (Exception ex) { Log.Error(ex); }
        window.Clear();
    }

    static double R(double v, int d = 1) => Math.Round(v, d);

    static HistLine Build(List<Snapshot> w)
    {
        var last = w[^1];
        var loaded = w.Where(x => x.CpuPct >= 50).ToList();
        var busiest = w.SelectMany(x => x.Disks).OrderByDescending(d => d.BusyPct).FirstOrDefault();

        // per-app averages across the window
        var apps = new Dictionary<string, (string Name, double Cpu, long Ram, double Gpu, double Io, double Thr)>();
        foreach (var s in w)
            foreach (var g in s.Groups)
            {
                var a = apps.GetValueOrDefault(g.Key, (g.Name, 0, 0, 0, 0, 0));
                apps[g.Key] = (g.Name, a.Cpu + g.CpuPct / w.Count, g.Ram, a.Gpu + g.GpuPct / w.Count,
                               a.Io + g.IoBps / w.Count, Math.Max(a.Thr, g.MaxThreadPct));
            }
        var vals = apps.Values.ToList();
        var keep = vals.OrderByDescending(a => a.Cpu).Take(6)
            .Concat(vals.OrderByDescending(a => a.Ram).Take(6))
            .Concat(vals.OrderByDescending(a => a.Gpu).Take(3).Where(a => a.Gpu >= 1))
            .Concat(vals.OrderByDescending(a => a.Io).Take(3).Where(a => a.Io >= 1024 * 1024))
            .Concat(vals.OrderByDescending(a => a.Thr).Take(3).Where(a => a.Thr >= 50))
            .DistinctBy(a => a.Name)
            .Select(a => new HistApp(a.Name, R(a.Cpu), a.Ram, R(a.Gpu), R(a.Io, 0), R(a.Thr, 0)))
            .ToArray();

        return new HistLine(
            T: last.Time,
            Cpu: R(w.Average(x => x.CpuPct)), CpuMax: R(w.Max(x => x.CpuPct)),
            CpuMHz: R(w.Average(x => x.CpuMHz), 0),
            CpuMHzMin: R(loaded.Count > 0 ? loaded.Min(x => x.CpuMHz) : w.Min(x => x.CpuMHz), 0),
            CpuTemp: w.Any(x => x.CpuTemp != null) ? R(w.Max(x => x.CpuTemp ?? 0)) : null,
            RamUsed: last.RamUsed, RamTotal: last.RamTotal, Commit: last.CommitTotal, CommitLimit: last.CommitLimit,
            PageIn: R(w.Average(x => x.PagesInBps), 0), PageInMax: R(w.Max(x => x.PagesInBps), 0),
            Gpu: R(w.Average(x => x.GpuPct)), GpuMax: R(w.Max(x => x.GpuPct)), Vram: last.VramUsed,
            GpuTemp: w.Any(x => x.GpuTemp != null) ? R(w.Max(x => x.GpuTemp ?? 0)) : null,
            GpuW: w.Any(x => x.GpuPowerW != null) ? R(w.Max(x => x.GpuPowerW ?? 0)) : null,
            Disk: R(busiest?.BusyPct ?? 0), DiskName: busiest?.Name,
            Issues: w.SelectMany(x => x.Issues).Select(i => i.Text).Distinct().Take(6).ToArray(),
            Apps: keep);
    }

    void Write(HistLine line)
    {
        Directory.CreateDirectory(Dir);
        File.AppendAllText(Path.Combine(Dir, $"{line.T:yyyy-MM-dd}.jsonl"), JsonSerializer.Serialize(line, Json) + "\n");
        if ((DateTime.Now - lastPrune).TotalHours >= 6)
        {
            lastPrune = DateTime.Now;
            foreach (var f in Directory.GetFiles(Dir, "*.jsonl"))
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    && d < DateTime.Today.AddDays(-KeepDays))
                    try { File.Delete(f); } catch { }
        }
    }

    public static List<HistLine> Read(DateTime from, DateTime to)
    {
        var list = new List<HistLine>();
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var f = Path.Combine(Dir, $"{day:yyyy-MM-dd}.jsonl");
            if (!File.Exists(f)) continue;
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? l;
            while ((l = sr.ReadLine()) != null)
            {
                try
                {
                    var h = JsonSerializer.Deserialize<HistLine>(l, Json);
                    if (h != null && h.T >= from && h.T <= to) list.Add(h);
                }
                catch { }
            }
        }
        return list;
    }

    /// <summary>Human-readable digest of a time span: peaks with culprits, alerts, memory growth, CPU hogs.</summary>
    public static string Summary(DateTime from, DateTime to)
    {
        var lines = Read(from, to);
        var sb = new StringBuilder();
        sb.AppendLine($"PerfWidget history  {from:yyyy-MM-dd HH:mm} → {to:HH:mm}  ({lines.Count} samples of {WindowSec} s)");
        if (lines.Count == 0)
        {
            sb.AppendLine("No history recorded in this window (the widget must be running to record).");
            return sb.ToString();
        }
        sb.AppendLine();

        static string Who(HistLine l, Func<HistApp, double> f, Func<HistApp, string> fmt) =>
            string.Join(", ", l.Apps.Where(a => f(a) > 0).OrderByDescending(f).Take(3).Select(a => $"{a.N} {fmt(a)}"));
        void Peak(string label, Func<HistLine, double> f, Func<HistLine, string> val, Func<HistLine, string> who)
        {
            var p = lines.MaxBy(f)!;
            sb.AppendLine($"  {label,-12}{val(p),-22}at {p.T:HH:mm:ss}   {who(p)}");
        }

        sb.AppendLine("PEAKS");
        Peak("CPU", l => l.CpuMax, l => $"{l.CpuMax:0}% (avg {l.Cpu:0}%)", l => Who(l, a => a.Cpu, a => $"{a.Cpu:0.#}%"));
        Peak("RAM", l => l.RamUsed, l => $"{Fmt.GB(l.RamUsed)}/{Fmt.GB(l.RamTotal)} GB", l => Who(l, a => a.Ram, a => Fmt.Bytes(a.Ram)));
        Peak("Commit", l => l.Commit, l => $"{Fmt.GB(l.Commit)}/{Fmt.GB(l.CommitLimit)} GB", l => "");
        Peak("Paging in", l => l.PageInMax, l => Fmt.Rate(l.PageInMax), l => Who(l, a => a.Ram, a => Fmt.Bytes(a.Ram)));
        Peak("GPU", l => l.GpuMax, l => $"{l.GpuMax:0}%  VRAM {Fmt.GB(l.Vram)} GB", l => Who(l, a => a.Gpu, a => $"{a.Gpu:0}%"));
        if (lines.Any(l => l.GpuTemp != null)) Peak("GPU temp", l => l.GpuTemp ?? 0, l => $"{l.GpuTemp:0}°C  {l.GpuW:0} W", l => "");
        if (lines.Any(l => l.CpuTemp != null)) Peak("CPU temp", l => l.CpuTemp ?? 0, l => $"{l.CpuTemp:0}°C", l => "");
        else sb.AppendLine("  CPU temp    n/a (needs LibreHardwareMonitor running)");
        Peak("Disk", l => l.Disk, l => $"{l.DiskName} {l.Disk:0}% busy", l => Who(l, a => a.Io, a => Fmt.Rate(a.Io)));
        Peak("1 thread", l => l.Apps.Select(a => a.Thr).DefaultIfEmpty(0).Max(), l => $"{l.Apps.Max(a => a.Thr):0}% of a core", l => Who(l, a => a.Thr >= 50 ? a.Thr : 0, a => $"{a.Thr:0}%"));
        var loaded = lines.Where(l => l.Cpu >= 50).ToList();
        if (loaded.Count > 0)
        {
            var low = loaded.MinBy(l => l.CpuMHzMin)!;
            sb.AppendLine($"  {"Min clock",-12}{low.CpuMHzMin / 1000:0.00} GHz under load   at {low.T:HH:mm:ss}");
        }

        sb.AppendLine();
        sb.AppendLine("ALERTS");
        var alerts = lines.SelectMany(l => l.Issues.Select(i => (l.T, Text: i)))
            .GroupBy(x => Regex.Replace(x.Text, @"[\d.,+]+", "#"))
            .Select(g => (Count: g.Count(), First: g.Min(x => x.T), Last: g.Max(x => x.T), Text: g.Last().Text))
            .OrderBy(a => a.First).ToList();
        if (alerts.Count == 0) sb.AppendLine("  none");
        foreach (var a in alerts)
            sb.AppendLine($"  {a.First:HH:mm:ss}–{a.Last:HH:mm:ss}  ×{a.Count,-4} {a.Text}");

        sb.AppendLine();
        sb.AppendLine("MEMORY GROWTH (first seen → last seen in window)");
        var first = new Dictionary<string, (DateTime T, long Ram)>();
        var lastSeen = new Dictionary<string, (DateTime T, long Ram)>();
        foreach (var l in lines)
            foreach (var a in l.Apps)
            {
                first.TryAdd(a.N, (l.T, a.Ram));
                lastSeen[a.N] = (l.T, a.Ram);
            }
        var growth = first.Keys.Select(k => (Name: k, From: first[k], To: lastSeen[k], Delta: lastSeen[k].Ram - first[k].Ram))
            .Where(g => Math.Abs(g.Delta) >= 100 * Fmt.MiB).OrderByDescending(g => Math.Abs(g.Delta)).Take(6).ToList();
        if (growth.Count == 0) sb.AppendLine("  no app changed by more than 100 MB");
        foreach (var g in growth)
            sb.AppendLine($"  {g.Name,-30}{Fmt.Bytes(g.From.Ram),9} → {Fmt.Bytes(g.To.Ram),-9} ({(g.Delta >= 0 ? "+" : "-")}{Fmt.Bytes(Math.Abs(g.Delta))})");

        sb.AppendLine();
        sb.AppendLine("AVERAGE CPU BY APP (whole window)");
        foreach (var a in lines.SelectMany(l => l.Apps).GroupBy(a => a.N)
                     .Select(g => (Name: g.Key, Cpu: g.Sum(x => x.Cpu) / lines.Count, Thr: g.Max(x => x.Thr)))
                     .OrderByDescending(a => a.Cpu).Take(8))
            sb.AppendLine($"  {a.Name,-30}{a.Cpu,6:0.0}%   busiest thread peak {a.Thr:0}% of a core");

        return sb.ToString();
    }
}
