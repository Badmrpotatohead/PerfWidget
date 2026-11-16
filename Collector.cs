using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PerfWidget;

sealed class ProcInfo
{
    public int Pid, ParentPid, Threads;
    public long CreateTime, CpuTime, PrivateWs, WorkingSet, Commit, IoBytes;
    public uint HardFaults;
    public string Name = "", LName = "";
    public double CpuPct, IoBps, HardFaultsPs, GpuPct;
    public long Vram;
    public ProcInfo? Parent;
}

sealed class AppGroup
{
    public string Key = "", Name = "";
    public readonly List<ProcInfo> Members = new();
    public double CpuPct, IoBps, HardFaultsPs, GpuPct;
    public long Ram, Commit, Vram;
}

sealed record DiskStat(string Name, double BusyPct, double Bps);
sealed record DriveStat(string Name, long Free, long Total, bool HasPagefile);
/// <summary>Severity: 2 = warning, 3 = critical. Resource: cpu, ram, commit, gpu, vram, disk, drive.</summary>
sealed record Issue(int Severity, string Resource, string Text);

sealed class Snapshot
{
    public int LogicalCpus, ProcessCount, ThreadCount;
    public double CpuPct;
    public long RamTotal, RamAvail, CommitTotal, CommitLimit, Cache;
    public long PagefileTotal, PagefileUsed;
    public double PagesInBps, PagesOutBps, HardFaultsPs;
    public double GpuPct;
    public long VramUsed, VramTotal;
    public string GpuName = "GPU";
    public List<DiskStat> Disks = new();
    public List<DriveStat> Drives = new();
    public List<AppGroup> Groups = new();
    public List<Issue> Issues = new();
    public string Bottleneck = "none";
    public double SelfCpuPct;
    public long RamUsed => RamTotal - RamAvail;
}

sealed class Collector : IDisposable
{
    IntPtr buf; int bufLen = 2 << 20;
    IntPtr pfBuf; int pfLen = 16 * 1024;
    Dictionary<(int, long), ProcInfo> prev = new();
    long prevStamp;
    long pIdle, pKernel, pUser;
    readonly Pdh pdh = new();
    readonly IntPtr cGpuEng, cGpuMem, cGpuAdapter, cDiskIdle, cDiskBytes, cPagesIn, cPagesOut;
    readonly int ncpu = Environment.ProcessorCount;
    readonly int selfPid = Environment.ProcessId;
    readonly long vramTotal;
    readonly string gpuName;
    readonly Dictionary<string, string> descCache = new();
    Dictionary<string, (double cpu, double gpu, double io, double hf)> ema = new();
    List<DriveStat> drives = new();
    long drivesStamp = long.MinValue;
    readonly Queue<double[]> hist = new();
    bool primed;

