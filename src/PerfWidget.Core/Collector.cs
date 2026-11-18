using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PerfWidget;

/// <summary>
/// Platform-neutral sampler: turns the platform's raw counters into rates, rolls processes up into apps,
/// smooths them, and decides what is wrong (Issues + Bottleneck). One instance per sampling thread.
/// </summary>
public sealed class Collector : IDisposable
{
    readonly IPlatform os;
    readonly GroupingRules rules;
    Dictionary<(int, long), ProcInfo> prev = new();
    long prevStamp;
    // per process lifetime: command line, image path, app-from-path
    Dictionary<(int, long), (string? Cmd, string? Path, string? App)> lifeCache = new();
    readonly Dictionary<string, int> pinnedFor = new();
    readonly Dictionary<string, List<(long Stamp, long Ram)>> ramHist = new();
    long lastRamHistStamp;
    readonly int ncpu = Environment.ProcessorCount;
    readonly int selfPid = Environment.ProcessId;
    readonly Dictionary<string, string> descCache = new();
    Dictionary<string, (double cpu, double gpu, double io, double hf, double thr)> ema = new();
    readonly Queue<double[]> hist = new();
    bool primed;

    public IPlatform Platform => os;
    public string? CpuTempSource => os.CpuTempSource;

    public Collector() : this(Platforms.Create()) { }

    public Collector(IPlatform platform)
    {
        os = platform;
        rules = platform.Rules;
        Sample(); // establishes the baseline for every delta
        primed = true;
    }

    // ------------------------------------------------------------------ sampling

    public Snapshot Sample()
    {
        long stamp = Stopwatch.GetTimestamp();
        double dt = prevStamp == 0 ? 1 : (stamp - prevStamp) / (double)Stopwatch.Frequency;
        if (dt <= 0) dt = 1;
        prevStamp = stamp;
        double dt100ns = dt * 1e7;

        var procs = os.ReadProcesses(dt);
        var cur = new Dictionary<(int, long), ProcInfo>(procs.Count);
        var byPid = new Dictionary<int, ProcInfo>(procs.Count);
        foreach (var p in procs)
        {
            var key = (p.Pid, p.CreateTime);
            if (prev.TryGetValue(key, out var o))
            {
                p.CpuPct = Math.Max(0, p.CpuTime - o.CpuTime) / (dt100ns * ncpu) * 100.0;
                p.IoBps = Math.Max(0, p.IoBytes - o.IoBytes) / dt;
                p.HardFaultsPs = Math.Max(0, p.HardFaults - o.HardFaults) / dt;
            }
            cur[key] = p;
            byPid[p.Pid] = p;
        }
        prev = cur;

        // per-lifetime facts: command line, path, bundle/app name
        var lifeNext = new Dictionary<(int, long), (string?, string?, string?)>(procs.Count);
        foreach (var p in procs)
        {
            var key = (p.Pid, p.CreateTime);
            if (!lifeCache.TryGetValue(key, out var life))
            {
                string? cmd = null, path = null, app = null;
                try { cmd = os.CommandLine(p); } catch { }
                if (rules.AppFromPath != null)
                {
                    try { path = os.ImagePath(p); } catch { }
                    if (path != null) app = rules.AppFromPath(path);
                }
                life = (cmd, path, app);
            }
            lifeNext[key] = life;
            p.CommandLine = life.Cmd;
            p.Summary = CmdLine.Summarize(life.Cmd);
        }
        lifeCache = lifeNext;

        // parents (a parent must be older than its child, or the pid was reused)
        foreach (var p in procs)
        {
            if (p.Pid == 0) continue;
            ProcInfo? par = null;
            if (byPid.TryGetValue(p.ParentPid, out var pp) && pp != p && pp.CreateTime <= p.CreateTime) par = pp;
            // macOS: XPC helpers are children of launchd; their "responsible" app is the real owner.
            // Only follow it to a real .app that isn't a launcher (Terminal…): a CLI tool orphaned to launchd
            // also has a responsible process, often a daemon, and must not be swallowed by it.
            if (p.ResponsiblePid > 0 && p.ResponsiblePid != p.Pid && (par == null || IsBoundary(par))
                && byPid.TryGetValue(p.ResponsiblePid, out var rp) && rp != p
                && AppFromPathOf(rp) != null && !IsBoundary(rp))
                par = rp;
            p.Parent = par;
        }

        // busiest thread, for platforms that only look at busy processes
        var busy = procs.Where(p => p.CpuPct * ncpu >= 50).ToList();
        if (busy.Count > 0) { try { os.FillThreads(busy, dt); } catch { } }

        var s = new Snapshot
        {
            Platform = os.Name,
            LogicalCpus = ncpu,
            ProcessCount = procs.Count,
            ThreadCount = procs.Sum(p => p.Threads),
            HardFaultsPs = procs.Sum(p => p.HardFaultsPs),
            SelfCpuPct = byPid.TryGetValue(selfPid, out var me) ? me.CpuPct : 0,
        };
        os.FillSystem(s, procs, dt);

        s.Groups = Group(procs);
        TrackRam(s.Groups);
        BuildIssues(s);
        return s;
    }

