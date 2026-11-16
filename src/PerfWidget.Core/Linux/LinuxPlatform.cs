using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PerfWidget;

/// <summary>
/// Linux sampler: /proc for processes, CPU, memory, PSI and disks; /sys for clocks, temperatures and AMD GPUs;
/// NVML for NVIDIA. Reads only what is cheap at 1 Hz, re-reads slow-changing facts on a timer, never throws.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxPlatform : IPlatform
{
    readonly string proc, sys;
    readonly FileReader fr = new();
    readonly double clkTck;      // USER_HZ, ticks per second
    readonly double tick100ns;   // 100 ns units per tick
    readonly long pageSize = Environment.SystemPageSize;
    readonly int ncpu = Environment.ProcessorCount;
    static readonly long Freq = Stopwatch.Frequency;

    /// <summary>Idle processes (no CPU time since last tick) get statm / io re-read this often.</summary>
    static readonly long IdleRefresh = 5 * Freq;
    /// <summary>VmSwap (needs the much bigger /proc/[pid]/status) is re-read this often, only while swap is in use.</summary>
    static readonly long SwapRefresh = 10 * Freq;
    const int ThreadBudget = 1024, ThreadsPerProcess = 256;

    readonly Dictionary<int, Life> lives = new();
    readonly List<int> deadPids = new();
    int gen;
    bool swapInUse;
    Dictionary<(int Tid, long Start), (long Ticks, long Stamp)> thrPrev = new(), thrCur = new();

    public string Name => "linux";
    public GroupingRules Rules { get; } = LinuxRules.Build();
    public string? CpuTempSource => tempSource;
    public string CpuTempHint => "no CPU temperature sensor found (try `sudo modprobe k10temp` or coretemp)";

    public LinuxPlatform() : this("/proc", "/sys") { }

    /// <summary>Roots are injectable so the plumbing can be exercised against a fake tree.</summary>
    internal LinuxPlatform(string procRoot, string sysRoot)
    {
        proc = procRoot;
        sys = sysRoot;
        clkTck = ClockTicks();
        tick100ns = 1e7 / clkTck;
        try { gpu = new GpuSensors(); } catch { gpu = null; }
        try { Discover(); } catch { }
        lastDiscover = Stopwatch.GetTimestamp();
        try
        {
            if (fr.TryRead(proc + "/meminfo", out var m))
            {
                var mi = LinuxParse.ParseMeminfo(m);
                swapInUse = mi.SwapTotal > 0 && mi.SwapFree >= 0 && mi.SwapTotal > mi.SwapFree;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ per-pid state

    /// <summary>What we remember about a pid: its file paths, and facts about its current lifetime (pid + starttime).</summary>
    sealed class Life
    {
        public readonly string Dir, StatPath, StatmPath, IoPath, StatusPath;
        public long StartTime = long.MinValue;
        public int Gen;
        public byte[] Comm = Array.Empty<byte>();
        public string Name = "", LName = "";
        public bool Kernel;
        public List<string>? Argv;
        public bool ArgvRead;
        public string? Exe;
        public bool ExeRead;
        public long CpuTicks = -1;
        public long MemStamp, IoStamp, SwapStamp;
        public long Resident, Shared, Io, SwapBytes;
        public bool IoDenied;

        public Life(string proc, int pid)
        {
            Dir = proc + "/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            StatPath = Dir + "/stat";
            StatmPath = Dir + "/statm";
            IoPath = Dir + "/io";
            StatusPath = Dir + "/status";
        }

        public void NewLifetime(long start)
        {
            StartTime = start;
            Comm = Array.Empty<byte>();
            Name = LName = "";
            Kernel = false;
            ForgetExec();
            CpuTicks = -1;
            MemStamp = IoStamp = 0;
            SwapStamp = 0; // VmSwap is read on first sight, then every 10 s, staggered by pid
            Resident = Shared = Io = SwapBytes = 0;
            IoDenied = false;
        }

        /// <summary>exec() keeps pid and starttime but changes argv and executable.</summary>
        public void ForgetExec() { Argv = null; ArgvRead = false; Exe = null; ExeRead = false; }
    }

    // ------------------------------------------------------------------ processes

    public List<ProcInfo> ReadProcesses(double dtSec)
    {
        var list = new List<ProcInfo>(lives.Count + 64);
        try
        {
            gen++;
            long now = Stopwatch.GetTimestamp();
            foreach (int pid in NumericEntries(proc))
            {
                try
                {
                    var p = ReadProcess(pid, now);
                    if (p != null) list.Add(p);
                }
                catch { } // the process vanished mid-read
            }
            deadPids.Clear();
            foreach (var kv in lives) if (kv.Value.Gen != gen) deadPids.Add(kv.Key);
            foreach (int pid in deadPids) lives.Remove(pid);
        }
        catch { }
        return list;
    }

    ProcInfo? ReadProcess(int pid, long now)
    {
        bool known = lives.TryGetValue(pid, out var L);
        L ??= new Life(proc, pid);
        if (!fr.TryRead(L.StatPath, out var data, whole: false)) return null;
        if (!LinuxParse.ParseStat(data, out var st)) return null;
        if (st.State == 'X' || (st.State == 'Z' && st.Threads <= 1)) return null; // dead / zombie: no resources
        if (!known) lives[pid] = L;
        L.Gen = gen;

        bool fresh = L.StartTime != st.StartTime;
        if (fresh) L.NewLifetime(st.StartTime);
        var comm = data.Slice(st.CommStart, st.CommLength);
        if (fresh || !comm.SequenceEqual(L.Comm))
        {
            if (!fresh) L.ForgetExec(); // renamed or exec'd
            L.Comm = comm.ToArray();
            L.Kernel = (st.Flags & LinuxParse.PF_KTHREAD) != 0;
            NameProcess(L, pid); // reads cmdline: `data` is no longer valid after this
        }

        long ticks = st.UTime + st.STime;
        bool active = ticks != L.CpuTicks;
        L.CpuTicks = ticks;

        if (!L.Kernel)
        {
            if (active || now - L.MemStamp >= IdleRefresh)
            {
                if (fr.TryRead(L.StatmPath, out var sm, whole: false) && LinuxParse.ParseStatm(sm, out long res, out long sh))
                {
                    L.Resident = res;
                    L.Shared = sh;
                }
                L.MemStamp = now;
            }
            if (!L.IoDenied && (active || now - L.IoStamp >= IdleRefresh))
            {
                // readable only for our own processes (or as root); remember a refusal for the whole lifetime
                if (fr.TryRead(L.IoPath, out var io, whole: false) && LinuxParse.ParseIo(io, out long rb, out long wb))
                    L.Io = rb + wb;
                else
                    L.IoDenied = true;
                L.IoStamp = now;
            }
            if (!swapInUse) L.SwapBytes = 0;
            else if (now - L.SwapStamp >= SwapRefresh)
            {
                if (fr.TryRead(L.StatusPath, out var status))
                {
                    long swap = LinuxParse.ParseVmSwap(status);
                    L.SwapBytes = swap > 0 ? swap : 0;
                }
                // after the first read, shift each pid's 10 s cycle by pid % 10 seconds so reads spread out
                L.SwapStamp = L.SwapStamp == 0 ? now - (pid % 10) * Freq : now;
            }
        }

        long priv = Math.Max(0, L.Resident - L.Shared) * pageSize;
        return new ProcInfo
        {
            Pid = pid,
            ParentPid = st.Ppid,
            Threads = Math.Max(1, st.Threads),
            CreateTime = st.StartTime,
            CpuTime = (long)(ticks * tick100ns),
            PrivateWs = priv,
            WorkingSet = Math.Max(0, L.Resident) * pageSize,
            Commit = priv + L.SwapBytes,
            IoBytes = L.Io,
            HardFaults = st.MajFlt,
            Name = L.Name,
            LName = L.LName,
        };
    }

    void NameProcess(Life L, int pid)
    {
        string comm = Encoding.UTF8.GetString(L.Comm);
        string name = comm;
        if (!L.Kernel && L.Comm.Length == LinuxParse.CommMax)
        {
            var argv = Argv(L);
            name = LinuxParse.PickName(comm, argv, null);
            if (name == comm) name = LinuxParse.PickName(comm, null, Exe(L)); // readlink only when argv didn't help
        }
        name = LinuxParse.CleanName(name);
        if (name.Length == 0) name = "pid " + pid;
        L.Name = name;
        L.LName = name.ToLowerInvariant();
    }

    List<string>? Argv(Life L)
    {
        if (L.ArgvRead) return L.Argv;
        L.ArgvRead = true;
        if (L.Kernel) return null;
        L.Argv = fr.TryRead(L.Dir + "/cmdline", out var d, whole: true, max: 64 * 1024) ? LinuxParse.SplitCmdline(d) : null;
        return L.Argv;
    }

    string? Exe(Life L)
    {
        if (L.ExeRead) return L.Exe;
        L.ExeRead = true;
        L.Exe = L.Kernel ? null : ResolveExe(L.Dir);
        return L.Exe;
    }

    /// <summary>
    /// /proc/[pid]/exe target (own processes only). Inside a Flatpak sandbox it reads "/app/…", which doesn't exist
    /// on the host; the sandbox's .flatpak-info gives the host path of /app.
    /// </summary>
    string? ResolveExe(string dir)
    {
        string? t = null;
        try { t = new FileInfo(dir + "/exe").LinkTarget; } catch { }
        if (string.IsNullOrEmpty(t)) return null;
        if (t.EndsWith(" (deleted)", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 10);
        if (t.StartsWith("/app/", StringComparison.Ordinal))
        {
            var info = fr.ReadText(dir + "/root/.flatpak-info");
            if (info != null)
            {
                var (appPath, id) = LinuxParse.ParseFlatpakInfo(info);
                if (appPath != null) t = appPath.TrimEnd('/') + t.Substring(4);
                else if (id != null) t = "/var/lib/flatpak/app/" + id + "/current/active/files" + t.Substring(4);
            }
        }
        return t;
    }

    Life? LifeOf(ProcInfo p) => lives.TryGetValue(p.Pid, out var L) && L.StartTime == p.CreateTime ? L : null;

    public string? CommandLine(ProcInfo p)
    {
        try
        {
            var L = LifeOf(p);
            List<string>? argv;
            if (L != null) argv = Argv(L);
            else argv = fr.TryRead(proc + "/" + p.Pid + "/cmdline", out var d, whole: true, max: 64 * 1024) ? LinuxParse.SplitCmdline(d) : null;
            if (argv == null) return null;
            bool singlePath = argv.Count == 1 && argv[0].StartsWith('/') && LinuxParse.HasWhitespace(argv[0]) && File.Exists(argv[0]);
            return LinuxParse.JoinCmdline(argv, singlePath);
        }
        catch { return null; }
    }

    public string? ImagePath(ProcInfo p)
    {
        try
        {
            var L = LifeOf(p);
            return L != null ? Exe(L) : ResolveExe(proc + "/" + p.Pid);
        }
        catch { return null; }
    }

    public string? DescribeImage(string path)
    {
        try { return LinuxParse.PrettyImageName(path, Rules.Aliases); }
        catch { return null; }
    }

    // ------------------------------------------------------------------ busiest thread

    public void FillThreads(List<ProcInfo> busy, double dtSec)
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            thrCur.Clear();
            int budget = ThreadBudget;
            foreach (var p in busy)
            {
                if (p.Threads <= 1)
                {
                    p.MaxThreadPct = Math.Clamp(p.CpuPct * ncpu, 0, 100); // the process is its only thread
                    continue;
                }
                if (budget <= 0) continue;
                double max = 0;
                int n = 0;
                string taskDir = proc + "/" + p.Pid + "/task";
                try
                {
                    foreach (int tid in NumericEntries(taskDir))
                    {
                        if (n++ >= ThreadsPerProcess || budget-- <= 0) break;
                        if (!fr.TryRead(taskDir + "/" + tid + "/stat", out var d, whole: false) || !LinuxParse.ParseStat(d, out var ts)) continue;
                        long ticks = ts.UTime + ts.STime;
                        var key = (tid, ts.StartTime);
                        thrCur[key] = (ticks, now);
                        if (thrPrev.TryGetValue(key, out var old))
                        {
                            double secs = (now - old.Stamp) / (double)Freq;
                            if (secs > 0.05) max = Math.Max(max, (ticks - old.Ticks) / clkTck / secs * 100);
                        }
                    }
                }
                catch { }
                p.MaxThreadPct = Math.Clamp(max, 0, 100);
            }
            (thrPrev, thrCur) = (thrCur, thrPrev);
        }
        catch { }
    }

    // ------------------------------------------------------------------ helpers

    static readonly EnumerationOptions EnumOpts = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Numeric entries of a directory (/proc pids, task tids) without allocating a string per entry.</summary>
    static IEnumerable<int> NumericEntries(string dir) =>
        new FileSystemEnumerable<int>(dir, static (ref FileSystemEntry e) => ParseInt(e.FileName), EnumOpts)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry e) => IsDigits(e.FileName),
        };

    static bool IsDigits(ReadOnlySpan<char> s)
    {
        if (s.Length == 0 || s.Length > 9) return false;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return true;
    }

    static int ParseInt(ReadOnlySpan<char> s)
    {
        int v = 0;
        foreach (char c in s) v = v * 10 + (c - '0');
        return v;
    }

    static double ClockTicks()
    {
        try
        {
            long v = LibC.sysconf(LibC._SC_CLK_TCK);
            if (v > 0 && v <= 100000) return v;
        }
        catch { }
        return 100;
    }

    static class LibC
    {
        public const int _SC_CLK_TCK = 2;
        [DllImport("libc", EntryPoint = "sysconf")] public static extern nint sysconf(int name);
    }

    public void Dispose() { }
}