    public Collector()
    {
        buf = Marshal.AllocHGlobal(bufLen);
        pfBuf = Marshal.AllocHGlobal(pfLen);
        cGpuEng = pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        cGpuMem = pdh.Add(@"\GPU Process Memory(*)\Dedicated Usage");
        cGpuAdapter = pdh.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        cDiskIdle = pdh.Add(@"\PhysicalDisk(*)\% Idle Time");
        cDiskBytes = pdh.Add(@"\PhysicalDisk(*)\Disk Bytes/sec");
        cPagesIn = pdh.Add(@"\Memory\Pages Input/sec");
        cPagesOut = pdh.Add(@"\Memory\Pages Output/sec");
        (vramTotal, gpuName) = ReadGpuInfo();
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

        // processes + per-process deltas
        var procs = ReadProcesses();
        var cur = new Dictionary<(int, long), ProcInfo>(procs.Count);
        var byPid = new Dictionary<int, ProcInfo>(procs.Count);
        foreach (var p in procs)
        {
            var key = (p.Pid, p.CreateTime);
            if (prev.TryGetValue(key, out var o))
            {
                p.CpuPct = Math.Max(0, p.CpuTime - o.CpuTime) / (dt100ns * ncpu) * 100.0;
                p.IoBps = Math.Max(0, p.IoBytes - o.IoBytes) / dt;
                p.HardFaultsPs = Math.Max(0, (long)p.HardFaults - o.HardFaults) / dt;
            }
            cur[key] = p;
            byPid[p.Pid] = p;
        }
        prev = cur;
        foreach (var p in procs)
            if (p.Pid != 0 && byPid.TryGetValue(p.ParentPid, out var par) && par != p && par.CreateTime <= p.CreateTime)
                p.Parent = par;

        // PDH: GPU, disks, paging
        pdh.Collect();
        var gpuByPid = new Dictionary<int, double>();
        var engTotals = new Dictionary<string, double>();
        foreach (var (name, val) in pdh.GetArray(cGpuEng))
        {
            int pid = ParsePid(name);
            int li = name.IndexOf("_luid_", StringComparison.Ordinal);
            if (pid < 0 || li < 0) continue;
            string eng = name.Substring(li);
            gpuByPid[pid] = Math.Max(gpuByPid.GetValueOrDefault(pid), val);
            engTotals[eng] = engTotals.GetValueOrDefault(eng) + val;
        }
        double gpuTotal = engTotals.Count > 0 ? Math.Min(100, engTotals.Values.Max()) : 0;
        var vramByPid = new Dictionary<int, long>();
        foreach (var (name, val) in pdh.GetArray(cGpuMem))
        {
            int pid = ParsePid(name);
            if (pid >= 0) vramByPid[pid] = vramByPid.GetValueOrDefault(pid) + (long)val;
        }
        long vramUsed = 0;
        foreach (var (_, val) in pdh.GetArray(cGpuAdapter)) vramUsed = Math.Max(vramUsed, (long)val);

        var bytesByDisk = pdh.GetArray(cDiskBytes).ToDictionary(x => x.Name, x => x.Value);
        var disks = new List<DiskStat>();
        foreach (var (name, idle) in pdh.GetArray(cDiskIdle))
        {
            if (name == "_Total") continue;
            disks.Add(new DiskStat(DiskLabel(name), Math.Clamp(100 - idle, 0, 100), bytesByDisk.GetValueOrDefault(name)));
        }

        foreach (var p in procs)
        {
            p.GpuPct = Math.Min(100, gpuByPid.GetValueOrDefault(p.Pid));
            p.Vram = vramByPid.GetValueOrDefault(p.Pid);
        }

        // system totals
        Native.GetSystemTimes(out long idleT, out long kernT, out long userT);
        long dIdle = idleT - pIdle, dTotal = (kernT - pKernel) + (userT - pUser);
        pIdle = idleT; pKernel = kernT; pUser = userT;
        double cpu = dTotal > 0 ? Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100) : 0;

