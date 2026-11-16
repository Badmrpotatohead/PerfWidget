using System;
using System.Collections.Generic;

namespace PerfWidget;

/// <summary>
/// Everything OS-specific. The collector owns deltas (CPU %, I/O rate), grouping, smoothing, alerts.
/// A platform only reads raw cumulative counters. All calls come from one collector thread.
/// </summary>
public interface IPlatform : IDisposable
{
    /// <summary>"windows", "macos" or "linux".</summary>
    string Name { get; }

    GroupingRules Rules { get; }

    /// <summary>All processes with the raw fields documented on <see cref="ProcInfo"/>. dtSec = seconds since the previous call.</summary>
    List<ProcInfo> ReadProcesses(double dtSec);

    /// <summary>
    /// Called after CPU % is known, with the processes using at least half a core. Platforms that
    /// can't cheaply read every thread each second compute MaxThreadPct here. May be a no-op.
    /// </summary>
    void FillThreads(List<ProcInfo> busy, double dtSec);

    /// <summary>System-wide fields of the snapshot, plus per-process GpuPct / Vram where the OS exposes them.</summary>
    void FillSystem(Snapshot s, List<ProcInfo> procs, double dtSec);

    /// <summary>Full command line, or null. Called once per process lifetime.</summary>
    string? CommandLine(ProcInfo p);

    /// <summary>Executable path, or null.</summary>
    string? ImagePath(ProcInfo p);

    /// <summary>Human name for an executable (file description, bundle name…), or null to use the process name.</summary>
    string? DescribeImage(string path);

    /// <summary>Where CPU temperature comes from, or null if unavailable.</summary>
    string? CpuTempSource { get; }

    /// <summary>One line telling the user how to get CPU temperature when it's missing.</summary>
    string CpuTempHint { get; }
}

/// <summary>How processes roll up into apps on a given OS. All names are lower-case process names (Windows keeps ".exe").</summary>
public sealed class GroupingRules
{
    /// <summary>OS launchers: their children start new apps.</summary>
    public HashSet<string> Boundary = new();
    /// <summary>Interactive shells: a boundary only when the user opened them (their parent is a boundary).</summary>
    public HashSet<string> Shells = new();
    /// <summary>process name -> app name.</summary>
    public Dictionary<string, string> Aliases = new();
    public (string Prefix, string Name)[] PrefixAliases = Array.Empty<(string, string)>();
    /// <summary>Runtimes hosting many apps (node, python, electron…): named after the folder or script they run.</summary>
    public HashSet<string> GenericHosts = new();
    /// <summary>
    /// Optional: app name derived from an executable path (macOS: outermost "X.app" bundle, Linux: flatpak/snap id).
    /// When it returns a name, processes sharing it group together even across separate process trees.
    /// </summary>
    public Func<string, string?>? AppFromPath;
}

public static class Platforms
{
    public static IPlatform Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPlatform();
        if (OperatingSystem.IsMacOS()) return new MacPlatform();
        if (OperatingSystem.IsLinux()) return new LinuxPlatform();
        throw new PlatformNotSupportedException("PerfWidget supports Windows, macOS and Linux.");
    }
}