/// <summary>Reads whole /proc and /sys files into one reused buffer (one collector thread).</summary>
internal sealed class FileReader
{
    byte[] buf = new byte[64 * 1024];
    const int MaxBytes = 4 << 20;

    /// <summary>
    /// Reads a file. whole = false stops after the first read() unless it filled the buffer: procfs per-process
    /// files return everything in one read. The span is valid until the next call.
    /// </summary>
    public bool TryRead(string path, out ReadOnlySpan<byte> data, bool whole = true, int max = MaxBytes)
    {
        data = default;
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int total = 0;
            while (true)
            {
                if (total == buf.Length)
                {
                    if (buf.Length >= max) break;
                    Array.Resize(ref buf, Math.Min(max, buf.Length * 2));
                }
                int room = Math.Min(buf.Length, max) - total;
                if (room <= 0) break;
                int n = RandomAccess.Read(h, buf.AsSpan(total, room), total);
                if (n <= 0) break;
                total += n;
                if (!whole && n < room) break;
            }
            data = buf.AsSpan(0, total);
            return true;
        }
        catch { return false; }
    }

    public string? ReadText(string path) =>
        TryRead(path, out var d) ? Encoding.UTF8.GetString(d).Trim() : null;

    /// <summary>A sysfs integer attribute, or -1.</summary>
    public long ReadLong(string path)
    {
        if (!TryRead(path, out var d, whole: false)) return -1;
        var t = d.Trim(" \t\n\r"u8);
        if (t.Length == 0 || (uint)(t[0] - (byte)'0') > 9 && t[0] != (byte)'-') return -1;
        return LinuxParse.ToLong(t);
    }
}