        Native.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>());
        long page = (long)pi.PageSize;
        var (pfTotal, pfUsed, pfDrives) = ReadPagefiles();

        if (Stopwatch.GetElapsedTime(drivesStamp).TotalSeconds >= 10 || drivesStamp == long.MinValue)
        {
            drives = ReadDrives(pfDrives);
            drivesStamp = Stopwatch.GetTimestamp();
        }

        var s = new Snapshot
        {
            LogicalCpus = ncpu,
            ProcessCount = (int)pi.ProcessCount,
            ThreadCount = (int)pi.ThreadCount,
            CpuPct = cpu,
            RamTotal = (long)pi.PhysicalTotal * page,
            RamAvail = (long)pi.PhysicalAvailable * page,
            CommitTotal = (long)pi.CommitTotal * page,
            CommitLimit = (long)pi.CommitLimit * page,
            Cache = (long)pi.SystemCache * page,
            PagefileTotal = pfTotal,
            PagefileUsed = pfUsed,
            PagesInBps = pdh.Get(cPagesIn) * 4096,
            PagesOutBps = pdh.Get(cPagesOut) * 4096,
            HardFaultsPs = procs.Sum(p => p.HardFaultsPs),
            GpuPct = gpuTotal,
            VramUsed = vramUsed,
            VramTotal = vramTotal,
            GpuName = gpuName,
            Disks = disks,
            Drives = drives,
            SelfCpuPct = byPid.TryGetValue(selfPid, out var me) ? me.CpuPct : 0,
        };
        s.Groups = Group(procs);
        BuildIssues(s);
        return s;
    }

    static int ParsePid(string inst)
    {
        // "pid_1234_luid_0x..._phys_0_eng_0_engtype_3D"
        if (!inst.StartsWith("pid_", StringComparison.Ordinal)) return -1;
        int end = inst.IndexOf('_', 4);
        return end > 4 && int.TryParse(inst.AsSpan(4, end - 4), out int pid) ? pid : -1;
    }

    static string DiskLabel(string inst)
    {
        // "0 C:" / "1 D: E:" / "2"
        var parts = inst.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "Disk " + inst;
    }

    List<ProcInfo> ReadProcesses()
    {
        uint st;
        int ret;
        while ((st = Native.NtQuerySystemInformation(Native.SystemProcessInformation, buf, bufLen, out ret)) == Native.STATUS_INFO_LENGTH_MISMATCH)
        {
            Marshal.FreeHGlobal(buf);
            bufLen = Math.Max(bufLen * 2, ret + 256 * 1024);
            buf = Marshal.AllocHGlobal(bufLen);
        }
        var list = new List<ProcInfo>(512);
        if (st != 0) return list;

        // SYSTEM_PROCESS_INFORMATION, x64 layout
        int off = 0;
        while (true)
        {
            IntPtr p = buf + off;
            int next = Marshal.ReadInt32(p, 0x00);
            ushort nameLen = (ushort)Marshal.ReadInt16(p, 0x38);
            IntPtr namePtr = Marshal.ReadIntPtr(p, 0x40);
            int pid = (int)Marshal.ReadIntPtr(p, 0x50);
            string name = nameLen > 0 && namePtr != IntPtr.Zero
                ? Marshal.PtrToStringUni(namePtr, nameLen / 2)
                : (pid == 0 ? "Idle" : "System");
            var pi = new ProcInfo
            {
                Threads = Marshal.ReadInt32(p, 0x04),
                PrivateWs = Marshal.ReadInt64(p, 0x08),
                HardFaults = (uint)Marshal.ReadInt32(p, 0x10),
                CreateTime = Marshal.ReadInt64(p, 0x20),
                CpuTime = Marshal.ReadInt64(p, 0x28) + Marshal.ReadInt64(p, 0x30),
                Pid = pid,
                ParentPid = (int)Marshal.ReadIntPtr(p, 0x58),
                WorkingSet = Marshal.ReadInt64(p, 0x90),
                Commit = Marshal.ReadInt64(p, 0xC8),
                IoBytes = Marshal.ReadInt64(p, 0xE8) + Marshal.ReadInt64(p, 0xF0),
                Name = name,
                LName = name.ToLowerInvariant(),
            };
            list.Add(pi);
            if (next == 0) break;
            off += next;
        }
        return list;
    }

    (long total, long used, HashSet<char> drives) ReadPagefiles()
    {
        var set = new HashSet<char>();
        uint st;
        int ret;
        while ((st = Native.NtQuerySystemInformation(Native.SystemPageFileInformation, pfBuf, pfLen, out ret)) == Native.STATUS_INFO_LENGTH_MISMATCH)
        {
            Marshal.FreeHGlobal(pfBuf);
            pfLen *= 2;
            pfBuf = Marshal.AllocHGlobal(pfLen);
        }
        if (st != 0 || ret < 32) return (0, 0, set);
        long total = 0, used = 0;
        int off = 0;
        while (true)
        {
            IntPtr p = pfBuf + off;
            int next = Marshal.ReadInt32(p, 0);
            total += (uint)Marshal.ReadInt32(p, 4) * 4096L;
            used += (uint)Marshal.ReadInt32(p, 8) * 4096L;
            ushort len = (ushort)Marshal.ReadInt16(p, 16);
            IntPtr np = Marshal.ReadIntPtr(p, 24);
            if (len > 0 && np != IntPtr.Zero)
            {
                string n = Marshal.PtrToStringUni(np, len / 2); // \??\C:\pagefile.sys
                int c = n.IndexOf(':');
                if (c > 0) set.Add(char.ToUpperInvariant(n[c - 1]));
            }
            if (next == 0) break;
            off += next;
        }
        return (total, used, set);
    }

    static List<DriveStat> ReadDrives(HashSet<char> pfDrives)
    {
        var list = new List<DriveStat>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string letter = d.Name.Substring(0, 2);
                var sb = new StringBuilder(1024);
                if (Native.QueryDosDevice(letter, sb, sb.Capacity) > 0 && sb.ToString().StartsWith(@"\??\", StringComparison.Ordinal))
                    continue; // subst alias of another drive's folder
                list.Add(new DriveStat(letter, d.AvailableFreeSpace, d.TotalSize, pfDrives.Contains(char.ToUpperInvariant(letter[0]))));
            }
            catch { }
        }
        return list;
    }

    static (long, string) ReadGpuInfo()
    {
        long best = 0;
        string name = "GPU";
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return (0, name);
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!int.TryParse(sub, out _)) continue;
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k == null) continue;
                    long mem = k.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => l,
                        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                        int i => (uint)i,
                        _ => k.GetValue("HardwareInformation.MemorySize") switch
                        {
                            int i2 => (uint)i2,
                            byte[] b2 when b2.Length >= 4 => BitConverter.ToUInt32(b2, 0),
                            _ => 0,
                        },
                    };
                    if (mem > best) { best = mem; name = k.GetValue("DriverDesc") as string ?? name; }
                }
                catch { }
            }
        }
        catch { }
        return (best, name);
    }

    // ------------------------------------------------------------------ grouping

    // Processes whose children start a new "app" (shells of the OS, launchers of everything).
    static readonly HashSet<string> Boundary = new()
    {
        "idle", "system", "registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "explorer.exe", "svchost.exe", "userinit.exe", "sihost.exe", "runtimebroker.exe",
        "windowsterminal.exe", "openconsole.exe", "conhost.exe", "taskhostw.exe",
    };

    // Interactive shells: a boundary only when the user opened them (parent is itself a boundary).
    static readonly HashSet<string> Shells = new()
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "wsl.exe", "nu.exe", "sh.exe", "zsh.exe",
    };

    static readonly Dictionary<string, string> Aliases = BuildAliases();

    static Dictionary<string, string> BuildAliases()
    {
        var d = new Dictionary<string, string>();
        void A(string name, params string[] exes) { foreach (var e in exes) d[e] = name; }
        A("Claude", "claude.exe");
        A("Blender", "blender.exe", "blender-launcher.exe");
        A("Unreal Engine", "unrealeditor.exe", "unrealeditor-cmd.exe", "ue4editor.exe", "ue4editor-cmd.exe",
          "shadercompileworker.exe", "unrealbuildtool.exe", "unrealtraceserver.exe", "crashreportclienteditor.exe",
          "livecodingconsole.exe", "zenserver.exe", "zendashboard.exe", "unrealinsights.exe");
        A("Epic Games Launcher", "epicgameslauncher.exe", "epicwebhelper.exe");
        A("Chrome", "chrome.exe"); A("Edge", "msedge.exe"); A("Firefox", "firefox.exe"); A("Brave", "brave.exe");
        A("Opera", "opera.exe"); A("Arc", "arc.exe");
        A("VS Code", "code.exe"); A("Cursor", "cursor.exe"); A("Windsurf", "windsurf.exe");
        A("Visual Studio", "devenv.exe"); A("Rider", "rider64.exe");
        A("Discord", "discord.exe"); A("Slack", "slack.exe"); A("Teams", "ms-teams.exe", "teams.exe");
        A("Spotify", "spotify.exe"); A("Zoom", "zoom.exe");
        A("Steam", "steam.exe", "steamwebhelper.exe"); A("OBS Studio", "obs64.exe");
        A("Photoshop", "photoshop.exe"); A("After Effects", "afterfx.exe"); A("Premiere Pro", "adobe premiere pro.exe");
        A("DaVinci Resolve", "resolve.exe"); A("Unity", "unity.exe"); A("Unity Hub", "unity hub.exe");
        A("Maya", "maya.exe"); A("3ds Max", "3dsmax.exe"); A("ZBrush", "zbrush.exe"); A("Houdini", "houdini.exe", "houdinifx.exe");
        A("Substance Painter", "adobe substance 3d painter.exe"); A("Godot", "godot.exe");
        A("Docker Desktop", "docker desktop.exe"); A("WSL / VM memory", "vmmem", "vmmemwsl", "vmmem.exe", "vmmemwsl.exe");
        A("Hyper-V VM", "vmwp.exe");
        A("Microsoft Defender", "msmpeng.exe", "mpdefendercoreservice.exe", "nissrv.exe");
        A("Windows Search indexer", "searchindexer.exe", "searchprotocolhost.exe", "searchfilterhost.exe");
        A("Desktop Window Manager", "dwm.exe");
        A("Memory compression", "memory compression");
        A("Windows core", "system", "registry", "secure system", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
          "services.exe", "lsass.exe", "lsaiso.exe", "fontdrvhost.exe");
        A("Windows services", "svchost.exe");
        A("Windows Explorer", "explorer.exe");
        A("Ollama", "ollama.exe", "ollama app.exe", "ollama_llama_server.exe"); A("LM Studio", "lm studio.exe");
        A("OneDrive", "onedrive.exe"); A("Dropbox", "dropbox.exe"); A("Google Drive", "googledrivefs.exe");
        A("NVIDIA services", "nvcontainer.exe", "nvdisplay.container.exe");
        A("PerfWidget (this monitor)", "perfwidget.exe");
        return d;
    }

    static readonly (string Prefix, string Name)[] PrefixAliases =
    {
        ("unrealeditor", "Unreal Engine"), ("ue5", "Unreal Engine"), ("godot", "Godot"), ("blender", "Blender"),
    };

    static string? AliasOf(ProcInfo p)
    {
        if (Aliases.TryGetValue(p.LName, out var a)) return a;
        foreach (var (prefix, name) in PrefixAliases)
            if (p.LName.StartsWith(prefix, StringComparison.Ordinal)) return name;
        return null;
    }

    static bool IsBoundary(ProcInfo p, int depth = 0)
    {
        if (Boundary.Contains(p.LName)) return true;
        if (Shells.Contains(p.LName) && depth < 8)
            return p.Parent == null || IsBoundary(p.Parent, depth + 1);
        return false;
    }

    /// <summary>A known app launched by something else (UE from the Epic launcher, Chrome from Claude) is its own app.</summary>
    static bool IsAnchor(ProcInfo p, ProcInfo parent)
    {
        var a = AliasOf(p);
        return a != null && a != AliasOf(parent);
    }

    static ProcInfo FindRoot(ProcInfo p, Dictionary<ProcInfo, ProcInfo> memo)
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
            var path = Native.ImagePath(p.Pid);
            if (path != null)
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                var desc = fvi.FileDescription?.Trim();
                var prod = fvi.ProductName?.Trim();
                if (!string.IsNullOrEmpty(desc) && desc.Length <= 40) d = desc;
                else if (!string.IsNullOrEmpty(prod) && prod.Length <= 40 && !prod.Contains("Operating System")) d = prod;
            }
        }
        catch { }
        descCache[p.LName] = d;
        return d;
    }

    // Runtimes that host many different apps: name the group after the app folder instead.
    static readonly HashSet<string> GenericHosts = new()
    {
        "electron.exe", "node.exe", "python.exe", "pythonw.exe", "java.exe", "javaw.exe", "dotnet.exe",
        "bun.exe", "deno.exe", "nw.exe",
    };
    Dictionary<(int, long), string?> hostAppCache = new();

    string? HostApp(ProcInfo p)
    {
        var key = (p.Pid, p.CreateTime);
        if (hostAppCache.TryGetValue(key, out var v)) return v;
        string? app = null;
        var path = Native.ImagePath(p.Pid);
        if (path != null)
        {
            var parts = path.Split('\\');
            int i = Array.FindIndex(parts, s => s.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                                                || s.Equals(".venv", StringComparison.OrdinalIgnoreCase)
                                                || s.Equals("venv", StringComparison.OrdinalIgnoreCase));
            if (i > 0) app = parts[i - 1];
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
            if (p.Pid == 0) continue; // Idle
            var root = FindRoot(p, memo);
            var alias = AliasOf(root);
            string key = alias ?? root.LName;
            string? hostApp = alias == null && GenericHosts.Contains(root.LName) ? HostApp(root) : null;
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
        }

        // light smoothing so the list doesn't jitter
        var next = new Dictionary<string, (double, double, double, double)>(groups.Count);
        foreach (var g in groups.Values)
        {
            g.GpuPct = Math.Min(100, g.GpuPct);
            if (ema.TryGetValue(g.Key, out var e))
            {
                g.CpuPct = 0.5 * e.cpu + 0.5 * g.CpuPct;
                g.GpuPct = 0.5 * e.gpu + 0.5 * g.GpuPct;
                g.IoBps = 0.5 * e.io + 0.5 * g.IoBps;
                g.HardFaultsPs = 0.5 * e.hf + 0.5 * g.HardFaultsPs;
            }
            next[g.Key] = (g.CpuPct, g.GpuPct, g.IoBps, g.HardFaultsPs);
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
        double[] m = { s.CpuPct, ramPct, commitPct, s.PagesInBps, s.GpuPct, busiest?.BusyPct ?? 0 };
        if (primed)
        {
            hist.Enqueue(m);
            while (hist.Count > 5) hist.Dequeue();
        }
        double Avg(int i) => hist.Count == 0 ? m[i] : hist.Average(h => h[i]);
        double cpuA = Avg(0), ramA = Avg(1), commitA = Avg(2), pagesInA = Avg(3), gpuA = Avg(4), diskA = Avg(5);

        string Top(Func<AppGroup, double> f, Func<AppGroup, string> fmt, int n = 2) =>
            string.Join(", ", s.Groups.Where(g => f(g) > 0).OrderByDescending(f).Take(n).Select(g => $"{g.Name} {fmt(g)}"));
        string TopRam() => Top(g => g.Ram, g => Fmt.Bytes(g.Ram));
        string TopCpu() => Top(g => g.CpuPct, g => Fmt.Pct(g.CpuPct));

        var L = s.Issues;

        // memory
        if (pagesInA > 20 * Fmt.MiB && ramA >= 85)
            L.Add(new(3, "ram", $"Out of RAM: swapping {Fmt.Rate(pagesInA)} from disk. {TopRam()}"));
        else if (ramA >= 92)
            L.Add(new(3, "ram", $"RAM {ramA:0}% full ({Fmt.GB(s.RamUsed)}/{Fmt.GB(s.RamTotal)} GB). {TopRam()}"));
        else if (ramA >= 85)
            L.Add(new(2, "ram", $"RAM getting tight, {ramA:0}% used. {TopRam()}"));

        if (commitA >= 95)
            L.Add(new(3, "commit", $"Commit {commitA:0}% of limit: apps may crash on allocation. {Top(g => g.Commit, g => Fmt.Bytes(g.Commit))}"));
        else if (commitA >= 85)
            L.Add(new(2, "commit", $"Commit {commitA:0}% of limit (RAM + page file). {Top(g => g.Commit, g => Fmt.Bytes(g.Commit))}"));

        // cpu
        if (cpuA >= 90) L.Add(new(3, "cpu", $"CPU maxed at {cpuA:0}%. {TopCpu()}"));
        else if (cpuA >= 75) L.Add(new(2, "cpu", $"CPU busy, {cpuA:0}%. {TopCpu()}"));

        // gpu
        if (gpuA >= 95)
            L.Add(new(2, "gpu", $"GPU saturated, {gpuA:0}%. {Top(g => g.GpuPct, g => $"{g.GpuPct:0}%")}"));
        if (s.VramTotal > 0 && s.VramUsed >= 0.92 * s.VramTotal)
            L.Add(new(2, "vram", $"VRAM nearly full ({Fmt.GB(s.VramUsed)}/{Fmt.GB(s.VramTotal)} GB); overflow spills into slower system RAM. {Top(g => g.Vram, g => Fmt.Bytes(g.Vram))}"));

        // disk activity
        if (busiest != null && diskA >= 90)
            L.Add(new(2, "disk", $"Disk {busiest.Name} {diskA:0}% busy ({Fmt.Rate(busiest.Bps)}). Most I/O: {Top(g => g.IoBps, g => Fmt.Rate(g.IoBps))}"));

        // disk space
        foreach (var d in s.Drives)
        {
            double pctFree = d.Total > 0 ? 100.0 * d.Free / d.Total : 100;
            if (d.HasPagefile && (d.Free < 10 * Fmt.GiB || pctFree < 5))
                L.Add(new(3, "drive", $"{d.Name} only {Fmt.Bytes(d.Free)} free: the page file on it can't grow."));
            else if (pctFree < 5)
                L.Add(new(3, "drive", $"{d.Name} almost full: {Fmt.Bytes(d.Free)} free ({pctFree:0}%)."));
            else if (pctFree < 10 && d.Free < 50 * Fmt.GiB)
                L.Add(new(2, "drive", $"{d.Name} low on space: {Fmt.Bytes(d.Free)} free ({pctFree:0}%)."));
        }

        // keep insertion order within the same severity (memory first: it's usually the root cause)
        var sorted = L.Select((x, i) => (x, i)).OrderByDescending(t => t.x.Severity).ThenBy(t => t.i).Select(t => t.x).ToList();
        L.Clear();
        L.AddRange(sorted);
        s.Bottleneck = L.FirstOrDefault(i => i.Resource != "drive")?.Resource ?? "none";
    }

    public void Dispose()
    {
        pdh.Dispose();
        Marshal.FreeHGlobal(buf);
        Marshal.FreeHGlobal(pfBuf);
    }
}

