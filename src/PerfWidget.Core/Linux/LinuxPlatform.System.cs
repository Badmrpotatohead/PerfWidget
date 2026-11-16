using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PerfWidget;

public sealed partial class LinuxPlatform
{
    // timers
    long lastDiscover, lastSlow, lastClock, lastTempRead, lastCpuinfo;

    // cpu
    LinuxParse.CpuTimes prevAll;
    LinuxParse.CpuTimes[] prevPer = new LinuxParse.CpuTimes[64];
    double[] cpuBusy = new double[64];           // 0..1 per cpu over the last interval, weights the clock average
    string?[] freqPaths = Array.Empty<string?>();  // per cpu scaling_cur_freq (kHz)
    double baseMHz, cpuMHz;

    // memory
    int overcommit = -1;
    long prevPswpin = -1, prevPswpout = -1;

    // PSI
    bool psiCpu, psiMem, psiIo;

    // disks / drives
    Dictionary<string, (long Sectors, long Ticks)> diskPrev = new();
    List<DriveStat> drives = new();

    // temperature
    List<string> tempPaths = new();
    string? tempSource;
    double? lastTemp;

    // gpu
    GpuSensors? gpu;
    string? nvName;
    string? nvRuntimeStatus;   // NVIDIA PCI device power/runtime_status: don't wake a sleeping laptop dGPU
    string? amdDev, amdName, amdTemp;
    long amdVramTotal;

    static double Sec(long ticks) => ticks / (double)Freq;

    public void FillSystem(Snapshot s, List<ProcInfo> procs, double dtSec)
    {
        if (!(dtSec > 0)) dtSec = 1;
        long now = Stopwatch.GetTimestamp();
        if (Sec(now - lastDiscover) >= 60)
        {
            lastDiscover = now;
            try { Discover(); } catch { }
        }
        bool slow = lastSlow == 0 || Sec(now - lastSlow) >= 10;
        if (slow) lastSlow = now;

        try { FillCpu(s); } catch { }
        try { FillMemory(s, dtSec, slow); } catch { }
        try { FillPressure(s); } catch { }
        try { FillDisks(s, dtSec); } catch { }
        try { if (slow) drives = ReadDrives(); } catch { }
        s.Drives = drives;
        try { FillClock(s, now); } catch { }
        try { FillTemp(s, now); } catch { }
        try { FillGpu(s); } catch { }
    }

    // ------------------------------------------------------------------ cpu

    void FillCpu(Snapshot s)
    {
        if (!fr.TryRead(proc + "/stat", out var data)) return;
        int pos = 0;
        while (pos < data.Length)
        {
            var line = LinuxParse.NextLine(data, ref pos);
            if (!line.StartsWith("cpu"u8)) break; // cpu lines come first; skip the huge "intr" line
            if (!LinuxParse.ParseCpuLine(line, out int cpu, out var t)) continue;
            if (cpu < 0) s.CpuPct = Pct(t, ref prevAll);
            else if (cpu < 8192)
            {
                if (cpu >= prevPer.Length)
                {
                    int n = Math.Max(cpu + 1, prevPer.Length * 2);
                    Array.Resize(ref prevPer, n);
                    Array.Resize(ref cpuBusy, n);
                }
                cpuBusy[cpu] = Pct(t, ref prevPer[cpu]) / 100;
            }
        }
    }

    static double Pct(LinuxParse.CpuTimes cur, ref LinuxParse.CpuTimes prev)
    {
        bool had = prev.Total > 0;
        long dTotal = cur.Total - prev.Total, dBusy = cur.Busy - prev.Busy;
        prev = cur;
        return had && dTotal > 0 ? Math.Clamp(dBusy * 100.0 / dTotal, 0, 100) : 0;
    }

