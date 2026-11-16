using System;
using System.Collections.Generic;

namespace PerfWidget;

/// <summary>One OS process, as reported by the platform sampler and enriched by the collector.</summary>
public sealed class ProcInfo
{
    // ---- set by the platform sampler (IPlatform.ReadProcesses) ----
    public int Pid, ParentPid, Threads;
    /// <summary>Process start stamp in any unit; (Pid, CreateTime) must identify one process lifetime.</summary>
    public long CreateTime;
    /// <summary>Cumulative user+kernel CPU time, in 100 ns units.</summary>
    public long CpuTime;
    /// <summary>The memory number users recognise (Task Manager "Memory", Activity Monitor "Memory", Linux private RSS), bytes.</summary>
    public long PrivateWs;
    /// <summary>Resident set (working set / RSS), bytes.</summary>
    public long WorkingSet;
    /// <summary>Private committed memory (Windows commit; macOS footprint + swapped; Linux private + swap), bytes.</summary>
    public long Commit;
    /// <summary>Cumulative I/O bytes (Windows: all I/O incl. network/pipes; macOS/Linux: real disk I/O).</summary>
    public long IoBytes;
    /// <summary>Cumulative hard (major) page faults.</summary>
    public long HardFaults;
    public string Name = "", LName = "";
    /// <summary>Busiest single thread, % of one core, over the last interval. Set by ReadProcesses or FillThreads.</summary>
    public double MaxThreadPct;
    /// <summary>Optional: a better parent for grouping than ParentPid (macOS "responsible" process). 0 = none.</summary>
    public int ResponsiblePid;

    // ---- set by the collector / FillSystem ----
    public ProcInfo? Parent;
    public double CpuPct, IoBps, HardFaultsPs, GpuPct;
    public long Vram;
    public string? CommandLine;
    public string Summary = "";
}

public sealed class AppGroup
{
    public string Key = "", Name = "";
    public readonly List<ProcInfo> Members = new();
    public double CpuPct, IoBps, HardFaultsPs, GpuPct, MaxThreadPct;
    public string MaxThreadProc = "";
    public long Ram, Commit, Vram;
}

public sealed record DiskStat(string Name, double BusyPct, double Bps);
public sealed record DriveStat(string Name, long Free, long Total, bool HasPagefile);

/// <summary>Severity: 2 = warning, 3 = critical. Resource: cpu, ram, commit, gpu, vram, disk, drive.</summary>
public sealed record Issue(int Severity, string Resource, string Title, string Detail)
{
    public string Text => Detail.Length > 0 ? $"{Title}: {Detail}" : Title;
}

public sealed class Snapshot
{
    /// <summary>"windows", "macos" or "linux".</summary>
    public string Platform = "";
    public int LogicalCpus, ProcessCount, ThreadCount;
    public double CpuPct;
    /// <summary>RamAvail = memory that can be handed to apps without paging (Windows available, Linux MemAvailable, macOS free+inactive+purgeable+file cache).</summary>
    public long RamTotal, RamAvail, Cache;
    /// <summary>Commit charge and limit. CommitLimit = 0 when the OS has no enforced limit (macOS, Linux heuristic overcommit): commit alerts are skipped.</summary>
    public long CommitTotal, CommitLimit;
    /// <summary>Page file / swap.</summary>
    public long PagefileTotal, PagefileUsed;
    /// <summary>Memory read back from page file / swap, and written out, bytes/s.</summary>
    public double PagesInBps, PagesOutBps, HardFaultsPs;
    /// <summary>macOS compressed memory, bytes (0 elsewhere).</summary>
    public long Compressed;
    /// <summary>OS memory-pressure verdict when the OS has one: 0 normal, 1 warning, 2 critical. macOS kernel level; Linux PSI.</summary>
    public int? MemoryPressure;
    /// <summary>When true, RAM alerts come only from MemoryPressure (macOS: high "used" is normal there).</summary>
    public bool RamAlertsFromPressureOnly;
    /// <summary>Linux PSI "some" avg10 for cpu / memory / io, % of time tasks were stalled.</summary>
    public double? CpuStallPct, MemStallPct, IoStallPct;
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

    // clocks & thermals
    /// <summary>CpuBaseMHz = 0 when unknown (Apple Silicon): clock-throttle checks are skipped.</summary>
    public double CpuMHz, CpuBaseMHz, CpuPerfLimit = 100;
    public double? CpuTemp;
    /// <summary>OS thermal state when available: 0 nominal, 1 fair, 2 serious (throttling), 3 critical. macOS.</summary>
    public int? ThermalLevel;
    public double? GpuTemp, GpuSlowdownTemp, GpuPowerW, GpuPowerLimitW, GpuClockMHz, GpuMaxClockMHz, GpuFanPct;
    public ulong GpuThrottleReasons;
    public DateTime Time = DateTime.Now;
}
