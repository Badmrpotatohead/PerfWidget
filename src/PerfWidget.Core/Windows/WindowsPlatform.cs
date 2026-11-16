using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace PerfWidget;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatform
{
    IntPtr buf; int bufLen = 2 << 20;
    IntPtr pfBuf; int pfLen = 16 * 1024;
    long pIdle, pKernel, pUser;
    readonly Pdh pdh = new();
    readonly IntPtr cGpuEng, cGpuMem, cGpuAdapter, cDiskIdle, cDiskBytes, cPagesIn, cPagesOut, cCpuPerf, cCpuFreq, cCpuLimit;
    Dictionary<(int, long), long> prevThreads = new();
    readonly GpuSensors gpuSensors = new();
    readonly CpuTempReader cpuTemp = new();
    readonly long vramTotal;
    readonly string gpuName;
    List<DriveStat> drives = new();
    long drivesStamp = long.MinValue;

    public string Name => "windows";
    public GroupingRules Rules { get; } = BuildRules();
    public string? CpuTempSource => cpuTemp.Source;
    public string CpuTempHint => "needs LibreHardwareMonitor running";

    public WindowsPlatform()
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
        cCpuPerf = pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        cCpuFreq = pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
        cCpuLimit = pdh.Add(@"\Processor Information(_Total)\% Performance Limit");
        (vramTotal, gpuName) = ReadGpuInfo();
    }

    // ------------------------------------------------------------------ processes

    public List<ProcInfo> ReadProcesses(double dtSec)
    {
        double dt100ns = dtSec * 1e7;
        var curThreads = new Dictionary<(int, long), long>(prevThreads.Count + 512);
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

            // SYSTEM_THREAD_INFORMATION[] follows the process entry (0x50 bytes each on x64)
            if (pid != 0)
            {
                double maxT = 0;
                for (int t = 0; t < pi.Threads; t++)
                {
                    IntPtr tp = p + 0x100 + t * 0x50;
                    long cpu = Marshal.ReadInt64(tp, 0x00) + Marshal.ReadInt64(tp, 0x08);
                    long ct = Marshal.ReadInt64(tp, 0x10);
                    int tid = (int)Marshal.ReadIntPtr(tp, 0x30);
                    var k = (tid, ct);
                    curThreads[k] = cpu;
                    if (prevThreads.TryGetValue(k, out var old))
                        maxT = Math.Max(maxT, (cpu - old) / dt100ns * 100);
                }
                pi.MaxThreadPct = Math.Clamp(maxT, 0, 100);
            }

            list.Add(pi);
            if (next == 0) break;
            off += next;
        }
        prevThreads = curThreads;
        return list;
    }

    public void FillThreads(List<ProcInfo> busy, double dtSec) { } // already done from the same snapshot

    public string? CommandLine(ProcInfo p) => p.Pid > 4 ? Native.CommandLine(p.Pid) : null;
    public string? ImagePath(ProcInfo p) => p.Pid > 4 ? Native.ImagePath(p.Pid) : null;

    public string? DescribeImage(string path)
    {
        var fvi = FileVersionInfo.GetVersionInfo(path);
        var desc = fvi.FileDescription?.Trim();
        var prod = fvi.ProductName?.Trim();
        if (!string.IsNullOrEmpty(desc) && desc.Length <= 40) return desc;
        if (!string.IsNullOrEmpty(prod) && prod.Length <= 40 && !prod.Contains("Operating System")) return prod;
        return null;
    }

    // ------------------------------------------------------------------ system

    public void FillSystem(Snapshot s, List<ProcInfo> procs, double dtSec)
    {
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

        Native.GetSystemTimes(out long idleT, out long kernT, out long userT);
        long dIdle = idleT - pIdle, dTotal = (kernT - pKernel) + (userT - pUser);
        pIdle = idleT; pKernel = kernT; pUser = userT;
        s.CpuPct = dTotal > 0 ? Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100) : 0;

        Native.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>());
        long page = (long)pi.PageSize;
        var (pfTotal, pfUsed, pfDrives) = ReadPagefiles();
        if (drivesStamp == long.MinValue || Stopwatch.GetElapsedTime(drivesStamp).TotalSeconds >= 10)
        {
            drives = ReadDrives(pfDrives);
            drivesStamp = Stopwatch.GetTimestamp();
        }

        s.ProcessCount = (int)pi.ProcessCount;
        s.ThreadCount = (int)pi.ThreadCount;
        s.RamTotal = (long)pi.PhysicalTotal * page;
        s.RamAvail = (long)pi.PhysicalAvailable * page;
        s.CommitTotal = (long)pi.CommitTotal * page;
        s.CommitLimit = (long)pi.CommitLimit * page;
        s.Cache = (long)pi.SystemCache * page;
        s.PagefileTotal = pfTotal;
        s.PagefileUsed = pfUsed;
        s.PagesInBps = pdh.Get(cPagesIn) * 4096;
        s.PagesOutBps = pdh.Get(cPagesOut) * 4096;
        s.GpuPct = gpuTotal;
        s.VramUsed = vramUsed;
        s.VramTotal = vramTotal;
        s.GpuName = gpuName;
        s.Disks = disks;
        s.Drives = drives;
        s.CpuBaseMHz = pdh.Get(cCpuFreq);
        s.CpuMHz = s.CpuBaseMHz * pdh.Get(cCpuPerf) / 100;
        double limit = pdh.Get(cCpuLimit);
        s.CpuPerfLimit = limit > 0 ? limit : 100;
        s.CpuTemp = cpuTemp.Read();
        gpuSensors.Fill(s);
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

    // ------------------------------------------------------------------ grouping rules

    static GroupingRules BuildRules()
    {
        var r = new GroupingRules
        {
            Boundary = new()
            {
                "idle", "system", "registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
                "lsass.exe", "explorer.exe", "svchost.exe", "userinit.exe", "sihost.exe", "runtimebroker.exe",
                "windowsterminal.exe", "openconsole.exe", "conhost.exe", "taskhostw.exe",
            },
            Shells = new() { "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "wsl.exe", "nu.exe", "sh.exe", "zsh.exe" },
            PrefixAliases = new[] { ("unrealeditor", "Unreal Engine"), ("ue5", "Unreal Engine"), ("godot", "Godot"), ("blender", "Blender") },
            GenericHosts = new() { "electron.exe", "node.exe", "python.exe", "pythonw.exe", "java.exe", "javaw.exe", "dotnet.exe", "bun.exe", "deno.exe", "nw.exe" },
        };
        var d = r.Aliases;
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
        A("PerfWidget (this monitor)", "perfwidget.exe", "perfwidget.app.exe");
        return r;
    }

    public void Dispose()
    {
        pdh.Dispose();
        Marshal.FreeHGlobal(buf);
        Marshal.FreeHGlobal(pfBuf);
    }
}