    /// <summary>
    /// Mean scaling_cur_freq weighted by how busy each cpu was, so idle cores parked at 800 MHz don't make a
    /// loaded chip look throttled. Falls back to /proc/cpuinfo "cpu MHz" (VMs, some ARM boards).
    /// </summary>
    void FillClock(Snapshot s, long now)
    {
        s.CpuBaseMHz = baseMHz;
        s.CpuPerfLimit = 100;
        if (lastClock == 0 || Sec(now - lastClock) >= 2)
        {
            lastClock = now;
            double sumF = 0, sumW = 0, sumWF = 0;
            int nF = 0;
            for (int i = 0; i < freqPaths.Length; i++)
            {
                var p = freqPaths[i];
                if (p == null) continue;
                long khz = fr.ReadLong(p);
                if (khz <= 0) continue;
                double mhz = khz / 1000.0, w = i < cpuBusy.Length ? cpuBusy[i] : 0;
                sumF += mhz; nF++;
                sumW += w; sumWF += w * mhz;
            }
            if (nF > 0) cpuMHz = sumW >= 0.05 ? sumWF / sumW : sumF / nF;
            else if (lastCpuinfo == 0 || Sec(now - lastCpuinfo) >= 5)
            {
                lastCpuinfo = now;
                if (fr.TryRead(proc + "/cpuinfo", out var ci)) cpuMHz = LinuxParse.ParseCpuinfoMHz(ci);
            }
        }
        s.CpuMHz = cpuMHz;
    }

    void FillTemp(Snapshot s, long now)
    {
        if (tempPaths.Count == 0) { s.CpuTemp = null; return; }
        if (lastTempRead == 0 || Sec(now - lastTempRead) >= 2)
        {
            lastTempRead = now;
            double? best = null;
            foreach (var p in tempPaths)
            {
                long milli = fr.ReadLong(p);
                if (milli <= 0 || milli >= 150_000) continue;
                double c = milli / 1000.0;
                if (best == null || c > best) best = c;
            }
            lastTemp = best;
        }
        s.CpuTemp = lastTemp;
    }

    // ------------------------------------------------------------------ memory

    void FillMemory(Snapshot s, double dt, bool slow)
    {
        if (fr.TryRead(proc + "/meminfo", out var data))
        {
            var m = LinuxParse.ParseMeminfo(data);
            s.RamTotal = Math.Max(0, m.Total);
            s.RamAvail = Math.Clamp(m.AvailableOrEstimate, 0, s.RamTotal);
            s.Cache = m.Cache;
            s.PagefileTotal = Math.Max(0, m.SwapTotal);
            s.PagefileUsed = m.SwapFree >= 0 ? Math.Max(0, s.PagefileTotal - m.SwapFree) : 0;
            s.CommitTotal = Math.Max(0, m.CommittedAs);
            if (slow || overcommit < 0) overcommit = ReadOvercommit();
            // Only mode 2 enforces CommitLimit; under heuristic overcommit Committed_AS above it is normal.
            s.CommitLimit = overcommit == 2 ? Math.Max(0, m.CommitLimit) : 0;
            swapInUse = s.PagefileUsed > 0;
        }
        s.RamAlertsFromPressureOnly = false;

        if (fr.TryRead(proc + "/vmstat", out var vm))
        {
            LinuxParse.ParseVmstat(vm, out long pin, out long pout);
            if (pin >= 0 && prevPswpin >= 0) s.PagesInBps = Math.Max(0, pin - prevPswpin) * (double)pageSize / dt;
            if (pout >= 0 && prevPswpout >= 0) s.PagesOutBps = Math.Max(0, pout - prevPswpout) * (double)pageSize / dt;
            prevPswpin = pin;
            prevPswpout = pout;
        }
    }

    int ReadOvercommit()
    {
        var t = fr.ReadText(proc + "/sys/vm/overcommit_memory");
        return t == "2" ? 2 : t == "1" ? 1 : 0;
    }

    void FillPressure(Snapshot s)
    {
        s.CpuStallPct = psiCpu ? Psi("cpu", out _) : null;
        s.IoStallPct = psiIo ? Psi("io", out _) : null;
        double? full = null;
        s.MemStallPct = psiMem ? Psi("memory", out full) : null;
        s.MemoryPressure = s.MemStallPct is double some ? LinuxParse.MemoryPressure(some, full) : null;
    }

    double? Psi(string resource, out double? full)
    {
        full = null;
        if (!fr.TryRead(proc + "/pressure/" + resource, out var d) || !LinuxParse.ParsePsi(d, out double some, out full))
        {
            // e.g. EOPNOTSUPP: stop trying until the next discovery pass
            if (resource == "cpu") psiCpu = false; else if (resource == "io") psiIo = false; else psiMem = false;
            return null;
        }
        return some;
    }