    /// <summary>Once a minute, remember each sizeable app's RAM so slow leaks can be spotted.</summary>
    void TrackRam(List<AppGroup> groups)
    {
        long now = Stopwatch.GetTimestamp();
        if (lastRamHistStamp != 0 && Stopwatch.GetElapsedTime(lastRamHistStamp).TotalSeconds < 60) return;
        lastRamHistStamp = now;
        var present = new HashSet<string>();
        foreach (var g in groups)
        {
            if (g.Ram < 200 * Fmt.MiB) continue;
            present.Add(g.Key);
            if (!ramHist.TryGetValue(g.Key, out var list)) ramHist[g.Key] = list = new();
            list.Add((now, g.Ram));
            list.RemoveAll(x => Stopwatch.GetElapsedTime(x.Stamp).TotalMinutes > 61);
        }
        foreach (var k in ramHist.Keys.Where(k => !present.Contains(k)).ToList()) ramHist.Remove(k);
    }

    // ------------------------------------------------------------------ grouping

    string? AppFromPathOf(ProcInfo p) =>
        lifeCache.TryGetValue((p.Pid, p.CreateTime), out var l) ? l.App : null;

    string? AliasOf(ProcInfo p)
    {
        if (rules.Aliases.TryGetValue(p.LName, out var a)) return a;
        foreach (var (prefix, name) in rules.PrefixAliases)
            if (p.LName.StartsWith(prefix, StringComparison.Ordinal)) return name;
        return AppFromPathOf(p);
    }

    bool IsBoundary(ProcInfo p, int depth = 0)
    {
        if (rules.Boundary.Contains(p.LName)) return true;
        if (rules.Shells.Contains(p.LName) && depth < 8)
            return p.Parent == null || IsBoundary(p.Parent, depth + 1);
        return false;
    }

    /// <summary>A known app launched by something else (UE from the Epic launcher, Chrome from Claude) is its own app.</summary>
    bool IsAnchor(ProcInfo p, ProcInfo parent)
    {
        var a = AliasOf(p);
        return a != null && a != AliasOf(parent);
    }

    ProcInfo FindRoot(ProcInfo p, Dictionary<ProcInfo, ProcInfo> memo)
    {
        var chain = new List<ProcInfo>();
        var cur = p;
        ProcInfo root;
        while (true)
        {
            if (memo.TryGetValue(cur, out var r)) { root = r; break; }
            chain.Add(cur);
            var par = cur.Parent;
            if (par == null || IsBoundary(par) || IsAnchor(cur, par) || chain.Count > 64) { root = cur; break; }
            cur = par;
        }
        foreach (var c in chain) memo[c] = root;
        return root;
    }

    string Describe(ProcInfo p)
    {
        if (descCache.TryGetValue(p.LName, out var d)) return d;
        d = Path.GetFileNameWithoutExtension(p.Name);
        if (string.IsNullOrEmpty(d)) d = p.Name;
        try
        {
            var path = lifeCache.TryGetValue((p.Pid, p.CreateTime), out var l) && l.Path != null ? l.Path : os.ImagePath(p);
            var desc = path != null ? os.DescribeImage(path) : null;
            if (!string.IsNullOrEmpty(desc)) d = desc;
        }
        catch { }
        if (descCache.Count > 5000) descCache.Clear();
        descCache[p.LName] = d;
        return d;
    }

    Dictionary<(int, long), string?> hostAppCache = new();

