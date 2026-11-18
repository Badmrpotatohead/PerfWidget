using System.Linq;
using System.Text;

namespace PerfWidget;

public static class Fmt
{
    public const double GiB = 1073741824.0, MiB = 1048576.0, KiB = 1024.0, TiB = GiB * 1024;

    public static string Bytes(double b) =>
        b >= TiB ? $"{b / TiB:0.0} TB" :
        b >= 100 * GiB ? $"{b / GiB:0} GB" :
        b >= GiB ? $"{b / GiB:0.0} GB" :
        b >= MiB ? $"{b / MiB:0} MB" :
        $"{b / KiB:0} KB";

    public static string GB(double b) => $"{b / GiB:0.0}";
    public static string Rate(double bps) => bps < KiB ? "0 KB/s" : Bytes(bps) + "/s";
    public static string Short(double bps) =>
        bps < 10 * KiB ? "–" : bps >= GiB ? $"{bps / GiB:0.0}G" : bps >= MiB ? $"{bps / MiB:0.#}M" : $"{bps / KiB:0}K";
    public static string Pct(double p) => p < 0.05 ? "–" : p < 10 ? $"{p:0.0}%" : $"{p:0}%";
}

public static class Report
{
    public static string Text(Snapshot s, string? cpuTempHint = null)
    {
        var sb = new StringBuilder();
        string swap = s.Platform == "windows" ? "pagefile" : "swap";
        sb.AppendLine($"Time {s.Time:yyyy-MM-dd HH:mm:ss}   platform {s.Platform}");
        sb.AppendLine($"CPU {s.CpuPct:0.0}%  ({s.LogicalCpus} logical)   procs {s.ProcessCount}  threads {s.ThreadCount}");
        sb.AppendLine($"CPU clock {(s.CpuMHz > 0 ? $"{s.CpuMHz / 1000:0.00} GHz" : "n/a")} (base {(s.CpuBaseMHz > 0 ? $"{s.CpuBaseMHz / 1000:0.00}" : "n/a")})  firmware limit {s.CpuPerfLimit:0}%  thermal level {s.ThermalLevel?.ToString() ?? "n/a"}  temp {(s.CpuTemp is double ct ? $"{ct:0}°C" : "n/a" + (cpuTempHint != null ? $" ({cpuTempHint})" : ""))}");
        sb.AppendLine($"GPU temp {s.GpuTemp?.ToString("0") ?? "n/a"}°C (slowdown {s.GpuSlowdownTemp?.ToString("0") ?? "?"})  power {s.GpuPowerW?.ToString("0") ?? "?"}/{s.GpuPowerLimitW?.ToString("0") ?? "?"} W  clock {s.GpuClockMHz?.ToString("0") ?? "?"}/{s.GpuMaxClockMHz?.ToString("0") ?? "?"} MHz  fan {s.GpuFanPct?.ToString("0") ?? "?"}%  limits: {GpuSensors.Describe(s.GpuThrottleReasons) ?? "none"}");
        sb.AppendLine($"RAM used {Fmt.Bytes(s.RamUsed)} / {Fmt.Bytes(s.RamTotal)}  avail {Fmt.Bytes(s.RamAvail)}  cache {Fmt.Bytes(s.Cache)}  compressed {Fmt.Bytes(s.Compressed)}  pressure {s.MemoryPressure?.ToString() ?? "n/a"}");
        sb.AppendLine($"Commit {Fmt.Bytes(s.CommitTotal)} / {(s.CommitLimit > 0 ? Fmt.Bytes(s.CommitLimit) : "no limit")}   {swap} {Fmt.Bytes(s.PagefileUsed)} / {Fmt.Bytes(s.PagefileTotal)}");
        sb.AppendLine($"Paging in {Fmt.Rate(s.PagesInBps)}  out {Fmt.Rate(s.PagesOutBps)}  hard faults {s.HardFaultsPs:0}/s");
        if (s.CpuStallPct != null || s.MemStallPct != null || s.IoStallPct != null)
            sb.AppendLine($"Stalls (PSI some avg10): cpu {s.CpuStallPct:0.0}%  memory {s.MemStallPct:0.0}%  io {s.IoStallPct:0.0}%");
        sb.AppendLine($"GPU {s.GpuName} {s.GpuPct:0.0}%  VRAM {Fmt.Bytes(s.VramUsed)}{(s.VramTotal > 0 ? $" / {Fmt.Bytes(s.VramTotal)}" : " (shared with RAM)")}");
        foreach (var d in s.Disks) sb.AppendLine($"Disk {d.Name}: busy {d.BusyPct:0}%  {Fmt.Rate(d.Bps)}");
        foreach (var d in s.Drives) sb.AppendLine($"Drive {d.Name}: free {Fmt.Bytes(d.Free)} of {Fmt.Bytes(d.Total)}{(d.HasPagefile ? $" [{swap}]" : "")}");
        sb.AppendLine($"Bottleneck: {s.Bottleneck}");
        foreach (var i in s.Issues) sb.AppendLine($"  [{i.Severity}] {i.Text}");
        sb.AppendLine();
        sb.AppendLine($"{"APP",-34}{"N",4}{"CPU%",8}{"THR%",6}{"RAM",10}{"COMMIT",10}{"GPU%",7}{"VRAM",10}{"IO/s",10}");
        foreach (var g in s.Groups.OrderByDescending(g => g.Ram).Take(40))
        {
            string name = g.Name.Length > 33 ? g.Name[..33] : g.Name;
            sb.AppendLine($"{name,-34}{g.Members.Count,4}{g.CpuPct,8:0.0}{g.MaxThreadPct,6:0}{Fmt.Bytes(g.Ram),10}{Fmt.Bytes(g.Commit),10}{g.GpuPct,7:0}{Fmt.Bytes(g.Vram),10}{Fmt.Short(g.IoBps),10}");
            foreach (var p in g.Members.OrderByDescending(p => p.PrivateWs).Take(6))
                sb.AppendLine($"    {p.Name,-30}{p.Pid,8}{p.CpuPct,8:0.0}{p.MaxThreadPct,6:0}{Fmt.Bytes(p.PrivateWs),10}   {p.Summary}");
        }
        sb.AppendLine("(THR% = busiest single thread as % of one core)");
        return sb.ToString();
    }
}