    // ------------------------------------------------------------------ disks

    void FillDisks(Snapshot s, double dt)
    {
        if (!fr.TryRead(proc + "/diskstats", out var data)) return;
        var phys = new List<DiskStat>();
        var virt = new List<DiskStat>();
        var seen = new Dictionary<string, (long Sectors, long Ticks)>(diskPrev.Count + 4);
        int pos = 0;
        while (pos < data.Length)
        {
            var line = LinuxParse.NextLine(data, ref pos);
            if (!LinuxParse.ParseDiskstats(line, out int ns, out int nl, out long sectors, out long ticks)) continue;
            var nameSpan = line.Slice(ns, nl);
            int kind = LinuxParse.DiskKind(nameSpan);
            if (kind == 0) continue;
            string name = Encoding.ASCII.GetString(nameSpan);
            double busy = 0, bps = 0;
            if (diskPrev.TryGetValue(name, out var o))
            {
                busy = Math.Clamp((ticks - o.Ticks) / (dt * 1000) * 100, 0, 100);
                bps = Math.Max(0, sectors - o.Sectors) * 512.0 / dt;
            }
            seen[name] = (sectors, ticks);
            (kind == 1 ? phys : virt).Add(new DiskStat(name, busy, bps));
        }
        diskPrev = seen;
        // device-mapper / md only when there is nothing physical to show (some VMs, LVM-only views)
        s.Disks = phys.Count > 0 ? phys : virt;
    }

    // ------------------------------------------------------------------ drives (filesystems)