    string? HostApp(ProcInfo p)
    {
        var key = (p.Pid, p.CreateTime);
        if (hostAppCache.TryGetValue(key, out var v)) return v;
        string? app = null;
        string? path = null;
        try { path = os.ImagePath(p); } catch { }
        if (path != null)
        {
            var parts = path.Split('\\', '/');
            int i = Array.FindIndex(parts, s => s.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                                                || s.Equals(".venv", StringComparison.OrdinalIgnoreCase)
                                                || s.Equals("venv", StringComparison.OrdinalIgnoreCase));
            if (i > 0) app = parts[i - 1];
        }
        if (app == null)
        {
            // "node .../node_modules/@scope/pkg/cli.js" -> "@scope/pkg (node)", "python train.py" -> "train.py (python)"
            var script = CmdLine.Script(p.CommandLine);
            if (script != null) app = $"{script} ({Path.GetFileNameWithoutExtension(p.Name)})";
        }
        if (hostAppCache.Count > 2000) hostAppCache = new();
        hostAppCache[key] = app;
        return app;
    }

    List<AppGroup> Group(List<ProcInfo> procs)
    {
        var memo = new Dictionary<ProcInfo, ProcInfo>();
        var groups = new Dictionary<string, AppGroup>();
        foreach (var p in procs)
        {
            if (p.Pid == 0 && os.Name == "windows") continue; // Idle
            var root = FindRoot(p, memo);
            var alias = AliasOf(root);
            string key = alias ?? root.LName;
            string? hostApp = alias == null && rules.GenericHosts.Contains(root.LName) ? HostApp(root) : null;
            if (hostApp != null) key += "|" + hostApp.ToLowerInvariant();
            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = new AppGroup { Key = key, Name = alias ?? hostApp ?? Describe(root) };
            g.Members.Add(p);
            g.Ram += p.PrivateWs;
            g.Commit += p.Commit;
            g.CpuPct += p.CpuPct;
            g.IoBps += p.IoBps;
            g.HardFaultsPs += p.HardFaultsPs;
            g.Vram += p.Vram;
            g.GpuPct += p.GpuPct;
            if (p.MaxThreadPct > g.MaxThreadPct)
            {
                g.MaxThreadPct = p.MaxThreadPct;
                g.MaxThreadProc = p.Summary.Length > 0 ? $"{p.Name} {p.Summary}" : p.Name;
            }
        }

        // light smoothing so the list doesn't jitter
        var next = new Dictionary<string, (double, double, double, double, double)>(groups.Count);
        foreach (var g in groups.Values)
        {
            g.GpuPct = Math.Min(100, g.GpuPct);
            if (ema.TryGetValue(g.Key, out var e))
            {
                g.CpuPct = 0.5 * e.cpu + 0.5 * g.CpuPct;
                g.GpuPct = 0.5 * e.gpu + 0.5 * g.GpuPct;
                g.IoBps = 0.5 * e.io + 0.5 * g.IoBps;
                g.HardFaultsPs = 0.5 * e.hf + 0.5 * g.HardFaultsPs;
                g.MaxThreadPct = 0.5 * e.thr + 0.5 * g.MaxThreadPct;
            }
            next[g.Key] = (g.CpuPct, g.GpuPct, g.IoBps, g.HardFaultsPs, g.MaxThreadPct);
        }
        ema = next;
        return groups.Values.ToList();
    }

    // ------------------------------------------------------------------ diagnosis

