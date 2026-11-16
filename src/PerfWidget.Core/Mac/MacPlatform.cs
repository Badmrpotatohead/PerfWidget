using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static PerfWidget.MacNative;

namespace PerfWidget;

/// <summary>
/// macOS sampler: sysctl KERN_PROC_ALL + libproc per process, host_statistics for the system, IOKit for GPU and disks.
/// Without root, macOS only reveals CPU / memory / disk I/O of the user's own processes (proc_pid_rusage and
/// PROC_PIDTASKINFO return EPERM for root and other users); those processes are still listed, with zeros.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacPlatform : IPlatform
{
    // ---- native buffers, allocated once and grown when needed
    byte* kinfoBuf; nuint kinfoCap;
    int* pidBuf; int pidCap;
    byte* pathBuf;
    byte* argsBuf; int argsCap;
    ulong* thrBuf; int thrCap;

    // ---- one-time facts
    readonly UInt128 tbNumer, tbDenom;            // mach absolute time -> ns
    readonly uint host, pset;
    readonly delegate* unmanaged<int, int> responsibleFor;
    readonly bool useKinfo;
    readonly long pageSize, ramTotal;
    readonly double cpuBaseMHz;
    readonly ThermalState thermal = new();
    readonly CF cf = new();
    readonly IntPtr kPerfStats, kDevUtil, kGpuActivity, kRendererUtil, kInUseMem, kModel,
                    kStatistics, kBytesR, kBytesW, kTimeR, kTimeW, kBsdName;

    static readonly byte[] NSwap = Z("vm.swapusage"),
                           NPressure = Z("kern.memorystatus_vm_pressure_level"), NIOService = Z("IOService");

    // ---- per process lifetime
    sealed class Life
    {
        public long CreateTime;        // start time in µs, +1 per exec (a new image in the same process)
        public ulong Comm0, Comm1;     // first 16 bytes of p_comm, to notice exec
        public string Name = "", LName = "";
        public string? Path;
        public int Responsible;
        public bool Denied;            // rusage said EPERM: don't ask again this lifetime
    }
    Dictionary<(int Pid, long Start), Life> lives = new();
    Dictionary<int, Life> livesByPid = new();

    // ---- deltas
    Dictionary<(int, long, ulong), (ulong Ns, long Stamp)> prevThreads = new();
    HostCpuLoadInfo prevCpu; bool havePrevCpu;
    ulong prevSwapIns, prevSwapOuts; bool havePrevVm;
    long prevSysStamp;
    int? thermalLevel; long thermalStamp = long.MinValue;

    // ---- IOKit
    List<uint>? gpus; long gpuEnumStamp; string gpuName = "Apple GPU";
    sealed class Disk { public uint Entry; public string Name = ""; public long R, W, Rt, Wt, Stamp; public bool Has; }
    List<Disk>? disks; long diskEnumStamp;
    List<DriveStat> drives = new(); long drivesStamp = long.MinValue;

    public string Name => "macos";
    public GroupingRules Rules { get; } = BuildRules();
    public string? CpuTempSource => null;
    public string CpuTempHint => "not exposed by macOS without admin tools";

    public MacPlatform()
    {
        SelfCheck();
        string layout = LayoutProblems();
        if (layout.Length > 0) Trace.WriteLine("PerfWidget macOS struct layout: " + layout);
        try
        {
            pathBuf = (byte*)NativeMemory.Alloc(PROC_PIDPATHINFO_MAXSIZE);
            Grow(ref kinfoBuf, ref kinfoCap, 1024 * (nuint)sizeof(KinfoProc));
            pidCap = 4096; pidBuf = (int*)NativeMemory.Alloc((nuint)pidCap * 4);
            thrCap = 1024; thrBuf = (ulong*)NativeMemory.Alloc((nuint)thrCap * 8);

            // Timebase. Apple Silicon: 125/3 (24 MHz ticks). Under Rosetta, mach_timebase_info reports 1/1 for the
            // translated process while the kernel's rusage times stay in native ticks, so ask hw.tbfrequency instead.
            MachTimebaseInfo tb;
            ulong numer = 1, denom = 1;
            if (mach_timebase_info(&tb) == 0 && tb.numer != 0 && tb.denom != 0) { numer = tb.numer; denom = tb.denom; }
            if (SysctlLong("sysctl.proc_translated") == 1)
            {
                long f = SysctlLong("hw.tbfrequency") ?? 24_000_000;
                if (f > 0) { ulong g = Gcd(1_000_000_000UL, (ulong)f); numer = 1_000_000_000UL / g; denom = (ulong)f / g; }
            }
            tbNumer = numer; tbDenom = denom;

            host = mach_host_self();
            uint ps = 0;
            if (processor_set_default(host, &ps) == 0) pset = ps;

            // host_statistics64 counts kernel pages (16 KiB on Apple Silicon, also for Rosetta processes)
            long page = 0;
            if (NativeLibrary.TryLoad(LibSystem, out var libSystem))
            {
                if (NativeLibrary.TryGetExport(libSystem, "vm_kernel_page_size", out var vkps)) page = (long)*(nuint*)vkps;
                if (NativeLibrary.TryGetExport(libSystem, "responsibility_get_pid_responsible_for_pid", out var rf))
                    responsibleFor = (delegate* unmanaged<int, int>)rf;
            }
            if (responsibleFor == null && NativeLibrary.TryLoad("/usr/lib/system/libquarantine.dylib", out var q)
                && NativeLibrary.TryGetExport(q, "responsibility_get_pid_responsible_for_pid", out var rf2))
                responsibleFor = (delegate* unmanaged<int, int>)rf2;
            if (page <= 0) page = SysctlLong("hw.pagesize") ?? 0;
            if (page <= 0) page = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 16384 : 4096;
            pageSize = page;
            ramTotal = SysctlLong("hw.memsize") ?? 0;
            long hz = SysctlLong("hw.cpufrequency") ?? 0; // Intel only; Apple Silicon has no public clock API
            cpuBaseMHz = hz > 0 ? hz / 1e6 : 0;

            useKinfo = KinfoWorks();

            kPerfStats = CFStr("PerformanceStatistics");
            kDevUtil = CFStr("Device Utilization %");
            kGpuActivity = CFStr("GPU Activity(%)");
            kRendererUtil = CFStr("Renderer Utilization %");
            kInUseMem = CFStr("In use system memory");
            kModel = CFStr("model");
            kStatistics = CFStr("Statistics");
            kBytesR = CFStr("Bytes (Read)");
            kBytesW = CFStr("Bytes (Write)");
            kTimeR = CFStr("Total Time (Read)");
            kTimeW = CFStr("Total Time (Write)");
            kBsdName = CFStr("BSD Name");
        }
        catch (Exception e) { Trace.WriteLine("PerfWidget macOS init: " + e.Message); }
    }

    static ulong Gcd(ulong a, ulong b) { while (b != 0) (a, b) = (b, a % b); return a; }

    static void Grow(ref byte* buf, ref nuint cap, nuint need)
    {
        if (buf != null && cap >= need) return;
        if (buf != null) NativeMemory.Free(buf);
        buf = (byte*)NativeMemory.Alloc(need);
        cap = need;
    }

    long AbsToHundredNs(ulong ticks) => (long)(ulong)((UInt128)ticks * tbNumer / tbDenom / 100);

    // ------------------------------------------------------------------ processes

    /// <summary>Checks the kinfo_proc layout against ourselves before trusting it.</summary>
    bool KinfoWorks()
    {
        try
        {
            int n = ReadKinfo();
            if (n <= 0) return false;
            int me = Environment.ProcessId, parent = getppid();
            var kp = (KinfoProc*)kinfoBuf;
            for (int i = 0; i < n; i++)
                if (kp[i].p_pid == me)
                    return kp[i].e_ppid == parent && kp[i].p_starttime_sec > 0 && kp[i].p_comm[0] != 0;
        }
        catch { }
        Trace.WriteLine("PerfWidget macOS: kinfo_proc check failed, using proc_listallpids");
        return false;
    }

    /// <summary>sysctl {CTL_KERN, KERN_PROC, KERN_PROC_ALL}: every process in one call. Returns the entry count or -1.</summary>
    int ReadKinfo()
    {
        int* mib = stackalloc int[3] { CTL_KERN, KERN_PROC, KERN_PROC_ALL };
        for (int attempt = 0; attempt < 4; attempt++)
        {
            nuint len = kinfoCap;
            if (sysctl(mib, 3, kinfoBuf, &len, null, 0) == 0)
                return len % (nuint)sizeof(KinfoProc) == 0 ? (int)(len / (nuint)sizeof(KinfoProc)) : -1;
            if (Marshal.GetLastPInvokeError() != ENOMEM) return -1;
            nuint need = 0;
            if (sysctl(mib, 3, null, &need, null, 0) != 0) return -1;
            Grow(ref kinfoBuf, ref kinfoCap, need + need / 4 + 64 * (nuint)sizeof(KinfoProc));
        }
        return -1;
    }

    public List<ProcInfo> ReadProcesses(double dtSec)
    {
        var list = new List<ProcInfo>(lives.Count + 64);
        var next = new Dictionary<(int, long), Life>(lives.Count + 64);
        var byPid = new Dictionary<int, Life>(lives.Count + 64);
        try
        {
            int n = useKinfo ? ReadKinfo() : -1;
            if (n > 0)
            {
                var kp = (KinfoProc*)kinfoBuf;
                for (int i = 0; i < n; i++)
                {
                    KinfoProc* k = kp + i;
                    if (k->p_stat == SZOMB) continue;
                    long start = k->p_starttime_sec * 1_000_000L + k->p_starttime_usec;
                    Add(list, next, byPid, k->p_pid, k->e_ppid, start, k->p_comm, 17);
                }
            }
            else ReadViaPidList(list, next, byPid);
        }
        catch { }
        lives = next;
        livesByPid = byPid;
        return list;
    }

    /// <summary>Fallback when kinfo_proc can't be trusted: proc_listallpids + proc_bsdinfo (own processes) or proc_bsdshortinfo.</summary>
    void ReadViaPidList(List<ProcInfo> list, Dictionary<(int, long), Life> next, Dictionary<int, Life> byPid)
    {
        int n = proc_listallpids(pidBuf, pidCap * 4);
        if (n >= pidCap)
        {
            NativeMemory.Free(pidBuf);
            pidCap = n * 2;
            pidBuf = (int*)NativeMemory.Alloc((nuint)pidCap * 4);
            n = proc_listallpids(pidBuf, pidCap * 4);
        }
        for (int i = 0; i < n && i < pidCap; i++)
        {
            int pid = pidBuf[i];
            ProcBsdInfo bi;
            ProcBsdShortInfo si;
            if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &bi, sizeof(ProcBsdInfo)) == sizeof(ProcBsdInfo))
            {
                if (bi.pbi_status == SZOMB) continue;
                long start = (long)bi.pbi_start_tvsec * 1_000_000L + (long)bi.pbi_start_tvusec;
                if (bi.pbi_name[0] != 0) Add(list, next, byPid, pid, (int)bi.pbi_ppid, start, bi.pbi_name, 32);
                else Add(list, next, byPid, pid, (int)bi.pbi_ppid, start, bi.pbi_comm, 16);
            }
            else if (proc_pidinfo(pid, PROC_PIDT_SHORTBSDINFO, 0, &si, sizeof(ProcBsdShortInfo)) == sizeof(ProcBsdShortInfo))
            {
                if (si.pbsi_status == SZOMB) continue;
                Add(list, next, byPid, pid, (int)si.pbsi_ppid, 0, si.pbsi_comm, 16);
            }
        }
    }

    void Add(List<ProcInfo> list, Dictionary<(int, long), Life> next, Dictionary<int, Life> byPid,
             int pid, int ppid, long start, byte* comm, int commMax)
    {
        try
        {
            ulong c0 = Unsafe.ReadUnaligned<ulong>(comm), c1 = Unsafe.ReadUnaligned<ulong>(comm + 8);
            var key = (pid, start);
            if (!lives.TryGetValue(key, out var life) || life.Comm0 != c0 || life.Comm1 != c1)
                life = NewLife(pid, ppid, life == null ? start : life.CreateTime + 1, c0, c1, CString(comm, commMax));
            next[key] = life;
            byPid[pid] = life;

            var p = new ProcInfo
            {
                Pid = pid, ParentPid = ppid, CreateTime = life.CreateTime,
                Name = life.Name, LName = life.LName, ResponsiblePid = life.Responsible,
            };
            if (!life.Denied) ReadUsage(p, life);
            list.Add(p);
        }
        catch { }
    }

    Life NewLife(int pid, int ppid, long createTime, ulong c0, ulong c1, string comm)
    {
        var l = new Life { CreateTime = createTime, Comm0 = c0, Comm1 = c1, Path = PidPath(pid) };
        // p_comm keeps 16 characters ("Google Chrome He"); the executable's file name has them all
        string name = comm;
        string? file = l.Path != null ? BaseName(l.Path) : null;
        if (file != null && (name.Length == 0 || (name.Length >= 15 && file.Length > name.Length && file.StartsWith(name, StringComparison.Ordinal))))
            name = file;
        else if (file == null && name.Length >= 15)
        {
            ProcBsdInfo bi; // pbi_name keeps 31 characters (own processes only)
            if (proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &bi, sizeof(ProcBsdInfo)) == sizeof(ProcBsdInfo) && bi.pbi_name[0] != 0)
                name = CString(bi.pbi_name, 32);
        }
        if (name.Length == 0) name = pid == 0 ? "kernel_task" : "pid " + pid;
        l.Name = name;
        l.LName = name.ToLowerInvariant();
        // XPC services, app extensions and helpers started by launchd: Activity Monitor files them under their "responsible" app
        if (ppid == 1 && responsibleFor != null)
        {
            int r = responsibleFor(pid);
            if (r > 0 && r != pid) l.Responsible = r;
        }
        return l;
    }

    void ReadUsage(ProcInfo p, Life life)
    {
        RusageInfoV4 ru;
        if (proc_pid_rusage(p.Pid, RUSAGE_INFO_V4, &ru) != 0)
        {
            if (Marshal.GetLastPInvokeError() == EPERM) life.Denied = true;
            return;
        }
        p.CpuTime = AbsToHundredNs(ru.ri_user_time + ru.ri_system_time);
        p.PrivateWs = (long)ru.ri_phys_footprint;          // Activity Monitor "Memory"
        p.Commit = p.PrivateWs;                            // footprint already counts compressed + swapped pages
        p.WorkingSet = (long)ru.ri_resident_size;
        p.IoBytes = (long)(ru.ri_diskio_bytesread + ru.ri_diskio_byteswritten);
        p.HardFaults = (long)ru.ri_pageins;
        ProcTaskInfo ti;
        if (proc_pidinfo(p.Pid, PROC_PIDTASKINFO, 0, &ti, sizeof(ProcTaskInfo)) == sizeof(ProcTaskInfo))
            p.Threads = ti.pti_threadnum;
    }

    string? PidPath(int pid)
    {
        if (pid <= 0 || pathBuf == null) return null;
        int n = proc_pidpath(pid, pathBuf, PROC_PIDPATHINFO_MAXSIZE);
        return n > 0 ? Encoding.UTF8.GetString(pathBuf, Math.Min(n, PROC_PIDPATHINFO_MAXSIZE)) : null;
    }

    static string? BaseName(string path)
    {
        int i = path.LastIndexOf('/');
        string f = i >= 0 ? path[(i + 1)..] : path;
        return f.Length > 0 ? f : null;
    }

    public void FillThreads(List<ProcInfo> busy, double dtSec)
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            var next = new Dictionary<(int, long, ulong), (ulong, long)>(prevThreads.Count + 64);
            int budget = 4096; // PROC_PIDTHREADINFO calls per second, at most
            foreach (var p in busy.OrderByDescending(x => x.CpuPct))
            {
                if (budget <= 0) break;
                int n = ListThreads(p.Pid);
                double max = 0;
                for (int i = 0; i < n && budget > 0; i++, budget--)
                {
                    ulong h = thrBuf[i];
                    ProcThreadInfo ti;
                    if (proc_pidinfo(p.Pid, PROC_PIDTHREADINFO, h, &ti, sizeof(ProcThreadInfo)) != sizeof(ProcThreadInfo)) continue;
                    ulong ns = ti.pth_user_time + ti.pth_system_time;
                    var key = (p.Pid, p.CreateTime, h);
                    double pct;
                    if (prevThreads.TryGetValue(key, out var o) && ns >= o.Ns && now > o.Stamp)
                        pct = (ns - o.Ns) / ((now - o.Stamp) * 1e9 / Stopwatch.Frequency) * 100;
                    else
                        pct = ti.pth_cpu_usage / 10.0; // first look: the scheduler's decaying average (1000 = one core)
                    next[key] = (ns, now);
                    if (pct > max) max = pct;
                }
                p.MaxThreadPct = Math.Clamp(max, 0, 100);
            }
            prevThreads = next;
        }
        catch { }
    }

    /// <summary>PROC_PIDLISTTHREADS into thrBuf. Returns the thread count (0 on failure).</summary>
    int ListThreads(int pid)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int bytes = proc_pidinfo(pid, PROC_PIDLISTTHREADS, 0, thrBuf, thrCap * 8);
            if (bytes <= 0) return 0;
            int n = bytes / 8;
            if (n < thrCap || thrCap >= 16384) return Math.Min(n, thrCap);
            NativeMemory.Free(thrBuf);
            thrCap *= 2;
            thrBuf = (ulong*)NativeMemory.Alloc((nuint)thrCap * 8);
        }
        return 0;
    }

    public string? CommandLine(ProcInfo p)
    {
        try
        {
            if (p.Pid <= 0) return null;
            if (argsBuf == null)
            {
                // KERN_PROCARGS2 wants room for the whole argument area (kern.argmax, 1 MiB on current macOS)
                argsCap = (int)Math.Clamp(SysctlLong("kern.argmax") ?? (1 << 20), 64 * 1024, 4 << 20);
                argsBuf = (byte*)NativeMemory.Alloc((nuint)argsCap);
            }
            int* mib = stackalloc int[3] { CTL_KERN, KERN_PROCARGS2, p.Pid };
            nuint len = (nuint)argsCap;
            if (sysctl(mib, 3, argsBuf, &len, null, 0) != 0) return null;
            var argv = ParseProcArgs2(new ReadOnlySpan<byte>(argsBuf, (int)Math.Min(len, (nuint)argsCap)));
            return argv == null ? null : CmdLine.Join(argv);
        }
        catch { return null; }
    }

    public string? ImagePath(ProcInfo p)
    {
        try
        {
            if (livesByPid.TryGetValue(p.Pid, out var l) && l.CreateTime == p.CreateTime) return l.Path;
            return PidPath(p.Pid);
        }
        catch { return null; }
    }

    public string? DescribeImage(string path)
    {
        try { return BundleName(path, forGrouping: false); }
        catch { return null; }
    }

    // ------------------------------------------------------------------ system

    public void FillSystem(Snapshot s, List<ProcInfo> procs, double dtSec)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = prevSysStamp != 0 ? (now - prevSysStamp) / (double)Stopwatch.Frequency : dtSec;
        if (dt <= 0) dt = dtSec > 0 ? dtSec : 1;
        prevSysStamp = now;

        try { Cpu(s); } catch { }
        try { Memory(s, dt); } catch { }
        try
        {
            if (thermalStamp == long.MinValue || Stopwatch.GetElapsedTime(thermalStamp).TotalSeconds >= 5)
            {
                thermalLevel = thermal.Read();
                thermalStamp = now;
            }
            s.ThermalLevel = thermalLevel;
        }
        catch { }
        try { Gpu(s); } catch { }
        try { Disks(s, now); } catch { }
        try
        {
            if (drivesStamp == long.MinValue || Stopwatch.GetElapsedTime(drivesStamp).TotalSeconds >= 10)
            {
                drives = ReadDrives();
                drivesStamp = now;
            }
            s.Drives = drives;
        }
        catch { }
        s.CpuMHz = 0;            // no public API for the live clock
        s.CpuBaseMHz = cpuBaseMHz;
        s.CpuTemp = null;
    }

    void Cpu(Snapshot s)
    {
        HostCpuLoadInfo li;
        uint cnt = HOST_CPU_LOAD_INFO_COUNT;
        if (host != 0 && host_statistics(host, HOST_CPU_LOAD_INFO, (int*)&li, &cnt) == 0)
        {
            if (havePrevCpu)
            {
                // natural_t tick counters wrap after ~a month on big machines: unsigned subtraction handles one wrap
                double user = unchecked(li.user - prevCpu.user), sys = unchecked(li.system - prevCpu.system),
                       idle = unchecked(li.idle - prevCpu.idle), nice = unchecked(li.nice - prevCpu.nice);
                double total = user + sys + idle + nice;
                if (total > 0) s.CpuPct = Math.Clamp((user + sys + nice) * 100 / total, 0, 100);
            }
            prevCpu = li;
            havePrevCpu = true;
        }
        // every thread on the machine, including the ones we may not inspect per process
        ProcessorSetLoadInfo ld;
        cnt = PROCESSOR_SET_LOAD_INFO_COUNT;
        if (pset != 0 && processor_set_statistics(pset, PROCESSOR_SET_LOAD_INFO, (int*)&ld, &cnt) == 0 && ld.thread_count > 0)
            s.ThreadCount = ld.thread_count;
    }

    void Memory(Snapshot s, double dt)
    {
        long total = ramTotal;
        long used = 0;
        VmStatistics64 vm;
        uint cnt = HOST_VM_INFO64_COUNT;
        if (host != 0 && host_statistics64(host, HOST_VM_INFO64, (int*)&vm, &cnt) == 0)
        {
            long pg = pageSize;
            long pages = cnt >= HOST_VM_INFO64_COUNT
                // Activity Monitor "Memory Used" = app memory (anonymous - purgeable) + wired + compressed
                ? Math.Max(0, (long)vm.internal_page_count - vm.purgeable_count) + vm.wire_count + vm.compressor_page_count
                : (long)vm.active_count + vm.wire_count; // pre-10.9 layout (never expected)
            used = pages * pg;
            if (total <= 0) total = ((long)vm.free_count + vm.active_count + vm.inactive_count + vm.wire_count + vm.compressor_page_count) * pg;
            s.Cache = (long)vm.external_page_count * pg;
            s.Compressed = (long)vm.compressor_page_count * pg;
            if (havePrevVm && dt > 0)
            {
                s.PagesInBps = vm.swapins >= prevSwapIns ? (vm.swapins - prevSwapIns) * (double)pg / dt : 0;
                s.PagesOutBps = vm.swapouts >= prevSwapOuts ? (vm.swapouts - prevSwapOuts) * (double)pg / dt : 0;
            }
            prevSwapIns = vm.swapins;
            prevSwapOuts = vm.swapouts;
            havePrevVm = true;
        }
        used = total > 0 ? Math.Clamp(used, 0, total) : used;
        s.RamTotal = total;
        s.RamAvail = Math.Max(0, total - used);

        long swapUsed = 0;
        if (SysctlStruct(NSwap, out XswUsage sw))
        {
            s.PagefileTotal = (long)sw.xsu_total;
            s.PagefileUsed = swapUsed = (long)sw.xsu_used;
        }
        s.CommitTotal = used + swapUsed;
        s.CommitLimit = 0; // macOS grows swap on demand: no commit limit

        if (SysctlStruct(NPressure, out int level))
            s.MemoryPressure = level switch { 1 => 0, 2 => 1, 4 => 2, _ => null };
        s.RamAlertsFromPressureOnly = true;
    }

    void Gpu(Snapshot s)
    {
        if (gpus == null || Stopwatch.GetElapsedTime(gpuEnumStamp).TotalSeconds >= 60)
        {
            if (gpus != null) foreach (var e in gpus) IOObjectRelease(e);
            gpus = Services("IOAccelerator");
            gpuEnumStamp = Stopwatch.GetTimestamp();
            string? name = null;
            foreach (var e in gpus)
            {
                name = GpuModel(e);
                if (name != null) break;
            }
            gpuName = name ?? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "Apple GPU" : "GPU");
        }
        long best = -1, mem = 0;
        foreach (var e in gpus)
        {
            IntPtr st = IORegistryEntryCreateCFProperty(e, kPerfStats, IntPtr.Zero, 0);
            if (st == IntPtr.Zero) continue;
            try
            {
                if (!cf.IsDict(st)) continue;
                if (!cf.Long(st, kDevUtil, out long u) && !cf.Long(st, kGpuActivity, out u) && !cf.Long(st, kRendererUtil, out u)) u = 0;
                cf.Long(st, kInUseMem, out long m);
                if (u > best) { best = u; mem = m; }
            }
            finally { CFRelease(st); }
        }
        if (best >= 0) s.GpuPct = Math.Clamp(best, 0, 100);
        s.VramUsed = Math.Max(0, mem);
        s.VramTotal = 0; // unified memory
        s.GpuName = gpuName;
    }

    string? GpuModel(uint entry)
    {
        // Apple Silicon: "model" = "Apple M2 Pro" on the AGXAccelerator itself. Intel/AMD: CFData on the parent PCI device.
        IntPtr v = IORegistryEntryCreateCFProperty(entry, kModel, IntPtr.Zero, 0);
        if (v == IntPtr.Zero)
            fixed (byte* plane = NIOService)
                v = IORegistryEntrySearchCFProperty(entry, plane, kModel, IntPtr.Zero, kIORegistryIterateRecursively | kIORegistryIterateParents);
        if (v == IntPtr.Zero) return null;
        try
        {
            var t = cf.Text(v)?.Trim();
            // the platform root also has a "model" ("MacBookPro18,3"): not a GPU name
            return string.IsNullOrEmpty(t) || t.Contains(',') ? null : t;
        }
        finally { CFRelease(v); }
    }

    void Disks(Snapshot s, long now)
    {
        if (disks == null || Stopwatch.GetElapsedTime(diskEnumStamp).TotalSeconds >= 10)
        {
            var old = disks;
            var fresh = new List<Disk>();
            int i = 0;
            foreach (var e in Services("IOBlockStorageDriver"))
            {
                string name = BsdName(e) ?? "disk?" + i;
                i++;
                var d = new Disk { Entry = e, Name = name };
                var o = old?.FirstOrDefault(x => x.Name == name);
                if (o != null) { d.R = o.R; d.W = o.W; d.Rt = o.Rt; d.Wt = o.Wt; d.Stamp = o.Stamp; d.Has = o.Has; }
                fresh.Add(d);
            }
            if (old != null) foreach (var d in old) IOObjectRelease(d.Entry);
            disks = fresh;
            diskEnumStamp = Stopwatch.GetTimestamp();
        }
        var list = new List<DiskStat>(disks.Count);
        foreach (var d in disks)
        {
            IntPtr st = IORegistryEntryCreateCFProperty(d.Entry, kStatistics, IntPtr.Zero, 0);
            if (st == IntPtr.Zero) continue;
            try
            {
                if (!cf.IsDict(st)) continue;
                cf.Long(st, kBytesR, out long r);
                cf.Long(st, kBytesW, out long w);
                cf.Long(st, kTimeR, out long rt);   // nanoseconds spent in reads, summed over requests
                cf.Long(st, kTimeW, out long wt);
                double busy = 0, bps = 0;
                if (d.Has && now > d.Stamp)
                {
                    double dt = (now - d.Stamp) / (double)Stopwatch.Frequency;
                    bps = Math.Max(0, (r - d.R) + (w - d.W)) / dt;
                    busy = Math.Clamp(Math.Max(0, (rt - d.Rt) + (wt - d.Wt)) / (dt * 1e9) * 100, 0, 100);
                }
                d.R = r; d.W = w; d.Rt = rt; d.Wt = wt; d.Stamp = now; d.Has = true;
                if (r + w > 0) list.Add(new DiskStat(d.Name, busy, bps));
            }
            finally { CFRelease(st); }
        }
        s.Disks = list;
    }

    string? BsdName(uint driver)
    {
        // the driver's first IOMedia child is the whole disk ("disk0"); a depth-first search finds it first
        IntPtr v;
        fixed (byte* plane = NIOService)
            v = IORegistryEntrySearchCFProperty(driver, plane, kBsdName, IntPtr.Zero, kIORegistryIterateRecursively);
        if (v == IntPtr.Zero) return null;
        try { return cf.Text(v); }
        finally { CFRelease(v); }
    }

    static List<DriveStat> ReadDrives()
    {
        var list = new List<DriveStat>();
        var seen = new HashSet<(long, long)>();
        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); } catch { return list; }
        foreach (var d in all.OrderBy(d => d.Name == "/" ? 0 : 1))
        {
            try
            {
                string mount = d.Name.Length > 1 ? d.Name.TrimEnd('/') : d.Name;
                bool root = mount == "/";
                // "/" and real volumes only: not /System/Volumes/*, /dev, simulator images, hidden /Volumes/.timemachine
                if (!root && !(mount.StartsWith("/Volumes/", StringComparison.Ordinal) && mount.Length > 9 && mount[9] != '.')) continue;
                if (d.DriveType is DriveType.Network or DriveType.CDRom or DriveType.Ram) continue;
                if (!d.IsReady) continue;
                long total = d.TotalSize, free = d.AvailableFreeSpace;
                if (total <= 0) continue;
                // read-only volumes (mounted .dmg installers) are always "full"; "/" is the sealed read-only system snapshot
                if (!root && IsReadOnly(mount)) continue;
                // APFS volumes of one container share their size and free space: show the container once
                if (!seen.Add((total, free))) continue;
                list.Add(new DriveStat(root ? "Macintosh HD" : mount[9..], free, total, root));
            }
            catch { }
        }
        return list;
    }

    static bool IsReadOnly(string mount)
    {
        StatFs64 st;
        fixed (byte* p = Z(mount))
            return StatFs(p, &st) == 0 && (st.f_flags & MNT_RDONLY) != 0;
    }

    // ------------------------------------------------------------------ parsing (pure, testable)

    /// <summary>
    /// KERN_PROCARGS2 buffer: int argc, the exec path, NUL padding, then argc NUL-terminated arguments (then the environment).
    /// Returns argv, or null when the buffer is malformed.
    /// </summary>
    internal static List<string>? ParseProcArgs2(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) return null;
        int argc = MemoryMarshal.Read<int>(b);
        if (argc <= 0 || argc > 100_000) return null;
        int i = 4;
        while (i < b.Length && b[i] != 0) i++;   // exec path
        while (i < b.Length && b[i] == 0) i++;   // padding
        var argv = new List<string>(Math.Min(argc, 64));
        while (argv.Count < argc && i < b.Length)
        {
            int s = i;
            while (i < b.Length && b[i] != 0) i++;
            argv.Add(Encoding.UTF8.GetString(b.Slice(s, i - s)));
            i++;
        }
        return argv.Count > 0 ? argv : null;
    }

    static readonly HashSet<string> SystemAppsShown = new(StringComparer.OrdinalIgnoreCase) { "Finder", "Dock", "Spotlight", "Siri" };
    static readonly Dictionary<string, string> BundleRenames = new(StringComparer.Ordinal)
    {
        ["Docker"] = "Docker Desktop",   // matches the com.docker.* aliases
        ["zoom.us"] = "Zoom",
    };

    /// <summary>
    /// Name of the outermost "X.app" bundle in an executable path, e.g.
    /// "/Applications/Google Chrome.app/Contents/Frameworks/…/Google Chrome Helper (Renderer).app/…" -> "Google Chrome".
    /// forGrouping (AppFromPath): skips macOS's own bundles under /System/Library (except Finder, Dock, Spotlight, Siri and
    /// CoreServices/Applications), bundles inside a .framework (Python.app) and inside node_modules (a dev copy of Electron).
    /// </summary>
    internal static string? BundleName(string path, bool forGrouping)
    {
        if (string.IsNullOrEmpty(path) || !path.Contains(".app/", StringComparison.OrdinalIgnoreCase)) return null;
        // macOS 13+: Safari and parts of the OS run from cryptexes ("/System/Volumes/Preboot/Cryptexes/App/System/Applications/Safari.app")
        const string cryptex = "/System/Volumes/Preboot/Cryptexes/";
        if (path.StartsWith(cryptex, StringComparison.Ordinal))
        {
            int slash = path.IndexOf('/', cryptex.Length);
            if (slash > 0) path = path[slash..];
        }
        bool system = path.StartsWith("/System/Library/", StringComparison.Ordinal)
                   || path.StartsWith("/Library/Apple/System/Library/", StringComparison.Ordinal);
        bool systemShown = path.StartsWith("/System/Library/CoreServices/Applications/", StringComparison.Ordinal);
        var parts = path.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string c = parts[i];
            if (c.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && c.Length > 4)
            {
                string name = c[..^4];
                if (!forGrouping) return name;
                if (system && !systemShown && !SystemAppsShown.Contains(name)) return null;
                return BundleRenames.TryGetValue(name, out var r) ? r : name;
            }
            if (forGrouping && (c.EndsWith(".framework", StringComparison.OrdinalIgnoreCase) || c == "node_modules")) return null;
        }
        return null;
    }

    static string? AppFromPath(string path)
    {
        try { return BundleName(path, forGrouping: true); }
        catch { return null; }
    }

    /// <summary>Desk check of the parsers and struct layouts. Empty string = all good.</summary>
    internal static string SelfTest()
    {
        var bad = new StringBuilder();
        void Eq(string what, string? got, string? want) { if (got != want) bad.Append($"{what}: got '{got}', want '{want}'; "); }

        static byte[] Args(int argc, params string[] parts)
        {
            var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes(argc));
            foreach (var p in parts) ms.Write(Encoding.UTF8.GetBytes(p));
            return ms.ToArray();
        }
        static string? J(byte[] b) { var a = ParseProcArgs2(b); return a == null ? null : CmdLine.Join(a); }

        // exec path, 3 NULs of padding, argv, environment
        Eq("procargs python", J(Args(3, "/usr/bin/python3\0\0\0\0", "python3\0", "-c\0", "print(1)\0", "PATH=/usr/bin\0")), "python3 -c print(1)");
        // no padding after the exec path; a login shell's argv[0] starts with '-'
        Eq("procargs zsh", J(Args(2, "/bin/zsh\0", "-zsh\0", "-l\0")), "-zsh -l");
        // an argument with a space is quoted so CmdLine.Split reads it back
        Eq("procargs space", J(Args(2, "/opt/homebrew/bin/node\0\0", "node\0", "/Users/me/My App/server.js\0", "HOME=/Users/me\0")),
           "node \"/Users/me/My App/server.js\"");
        // truncated buffer: what is there
        Eq("procargs truncated", J(Args(3, "/bin/ls\0\0", "ls\0")), "ls");
        Eq("procargs argc 0", J(Args(0, "/bin/ls\0\0")), null);
        Eq("procargs short", J(new byte[] { 1, 0 }), null);

        const string chrome = "/Applications/Google Chrome.app/Contents/Frameworks/Google Chrome Framework.framework/Versions/131.0.6778.86/Helpers/Google Chrome Helper (Renderer).app/Contents/MacOS/Google Chrome Helper (Renderer)";
        const string python = "/opt/homebrew/Cellar/python@3.12/3.12.7/Frameworks/Python.framework/Versions/3.12/Resources/Python.app/Contents/MacOS/Python";
        const string webkit = "/System/Volumes/Preboot/Cryptexes/OS/System/Library/Frameworks/WebKit.framework/Versions/A/XPCServices/com.apple.WebKit.WebContent.xpc/Contents/MacOS/com.apple.WebKit.WebContent";
        Eq("app chrome", BundleName(chrome, true), "Google Chrome");
        Eq("app safari", BundleName("/System/Volumes/Preboot/Cryptexes/App/System/Applications/Safari.app/Contents/MacOS/Safari", true), "Safari");
        Eq("app webkit", BundleName(webkit, true), null);
        Eq("app finder", BundleName("/System/Library/CoreServices/Finder.app/Contents/MacOS/Finder", true), "Finder");
        Eq("app system", BundleName("/System/Library/CoreServices/WindowManager.app/Contents/MacOS/WindowManager", true), null);
        Eq("desc system", BundleName("/System/Library/CoreServices/WindowManager.app/Contents/MacOS/WindowManager", false), "WindowManager");
        Eq("app archive", BundleName("/System/Library/CoreServices/Applications/Archive Utility.app/Contents/MacOS/Archive Utility", true), "Archive Utility");
        Eq("app terminal", BundleName("/System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal", true), "Terminal");
        Eq("app python", BundleName(python, true), null);
        Eq("desc python", BundleName(python, false), "Python");
        Eq("app electron dev", BundleName("/Users/me/proj/node_modules/electron/dist/Electron.app/Contents/MacOS/Electron", true), null);
        Eq("app vscode", BundleName("/Applications/Visual Studio Code.app/Contents/MacOS/Electron", true), "Visual Studio Code");
        Eq("app claude helper", BundleName("/Applications/Claude.app/Contents/Frameworks/Claude Helper (Renderer).app/Contents/MacOS/Claude Helper (Renderer)", true), "Claude");
        Eq("app docker", BundleName("/Applications/Docker.app/Contents/MacOS/com.docker.backend", true), "Docker Desktop");
        Eq("app xprotect", BundleName("/Library/Apple/System/Library/CoreServices/XProtect.app/Contents/MacOS/XProtect", true), null);
        Eq("app node", BundleName("/opt/homebrew/bin/node", true), null);
        Eq("app translocated", BundleName("/private/var/folders/xy/T/AppTranslocation/1234/d/Foo.app/Contents/MacOS/Foo", true), "Foo");

        bad.Append(LayoutProblems());
        return bad.ToString();
    }

    [Conditional("DEBUG")]
    static void SelfCheck()
    {
        string r = SelfTest();
        Debug.Assert(r.Length == 0, "MacPlatform self-test: " + r);
    }

    // ------------------------------------------------------------------ grouping rules

    static GroupingRules BuildRules()
    {
        var r = new GroupingRules
        {
            Boundary = new()
            {
                "launchd", "kernel_task", "loginwindow", "windowserver", "login", "sshd", "sshd-session",
                // terminals and multiplexers: what runs in them is its own app
                "terminal", "iterm2", "ghostty", "warp", "wezterm-gui", "alacritty", "kitty", "hyper", "tabby", "rio",
                "tmux", "screen", "zellij", "mosh-server",
                // the VS Code-family utility process that hosts the integrated terminal's shells
                "code helper", "code - insiders helper", "cursor helper", "windsurf helper",
            },
            Shells = new() { "zsh", "bash", "sh", "fish", "dash", "ksh", "tcsh", "csh", "nu", "pwsh", "-zsh", "-bash", "-sh", "-fish" },
            PrefixAliases = new[]
            {
                ("com.docker.", "Docker Desktop"), ("xprotect", "XProtect (malware scan)"),
                ("com.apple.mobilesoftwareupdate", "Software Update"), ("mdworker", "Spotlight indexing"),
                ("perfwidget", "PerfWidget (this monitor)"),
                ("unrealeditor", "Unreal Engine"), ("godot", "Godot"), ("blender", "Blender"),
            },
            GenericHosts = new()
            {
                "node", "python", "python3", "java", "ruby", "bun", "deno", "electron", "dotnet", "perl", "php",
                "python3.8", "python3.9", "python3.10", "python3.11", "python3.12", "python3.13", "python3.14", "python3.15",
            },
            AppFromPath = AppFromPath,
        };
        var d = r.Aliases;
        void A(string name, params string[] procs) { foreach (var p in procs) d[p] = name; }
        // Apps in .app bundles are named by AppFromPath. These are the heavy hitters that live outside a bundle,
        // or whose bundle is a macOS internal. Never alias WebKit / Virtualization XPC services: that would make them
        // anchors and stop them joining their "responsible" app (Safari, Mail, Docker…).
        A("macOS kernel", "kernel_task");
        A("WindowServer (display)", "windowserver");
        A("macOS core", "launchd", "loginwindow", "logd", "configd", "notifyd", "distnoted", "cfprefsd", "opendirectoryd",
          "securityd", "trustd", "runningboardd", "powerd", "kernelmanagerd", "usermanagerd", "coreservicesd", "lsd");
        A("Spotlight indexing", "mds", "mds_stores", "mdworker", "mdworker_shared", "mdbulkimport", "mdsync", "mdimport",
          "mdutil", "corespotlightd", "spotlightknowledged");
        A("Photos analysis", "photoanalysisd", "mediaanalysisd", "photolibraryd", "mediaanalysisd-access");
        A("Time Machine", "backupd", "backupd-helper");
        A("Software Update", "softwareupdated", "softwareupdate", "system_installd", "osinstallersetupd", "mobileassetd");
        A("iCloud sync", "bird", "cloudd", "cloudphotod");
        A("Gatekeeper", "syspolicyd");
        A("Rosetta translation", "oahd", "oahd-helper", "oahd-root-helper");
        A("Crash reports / diagnostics", "reportcrash", "spindump", "tailspind", "osanalyticshelper", "sysdiagnose");
        A("Core Audio", "coreaudiod");
        A("File system events", "fseventsd");
        A("Background downloads", "nsurlsessiond");
        A("Docker Desktop", "docker desktop", "vpnkit-bridge");
        A("Claude", "claude");
        A("Ollama", "ollama", "ollama_llama_server");
        A("PerfWidget (this monitor)", "perfwidget", "perfwidget.app");
        return r;
    }

    // ------------------------------------------------------------------ cleanup

    public void Dispose()
    {
        try
        {
            if (gpus != null) foreach (var e in gpus) IOObjectRelease(e);
            if (disks != null) foreach (var x in disks) IOObjectRelease(x.Entry);
            gpus = null; disks = null;
            foreach (var k in new[] { kPerfStats, kDevUtil, kGpuActivity, kRendererUtil, kInUseMem, kModel,
                                      kStatistics, kBytesR, kBytesW, kTimeR, kTimeW, kBsdName })
                if (k != IntPtr.Zero) CFRelease(k);
        }
        catch { }
        if (kinfoBuf != null) { NativeMemory.Free(kinfoBuf); kinfoBuf = null; }
        if (pidBuf != null) { NativeMemory.Free(pidBuf); pidBuf = null; }
        if (pathBuf != null) { NativeMemory.Free(pathBuf); pathBuf = null; }
        if (argsBuf != null) { NativeMemory.Free(argsBuf); argsBuf = null; }
        if (thrBuf != null) { NativeMemory.Free(thrBuf); thrBuf = null; }
    }
}