static class Fmt
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

static class Report
{
    public static string Text(Snapshot s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CPU {s.CpuPct:0.0}%  ({s.LogicalCpus} logical)   procs {s.ProcessCount}  threads {s.ThreadCount}");
        sb.AppendLine($"RAM used {Fmt.Bytes(s.RamUsed)} / {Fmt.Bytes(s.RamTotal)}  avail {Fmt.Bytes(s.RamAvail)}  cache {Fmt.Bytes(s.Cache)}");
        sb.AppendLine($"Commit {Fmt.Bytes(s.CommitTotal)} / {Fmt.Bytes(s.CommitLimit)}   pagefile {Fmt.Bytes(s.PagefileUsed)} / {Fmt.Bytes(s.PagefileTotal)}");
        sb.AppendLine($"Paging in {Fmt.Rate(s.PagesInBps)}  out {Fmt.Rate(s.PagesOutBps)}  hard faults {s.HardFaultsPs:0}/s");
        sb.AppendLine($"GPU {s.GpuName} {s.GpuPct:0.0}%  VRAM {Fmt.Bytes(s.VramUsed)} / {Fmt.Bytes(s.VramTotal)}");
        foreach (var d in s.Disks) sb.AppendLine($"Disk {d.Name}: busy {d.BusyPct:0}%  {Fmt.Rate(d.Bps)}");
        foreach (var d in s.Drives) sb.AppendLine($"Drive {d.Name}: free {Fmt.Bytes(d.Free)} of {Fmt.Bytes(d.Total)}{(d.HasPagefile ? " [pagefile]" : "")}");
        sb.AppendLine($"Bottleneck: {s.Bottleneck}");
        foreach (var i in s.Issues) sb.AppendLine($"  [{i.Severity}] {i.Text}");
        sb.AppendLine();
        sb.AppendLine($"{"APP",-34}{"N",4}{"CPU%",8}{"RAM",10}{"COMMIT",10}{"GPU%",7}{"VRAM",10}{"IO/s",10}");
        foreach (var g in s.Groups.OrderByDescending(g => g.Ram).Take(40))
        {
            string name = g.Name.Length > 33 ? g.Name[..33] : g.Name;
            sb.AppendLine($"{name,-34}{g.Members.Count,4}{g.CpuPct,8:0.0}{Fmt.Bytes(g.Ram),10}{Fmt.Bytes(g.Commit),10}{g.GpuPct,7:0}{Fmt.Bytes(g.Vram),10}{Fmt.Short(g.IoBps),10}");
            foreach (var p in g.Members.OrderByDescending(p => p.PrivateWs).Take(4))
                sb.AppendLine($"    {p.Name,-30}{p.Pid,8}{p.CpuPct,8:0.0}{Fmt.Bytes(p.PrivateWs),10}");
        }
        return sb.ToString();
    }
}