    void BuildIssues(Snapshot s)
    {
        double ramPct = s.RamTotal > 0 ? 100.0 * s.RamUsed / s.RamTotal : 0;
        double commitPct = s.CommitLimit > 0 ? 100.0 * s.CommitTotal / s.CommitLimit : 0;
        var busiest = s.Disks.OrderByDescending(d => d.BusyPct).FirstOrDefault();
        double[] m =
        {
            s.CpuPct, ramPct, commitPct, s.PagesInBps, s.GpuPct, busiest?.BusyPct ?? 0, s.CpuMHz, s.CpuPerfLimit,
            s.MemoryPressure ?? 0, s.ThermalLevel ?? 0,
        };
        if (primed)
        {
            hist.Enqueue(m);
            while (hist.Count > 5) hist.Dequeue();
        }
        double Avg(int i) => hist.Count == 0 ? m[i] : hist.Average(h => h[i]);
        double cpuA = Avg(0), ramA = Avg(1), commitA = Avg(2), pagesInA = Avg(3), gpuA = Avg(4), diskA = Avg(5),
               mhzA = Avg(6), limitA = Avg(7), pressureA = Avg(8), thermalA = Avg(9);

        string Top(Func<AppGroup, double> f, Func<AppGroup, string> fmt, int n = 2) =>
            string.Join(", ", s.Groups.Where(g => f(g) > 0).OrderByDescending(f).Take(n).Select(g => $"{g.Name} {fmt(g)}"));
        string TopRam() => Top(g => g.Ram, g => Fmt.Bytes(g.Ram));
        string TopCpu() => Top(g => g.CpuPct, g => Fmt.Pct(g.CpuPct));
        string swapName = s.Platform == "windows" ? "page file" : "swap";

        var L = s.Issues;
        void Add(int sev, string res, string title, string detail) => L.Add(new Issue(sev, res, title, detail));

        // memory
        bool pressureOnly = s.RamAlertsFromPressureOnly && s.MemoryPressure != null;
        if (pagesInA > 20 * Fmt.MiB && (ramA >= 85 || pressureA >= 1))
            Add(3, "ram", "Out of RAM, swapping to disk", $"{Fmt.Rate(pagesInA)} from {swapName} · {TopRam()}");
        else if (pressureA >= 1.5)
            Add(3, "ram", "Out of RAM", $"memory pressure critical{(s.MemStallPct is double ms ? $", apps stalled {ms:0}% of the time" : "")} · {TopRam()}");
        else if (!pressureOnly && ramA >= 92)
            Add(3, "ram", "RAM almost full", $"{Fmt.GB(s.RamUsed)} of {Fmt.GB(s.RamTotal)} GB · {TopRam()}");
        else if (pressureA >= 0.6)
            Add(2, "ram", "RAM getting tight", $"memory pressure elevated · {TopRam()}");
        else if (!pressureOnly && ramA >= 85)
            Add(2, "ram", "RAM getting tight", $"{ramA:0}% used · {TopRam()}");

        if (commitA >= 95)
            Add(3, "commit", "Virtual memory nearly exhausted", $"{commitA:0}% of RAM + page file · apps may crash · {Top(g => g.Commit, g => Fmt.Bytes(g.Commit))}");
        else if (commitA >= 85)
            Add(2, "commit", "Virtual memory high", $"{commitA:0}% of RAM + page file · {Top(g => g.Commit, g => Fmt.Bytes(g.Commit))}");

        // memory creeping up for an hour (leak or runaway cache)
        foreach (var g in s.Groups)
        {
            if (!ramHist.TryGetValue(g.Key, out var h) || h.Count < 2) continue;
            var oldest = h[0];
            double mins = Stopwatch.GetElapsedTime(oldest.Stamp).TotalMinutes;
            long growth = g.Ram - oldest.Ram;
            long peak = Math.Max(g.Ram, h.Max(x => x.Ram));
            if (mins >= 30 && growth >= 1.5 * Fmt.GiB && growth >= 0.25 * g.Ram && g.Ram >= 0.95 * peak)
                Add(2, "ram", $"{g.Name} keeps growing", $"+{Fmt.Bytes(growth)} in {mins:0} min, now {Fmt.Bytes(g.Ram)}");
        }

        // cpu
        if (cpuA >= 90) Add(3, "cpu", "CPU maxed", $"{cpuA:0}% · {TopCpu()}");
        else if (cpuA >= 75) Add(2, "cpu", "CPU busy", $"{cpuA:0}% · {TopCpu()}");

        // one thread pinned: the app is bottlenecked even though total CPU looks low
        var stillPinned = new HashSet<string>();
        foreach (var g in s.Groups.Where(g => g.MaxThreadPct >= 90))
        {
            stillPinned.Add(g.Key);
            pinnedFor[g.Key] = pinnedFor.GetValueOrDefault(g.Key) + 1;
        }
        foreach (var k in pinnedFor.Keys.Where(k => !stillPinned.Contains(k)).ToList()) pinnedFor.Remove(k);
        if (cpuA < 90)
            foreach (var g in s.Groups.Where(g => pinnedFor.GetValueOrDefault(g.Key) >= 5).OrderByDescending(g => g.MaxThreadPct).Take(2))
                Add(2, "cpu", $"{g.Name} is maxing one core", $"1 thread at {g.MaxThreadPct:0}% · it can't use your other {ncpu - 1} threads");

        // cpu clocks / heat
        if (s.CpuTemp is double ct && ct >= 89)
            Add(ct >= 95 ? 3 : 2, "cpu", $"CPU at {ct:0}°C", "at its thermal limit, clocks will drop");
        if (thermalA >= 2.5)
            Add(3, "cpu", "Overheating", "the system reports a critical thermal state · it is slowing everything down hard");
        else if (thermalA >= 1.5)
            Add(2, "cpu", "CPU throttling", "the system is slowing the chip down to cool it");
        if (cpuA >= 50 && s.CpuBaseMHz > 0)
        {
            if (limitA < 95)
                Add(2, "cpu", "CPU throttling", $"clocks capped at {limitA:0}% by heat or power limit · {mhzA / 1000:0.00} GHz");
            else if (mhzA > 0 && mhzA < 0.95 * s.CpuBaseMHz)
                Add(2, "cpu", "CPU throttling", $"{mhzA / 1000:0.00} GHz under load, below its {s.CpuBaseMHz / 1000:0.00} GHz base");
        }

        // gpu
        if (gpuA >= 95)
            Add(2, "gpu", "GPU maxed", $"{gpuA:0}%" + (s.Groups.Any(g => g.GpuPct > 0) ? $" · {Top(g => g.GpuPct, g => $"{g.GpuPct:0}%")}" : ""));
        if ((s.GpuThrottleReasons & GpuSensors.BadThrottle) != 0 && gpuA >= 30)
            Add(2, "gpu", "GPU throttling", (GpuSensors.Describe(s.GpuThrottleReasons & GpuSensors.BadThrottle) ?? "") +
                                             (s.GpuTemp is double gt0 ? $" at {gt0:0}°C" : ""));
        else if (s.GpuTemp is double gt && (gt >= 87 || (s.GpuSlowdownTemp is double sd && gt >= sd - 3)))
            Add(2, "gpu", $"GPU hot, {gt:0}°C", s.GpuSlowdownTemp is double sd2 ? $"slows down at {sd2:0}°C" : "");
        if (s.VramTotal > 0 && s.VramUsed >= 0.92 * s.VramTotal)
            Add(2, "vram", "Video memory full", $"{Fmt.GB(s.VramUsed)} of {Fmt.GB(s.VramTotal)} GB, spilling into system RAM · {Top(g => g.Vram, g => Fmt.Bytes(g.Vram))}");

        // disk activity
        if (busiest != null && diskA >= 90)
            Add(2, "disk", $"Disk {busiest.Name} maxed", $"{diskA:0}% busy, {Fmt.Rate(busiest.Bps)} · most I/O: {Top(g => g.IoBps, g => Fmt.Rate(g.IoBps))}");

        // disk space
        foreach (var d in s.Drives)
        {
            double pctFree = d.Total > 0 ? 100.0 * d.Free / d.Total : 100;
            if (d.HasPagefile && (d.Free < 10 * Fmt.GiB || pctFree < 5))
                Add(3, "drive", $"{d.Name} nearly full", $"{Fmt.Bytes(d.Free)} free · the {swapName} can't grow");
            else if (pctFree < 5)
                Add(3, "drive", $"{d.Name} nearly full", $"{Fmt.Bytes(d.Free)} free ({pctFree:0}%)");
            else if (pctFree < 10 && d.Free < 50 * Fmt.GiB)
                Add(2, "drive", $"{d.Name} low on space", $"{Fmt.Bytes(d.Free)} free ({pctFree:0}%)");
        }

        // keep insertion order within the same severity (memory first: it's usually the root cause)
        var sorted = L.Select((x, i) => (x, i)).OrderByDescending(t => t.x.Severity).ThenBy(t => t.i).Select(t => t.x).ToList();
        L.Clear();
        L.AddRange(sorted);
        s.Bottleneck = L.FirstOrDefault(i => i.Resource != "drive")?.Resource ?? "none";
    }

    public void Dispose() => os.Dispose();
}