    static readonly HashSet<string> RealFs = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "f2fs", "zfs", "ntfs", "ntfs3", "vfat", "exfat", "apfs", "hfsplus",
        "fuseblk", "bcachefs", "jfs",
    };

    List<DriveStat> ReadDrives()
    {
        var text = fr.ReadText(proc + "/self/mounts");
        if (text == null) return drives;
        var swapFiles = LinuxParse.ParseSwapFiles(fr.ReadText(proc + "/swaps") ?? "");

        var cands = new List<(string Mp, string Key, long Free, long Total)>();
        foreach (var m in LinuxParse.ParseMounts(text))
        {
            try
            {
                if (!RealFs.Contains(m.Type)) continue;
                string mp = m.MountPoint;
                if (mp.StartsWith("/snap/", StringComparison.Ordinal) || mp.StartsWith("/var/snap/", StringComparison.Ordinal)
                    || mp.StartsWith("/var/lib/snapd/", StringComparison.Ordinal)) continue;
                if (!Directory.Exists(mp)) continue; // file bind mounts (containers' /etc/hosts)
                var di = new DriveInfo(mp);
                long total = di.TotalSize, free = di.AvailableFreeSpace;
                if (total <= 0) continue;
                if (m.Type == "vfat" && total < 2 * (long)Fmt.GiB
                    && (mp == "/boot/efi" || mp == "/efi" || mp == "/boot" || mp.StartsWith("/boot/", StringComparison.Ordinal))) continue;
                if (IsRemovable(m.Source)) continue;
                // one entry per filesystem: btrfs subvolumes and bind mounts share the device and size,
                // ZFS datasets share their pool's free space
                string key = m.Type == "zfs" ? "zfs:" + m.Source.Split('/')[0] : m.Source + "|" + total;
                cands.Add((mp, key, free, total));
            }
            catch { }
        }

        var pageKeys = new HashSet<string>();
        foreach (var sf in swapFiles)
        {
            int bestLen = -1;
            string? bestKey = null;
            foreach (var c in cands)
                if (IsUnder(sf, c.Mp) && c.Mp.Length > bestLen) { bestLen = c.Mp.Length; bestKey = c.Key; }
            if (bestKey != null) pageKeys.Add(bestKey);
        }

        cands.Sort((a, b) => a.Mp.Length != b.Mp.Length ? a.Mp.Length - b.Mp.Length : string.CompareOrdinal(a.Mp, b.Mp));
        var list = new List<DriveStat>();
        var keys = new HashSet<string>();
        foreach (var c in cands)
            if (keys.Add(c.Key)) list.Add(new DriveStat(c.Mp, c.Free, c.Total, pageKeys.Contains(c.Key)));
        return list;
    }

    static bool IsUnder(string path, string mp) =>
        mp == "/" ? path.StartsWith('/') : path == mp || path.StartsWith(mp + "/", StringComparison.Ordinal);

    /// <summary>USB sticks and SD cards (sysfs "removable" of the disk) aren't drives to warn about.</summary>
    bool IsRemovable(string source)
    {
        if (!source.StartsWith("/dev/", StringComparison.Ordinal)) return false;
        string blk = sys + "/class/block/" + LinuxParse.BaseName(source);
        if (File.Exists(blk + "/removable")) return fr.ReadLong(blk + "/removable") == 1;
        // a partition: its disk is the parent directory of the sysfs device ("…/block/sdb/sdb1")
        string? target = null;
        try { target = new FileInfo(blk).LinkTarget; } catch { }
        if (target == null) return false;
        var parts = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        string parent = sys + "/class/block/" + parts[^2] + "/removable";
        return File.Exists(parent) && fr.ReadLong(parent) == 1;
    }

    // ------------------------------------------------------------------ gpu

    void FillGpu(Snapshot s)
    {
        if (gpu != null && gpu.Available)
        {
            s.GpuName = nvName ??= gpu.Name() ?? "NVIDIA GPU";
            if (nvRuntimeStatus != null && fr.ReadText(nvRuntimeStatus) == "suspended") return; // asleep: 0 %, don't wake it
            if (gpu.ReadUtilization(out double pct, out long used, out long total))
            {
                s.GpuPct = Math.Clamp(pct, 0, 100);
                s.VramUsed = used;
                s.VramTotal = total;
            }
            gpu.Fill(s);
            return;
        }
        if (amdDev == null) return;
        s.GpuName = amdName ?? "AMD GPU";
        if (fr.ReadText(amdDev + "/power/runtime_status") == "suspended") return; // reading would wake it
        long busy = fr.ReadLong(amdDev + "/gpu_busy_percent");
        if (busy >= 0) s.GpuPct = Math.Clamp(busy, 0, 100);
        if (amdVramTotal > 0)
        {
            long used = fr.ReadLong(amdDev + "/mem_info_vram_used");
            if (used >= 0) { s.VramUsed = used; s.VramTotal = amdVramTotal; }
        }
        if (amdTemp != null)
        {
            long t = fr.ReadLong(amdTemp);
            if (t > 0) s.GpuTemp = t / 1000.0;
        }
    }

    // ------------------------------------------------------------------ discovery (every 60 s)

    void Discover()
    {
        try { DiscoverCpuFreq(); } catch { }
        try { DiscoverCpuTemp(); } catch { }
        try { DiscoverGpu(); } catch { }
        psiCpu = File.Exists(proc + "/pressure/cpu");
        psiMem = File.Exists(proc + "/pressure/memory");
        psiIo = File.Exists(proc + "/pressure/io");
    }

    static IEnumerable<string> Entries(string dir, string pattern = "*")
    {
        try { return Directory.EnumerateFileSystemEntries(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    static List<string> Collect(IEnumerable<string> e)
    {
        var l = new List<string>();
        try { foreach (var x in e) l.Add(x); } catch { }
        return l;
    }

    static string Unix(string p) => p.Replace('\\', '/'); // the fake-tree tests run on Windows

    void DiscoverCpuFreq()
    {
        string root = sys + "/devices/system/cpu";
        var paths = new List<(int Cpu, string Path)>();
        int max = -1;
        foreach (var e in Collect(Entries(root, "cpu*")))
        {
            int n = NumberAfter(Path.GetFileName(e), "cpu"); // "cpu12" -> 12; "cpufreq", "cpuidle" -> -1
            if (n < 0) continue;
            string p = root + "/cpu" + n + "/cpufreq/scaling_cur_freq";
            if (!File.Exists(p)) continue;
            paths.Add((n, p));
            max = Math.Max(max, n);
        }
        var arr = new string?[max + 1];
        foreach (var (n, p) in paths) arr[n] = p;
        freqPaths = arr;

        // A true base / nominal clock only. Never cpuinfo_max_freq: that's the turbo ceiling, and comparing
        // against it would report "throttling" whenever the chip isn't boosting.
        baseMHz = 0;
        foreach (var (n, _) in paths)
        {
            string d = root + "/cpu" + n + "/cpufreq/";
            long khz = File.Exists(d + "base_frequency") ? fr.ReadLong(d + "base_frequency")
                     : File.Exists(d + "amd_pstate_nominal_freq") ? fr.ReadLong(d + "amd_pstate_nominal_freq") : -1;
            if (khz > 0) { baseMHz = khz / 1000.0; break; }
        }
    }

    void DiscoverCpuTemp()
    {
        var sensors = new List<LinuxParse.TempSensor>();
        foreach (var e in Collect(Entries(sys + "/class/hwmon", "hwmon*")))
        {
            string dir = Unix(e);
            string? chip = fr.ReadText(dir + "/name");
            if (chip == null || LinuxParse.TempChipRank(chip) == 0) continue;
            foreach (var f in Collect(Entries(dir, "temp*_input")))
            {
                string input = Unix(f);
                string labelPath = input.Substring(0, input.Length - "_input".Length) + "_label";
                string? label = File.Exists(labelPath) ? fr.ReadText(labelPath) : null;
                sensors.Add(new LinuxParse.TempSensor(chip, label, input));
            }
        }
        foreach (var e in Collect(Entries(sys + "/class/thermal", "thermal_zone*")))
        {
            string dir = Unix(e);
            string? type = fr.ReadText(dir + "/type");
            if (type != null && LinuxParse.TempChipRank("zone:" + type) > 0 && File.Exists(dir + "/temp"))
                sensors.Add(new LinuxParse.TempSensor("zone:" + type, null, dir + "/temp"));
        }
        tempPaths = LinuxParse.ChooseCpuTemp(sensors, out tempSource);
    }

    void DiscoverGpu()
    {
        if (gpu != null && gpu.Available)
        {
            // NVIDIA: find its PCI device to check runtime power state before each NVML query
            nvRuntimeStatus = null;
            foreach (var e in Collect(Entries(sys + "/bus/pci/devices")))
            {
                string dir = Unix(e);
                if (fr.ReadText(dir + "/vendor") != "0x10de") continue;
                var cls = fr.ReadText(dir + "/class");
                if (cls == null || !cls.StartsWith("0x03", StringComparison.Ordinal)) continue;
                if (File.Exists(dir + "/power/runtime_status")) { nvRuntimeStatus = dir + "/power/runtime_status"; break; }
            }
            return;
        }

        // AMD (amdgpu): the card with the most VRAM, so a discrete GPU wins over the iGPU
        string? bestDev = null;
        long bestVram = -1;
        foreach (var e in Collect(Entries(sys + "/class/drm", "card*")))
        {
            string name = Path.GetFileName(e);
            if (NumberAfter(name, "card") < 0) continue; // skip connectors such as "card0-DP-1"
            string dev = Unix(e) + "/device";
            if (!File.Exists(dev + "/gpu_busy_percent")) continue;
            long vram = File.Exists(dev + "/mem_info_vram_total") ? fr.ReadLong(dev + "/mem_info_vram_total") : 0;
            if (vram > bestVram) { bestVram = vram; bestDev = dev; }
        }
        amdDev = bestDev;
        if (bestDev == null) { amdName = null; amdTemp = null; amdVramTotal = 0; return; }
        // an APU's small carve-out "full" is normal (it spills into GTT): only report VRAM for real VRAM
        amdVramTotal = bestVram >= 2 * (long)Fmt.GiB ? bestVram : 0;
        string? product = File.Exists(bestDev + "/product_name") ? fr.ReadText(bestDev + "/product_name") : null;
        amdName = string.IsNullOrWhiteSpace(product) ? "AMD GPU" : product;
        amdTemp = null;
        foreach (var h in Collect(Entries(bestDev + "/hwmon", "hwmon*")))
        {
            string t = Unix(h) + "/temp1_input";
            if (File.Exists(t)) { amdTemp = t; break; }
        }
    }

    static int NumberAfter(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length) return -1;
        int v = 0;
        for (int i = prefix.Length; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i]) || v > 100000) return -1;
            v = v * 10 + (name[i] - '0');
        }
        return v;
    }
}
