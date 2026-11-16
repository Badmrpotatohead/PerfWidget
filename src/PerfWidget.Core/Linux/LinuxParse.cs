using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Text;

namespace PerfWidget;

/// <summary>
/// Pure parsers for Linux /proc and /sys text: no I/O, and no allocation on the per-process paths.
/// Everything here can be desk-checked on any OS.
/// </summary>
internal static class LinuxParse
{
    /// <summary>task_struct flag (stat field 9): this is a kernel thread.</summary>
    public const uint PF_KTHREAD = 0x00200000;
    /// <summary>TASK_COMM_LEN - 1. A comm of exactly this many bytes was probably cut.</summary>
    public const int CommMax = 15;

    // ------------------------------------------------------------------ tokens

    static bool IsWs(byte b) => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r';

    /// <summary>Next whitespace-separated token at or after <paramref name="i"/>; i ends just past it.</summary>
    internal static bool NextToken(ReadOnlySpan<byte> s, ref int i, out ReadOnlySpan<byte> tok)
    {
        while (i < s.Length && IsWs(s[i])) i++;
        if (i >= s.Length) { tok = default; return false; }
        int start = i;
        while (i < s.Length && !IsWs(s[i])) i++;
        tok = s.Slice(start, i - start);
        return true;
    }

    /// <summary>Next line (without the '\n') starting at pos; pos moves past it.</summary>
    internal static ReadOnlySpan<byte> NextLine(ReadOnlySpan<byte> s, ref int pos)
    {
        var tail = s.Slice(pos);
        int nl = tail.IndexOf((byte)'\n');
        if (nl < 0) { pos = s.Length; return tail; }
        pos += nl + 1;
        return tail.Slice(0, nl);
    }

    /// <summary>Leading (optionally negative) decimal integer, skipping leading whitespace. Saturates on overflow.</summary>
    internal static long ToLong(ReadOnlySpan<byte> t)
    {
        int i = 0;
        while (i < t.Length && IsWs(t[i])) i++;
        bool neg = false;
        if (i < t.Length && t[i] == (byte)'-') { neg = true; i++; }
        long v = 0;
        for (; i < t.Length; i++)
        {
            uint d = (uint)(t[i] - (byte)'0');
            if (d > 9) break;
            if (v > (long.MaxValue - 9) / 10) return neg ? long.MinValue : long.MaxValue;
            v = v * 10 + d;
        }
        return neg ? -v : v;
    }

    static bool AllDigits(ReadOnlySpan<byte> t)
    {
        if (t.Length == 0) return false;
        foreach (byte b in t) if ((uint)(b - (byte)'0') > 9) return false;
        return true;
    }

    /// <summary>
    /// Value of the line "key: 123" / "key 123" (the key must be followed by ':' or whitespace), or -1.
    /// Used for /proc/[pid]/io, /proc/[pid]/status and /proc/vmstat.
    /// </summary>
    internal static long LineValue(ReadOnlySpan<byte> s, ReadOnlySpan<byte> key)
    {
        int pos = 0;
        while (pos < s.Length)
        {
            var line = NextLine(s, ref pos);
            if (line.Length > key.Length && line.StartsWith(key))
            {
                byte sep = line[key.Length];
                if (sep == (byte)':' || IsWs(sep))
                {
                    var v = line.Slice(key.Length).TrimStart(" \t:"u8);
                    return v.Length > 0 && (uint)(v[0] - (byte)'0') <= 9 ? ToLong(v) : -1;
                }
            }
        }
        return -1;
    }

    // ------------------------------------------------------------------ /proc/[pid]/stat

    /// <summary>The fields of /proc/[pid]/stat we use. Field numbers below are man proc(5)'s, 1-based.</summary>
    internal struct Stat
    {
        public int Pid;          // (1)
        public int CommStart;    // (2) byte range of comm in the parsed text, between the first '(' and the LAST ')'
        public int CommLength;
        public char State;       // (3)
        public int Ppid;         // (4)
        public uint Flags;       // (9)
        public long MajFlt;      // (12)
        public long UTime;       // (14) clock ticks
        public long STime;       // (15) clock ticks
        public int Threads;      // (20)
        public long StartTime;   // (22) clock ticks after boot
        public long Rss;         // (24) pages
    }

    /// <summary>
    /// Parses /proc/[pid]/stat (also /proc/[pid]/task/[tid]/stat). comm may contain spaces and ')',
    /// so the fixed fields start after the LAST ')'.
    /// </summary>
    internal static bool ParseStat(ReadOnlySpan<byte> s, out Stat st)
    {
        st = default;
        int open = s.IndexOf((byte)'(');
        int close = s.LastIndexOf((byte)')');
        if (open <= 0 || close < open) return false;
        var pidTok = s.Slice(0, open).Trim((byte)' ');
        if (!AllDigits(pidTok)) return false;
        st.Pid = (int)ToLong(pidTok);
        st.CommStart = open + 1;
        st.CommLength = close - open - 1;

        // token k after ')' is field k + 3
        int i = close + 1, k = 0;
        while (NextToken(s, ref i, out var t))
        {
            switch (k)
            {
                case 0: st.State = (char)t[0]; break;
                case 1: st.Ppid = (int)ToLong(t); break;
                case 6: st.Flags = (uint)ToLong(t); break;
                case 9: st.MajFlt = ToLong(t); break;
                case 11: st.UTime = ToLong(t); break;
                case 12: st.STime = ToLong(t); break;
                case 17: st.Threads = (int)ToLong(t); break;
                case 19: st.StartTime = ToLong(t); break;
                case 21: st.Rss = ToLong(t); return true;
            }
            k++;
        }
        return k > 19; // starttime is the last field we can't do without
    }

    // ------------------------------------------------------------------ /proc/[pid]/statm, io, status

    /// <summary>/proc/[pid]/statm: "size resident shared text lib data dt", in pages.</summary>
    internal static bool ParseStatm(ReadOnlySpan<byte> s, out long resident, out long shared)
    {
        resident = shared = 0;
        int i = 0;
        if (!NextToken(s, ref i, out _) || !NextToken(s, ref i, out var r) || !NextToken(s, ref i, out var sh)) return false;
        resident = ToLong(r);
        shared = ToLong(sh);
        return true;
    }

    /// <summary>/proc/[pid]/io read_bytes + write_bytes: bytes that really went to or came from storage.</summary>
    internal static bool ParseIo(ReadOnlySpan<byte> s, out long readBytes, out long writeBytes)
    {
        readBytes = LineValue(s, "read_bytes"u8);
        writeBytes = LineValue(s, "write_bytes"u8);
        return readBytes >= 0 && writeBytes >= 0;
    }

    /// <summary>/proc/[pid]/status "VmSwap:  123 kB", in bytes; -1 if absent (kernel threads).</summary>
    internal static long ParseVmSwap(ReadOnlySpan<byte> s)
    {
        long kb = LineValue(s, "VmSwap"u8);
        return kb < 0 ? -1 : kb * 1024;
    }

    // ------------------------------------------------------------------ /proc/[pid]/cmdline

    /// <summary>NUL-separated argv. Empty arguments (padding left by setproctitle) are dropped. Null when empty (kernel threads, zombies).</summary>
    internal static List<string>? SplitCmdline(ReadOnlySpan<byte> s)
    {
        List<string>? list = null;
        while (s.Length > 0)
        {
            int z = s.IndexOf((byte)0);
            var arg = z < 0 ? s : s.Slice(0, z);
            if (arg.Length > 0) (list ??= new List<string>()).Add(Encoding.UTF8.GetString(arg));
            if (z < 0) break;
            s = s.Slice(z + 1);
        }
        return list;
    }

    /// <summary>
    /// argv as one line for <see cref="CmdLine"/>. A single argument containing spaces is a process title
    /// set with setproctitle ("postgres: checkpointer", "sshd: ann@pts/0", "npm run dev") and is returned as-is
    /// so it splits into words, unless <paramref name="singleArgIsPath"/> says it is a real path with spaces.
    /// </summary>
    internal static string? JoinCmdline(List<string>? argv, bool singleArgIsPath)
    {
        if (argv == null || argv.Count == 0) return null;
        if (argv.Count == 1 && !singleArgIsPath && HasWhitespace(argv[0])) return argv[0].Trim();
        return CmdLine.Join(argv);
    }

    internal static bool HasWhitespace(string s)
    {
        foreach (char c in s) if (char.IsWhiteSpace(c)) return true;
        return false;
    }

    // ------------------------------------------------------------------ naming

    internal static string BaseName(string path)
    {
        int s = path.LastIndexOf('/');
        return s >= 0 ? path.Substring(s + 1) : path;
    }

    /// <summary>
    /// comm is cut to 15 bytes. When it looks cut, the full name is the basename of argv[0], argv[1]
    /// (scripts: argv is "interpreter script ...") or the executable that starts with it.
    /// A comm the process set itself ("Isolated Web Co") matches none of them and is kept.
    /// </summary>
    internal static string PickName(string comm, IReadOnlyList<string>? argv, string? exe)
    {
        string? c;
        if (argv != null)
        {
            if (argv.Count > 0 && (c = Longer(comm, BaseName(argv[0]))) != null) return c;
            if (argv.Count > 1 && !argv[1].StartsWith('-') && (c = Longer(comm, BaseName(argv[1]))) != null) return c;
        }
        if (exe != null && (c = Longer(comm, BaseName(exe))) != null) return c;
        return comm;
    }

    static string? Longer(string comm, string cand)
    {
        cand = cand.TrimEnd();
        return cand.Length > comm.Length && cand.StartsWith(comm, StringComparison.Ordinal) ? cand : null;
    }

    /// <summary>NixOS wrappers: ".firefox-wrapped" (or cut: ".firefox-wrappe") is "firefox".</summary>
    internal static string CleanName(string n)
    {
        if (n.Length > 2 && n[0] == '.')
        {
            int w = n.IndexOf("-wrap", StringComparison.Ordinal);
            if (w > 1)
            {
                var tail = n.AsSpan(w);
                if (tail.StartsWith("-wrapped", StringComparison.Ordinal) || "-wrapped".AsSpan().StartsWith(tail, StringComparison.Ordinal))
                {
                    var core = n.Substring(1, w - 1).TrimStart('.');
                    if (core.Length > 0) return core;
                }
            }
        }
        return n;
    }

    static readonly HashSet<string> GenericIdParts = new(StringComparer.OrdinalIgnoreCase)
        { "client", "desktop", "studio", "app", "application", "gui", "browser" };

    static readonly Dictionary<string, string> FlatpakNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["com.obsproject.Studio"] = "OBS Studio",
    };

    /// <summary>
    /// App name from where an executable lives, or null:
    /// Flatpak ".../flatpak/app/org.mozilla.firefox/..." -> "Firefox";
    /// Snap "/snap/spotify/123/..." -> "Spotify";
    /// AppImage "/tmp/.mount_ObsidiAbC123/obsidian" -> "Obsidian".
    /// Names that are alias keys map to the alias ("code" -> "VS Code") so every process of an app agrees.
    /// </summary>
    internal static string? AppName(string? path, IReadOnlyDictionary<string, string>? aliases)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int i = path.IndexOf("/flatpak/app/", StringComparison.Ordinal);
        if (i >= 0)
        {
            string id = Segment(path, i + "/flatpak/app/".Length);
            return id.Length > 0 ? FlatpakAppName(id, aliases) : null;
        }
        if (path.StartsWith("/snap/", StringComparison.Ordinal))
        {
            string n = Segment(path, "/snap/".Length);
            return IsRuntimeSnap(n) ? null : Known(n, aliases);
        }
        i = path.IndexOf("/.mount_", StringComparison.Ordinal);
        if (i >= 0) return AppImageName(path, i + "/.mount_".Length, aliases);
        return null;
    }

    static string Segment(string p, int start)
    {
        if (start >= p.Length) return "";
        int e = p.IndexOf('/', start);
        return e < 0 ? p.Substring(start) : p.Substring(start, e - start);
    }

    /// <summary>Flatpak id -> name: last part of the reverse-DNS id, or the one before a generic last part (com.spotify.Client).</summary>
    internal static string? FlatpakAppName(string id, IReadOnlyDictionary<string, string>? aliases)
    {
        if (FlatpakNames.TryGetValue(id, out var fixedName)) return fixedName;
        var parts = id.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        string last = parts[^1];
        if (parts.Length >= 2 && GenericIdParts.Contains(last)) last = parts[^2];
        return Known(last, aliases);
    }

    /// <summary>Base and content snaps hold shared runtimes (a launcher's bash, mesa), not apps.</summary>
    static bool IsRuntimeSnap(string n)
    {
        if (n.Length == 0 || n is "bin" or "snapd" or "bare" or "gtk-common-themes") return true;
        if (n.StartsWith("core", StringComparison.Ordinal) && (n.Length == 4 || AllDigits(n.AsSpan(4)))) return true;
        if (n.StartsWith("gnome-", StringComparison.Ordinal) && n.Length > 6 && char.IsAsciiDigit(n[6])) return true;
        return n.StartsWith("kf5-", StringComparison.Ordinal) || n.StartsWith("kf6-", StringComparison.Ordinal)
            || n.StartsWith("mesa-", StringComparison.Ordinal) || n.StartsWith("gpu-", StringComparison.Ordinal)
            || n.StartsWith("kde-frameworks", StringComparison.Ordinal);
    }

    static bool AllDigits(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s) if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// The AppImage runtime mounts at ".mount_" + the first 6 characters of the file name + 6 random ones.
    /// A path component inside the mount that starts with that prefix carries the full name.
    /// </summary>
    static string? AppImageName(string path, int start, IReadOnlyDictionary<string, string>? aliases)
    {
        string mount = Segment(path, start);
        string? prefix = mount.Length > 6 ? mount.Substring(0, mount.Length - 6).TrimEnd('-', '_', '.', ' ') : null;
        if (string.IsNullOrEmpty(prefix)) prefix = null;
        int restAt = start + mount.Length;
        if (prefix != null && restAt < path.Length)
            foreach (var comp in path.Substring(restAt).Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (comp.Length >= prefix.Length && comp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return Known(comp, aliases);
        if (prefix != null) return Known(prefix, aliases);
        string b = BaseName(path);
        return b.Length > 0 && b != mount ? Known(b, aliases) : null;
    }

    static string Known(string n, IReadOnlyDictionary<string, string>? aliases)
    {
        if (aliases != null && aliases.TryGetValue(n.ToLowerInvariant(), out var a)) return a;
        return Capitalize(n);
    }

    internal static string Capitalize(string s) =>
        s.Length == 0 || char.IsUpper(s[0]) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    static readonly string[] DecorSuffixes = { ".bin", "-bin", ".real", ".AppImage", ".appimage", ".exe" };

    /// <summary>
    /// <see cref="IPlatform.DescribeImage"/>: a nicer name than the process name, or null when the path adds
    /// nothing (then the process name, already un-truncated, is used). Wine and other loaders keep their comm.
    /// </summary>
    internal static string? PrettyImageName(string? path, IReadOnlyDictionary<string, string>? aliases)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var app = AppName(path, aliases);
        if (app != null) return app;
        string orig = BaseName(path), b = orig;
        foreach (var suf in DecorSuffixes)
            if (b.Length > suf.Length && b.EndsWith(suf, StringComparison.Ordinal)) { b = b.Substring(0, b.Length - suf.Length); break; }
        b = CleanName(b);
        // "/opt/Obsidian/obsidian": the folder carries the brand's capitalisation
        int slash = path.LastIndexOf('/');
        if (slash > 0)
        {
            int prev = path.LastIndexOf('/', slash - 1);
            string dir = path.Substring(prev + 1, slash - prev - 1);
            if (dir != b && dir.Equals(b, StringComparison.OrdinalIgnoreCase)) b = dir;
        }
        return b == orig || b.Length == 0 ? null : b;
    }

    /// <summary>/proc/[pid]/root/.flatpak-info: [Instance] app-path (host path of /app) and [Application] name.</summary>
    internal static (string? AppPath, string? Name) ParseFlatpakInfo(string text)
    {
        string? appPath = null, name = null, section = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line; continue; }
            if (section == "[Application]" && line.StartsWith("name=", StringComparison.Ordinal)) name = line.Substring(5).Trim();
            else if (section == "[Instance]" && line.StartsWith("app-path=", StringComparison.Ordinal)) appPath = line.Substring(9).Trim();
        }
        return (string.IsNullOrEmpty(appPath) ? null : appPath, string.IsNullOrEmpty(name) ? null : name);
    }

    // ------------------------------------------------------------------ /proc/stat, meminfo, vmstat, PSI

    internal struct CpuTimes { public long Busy, Total; }

    /// <summary>
    /// One "cpu" / "cpuN" line of /proc/stat: user nice system idle iowait irq softirq steal guest guest_nice.
    /// Total = the first eight (guest and guest_nice are already inside user and nice); idle = idle + iowait.
    /// cpu = -1 for the aggregate line.
    /// </summary>
    internal static bool ParseCpuLine(ReadOnlySpan<byte> line, out int cpu, out CpuTimes t)
    {
        cpu = -2; t = default;
        if (!line.StartsWith("cpu"u8)) return false;
        int i = 0;
        if (!NextToken(line, ref i, out var name)) return false;
        if (name.Length == 3) cpu = -1;
        else if (AllDigits(name.Slice(3))) cpu = (int)ToLong(name.Slice(3));
        else return false;
        long total = 0, idle = 0;
        int k = 0;
        while (k < 8 && NextToken(line, ref i, out var tok))
        {
            long v = ToLong(tok);
            total += v;
            if (k == 3 || k == 4) idle += v;
            k++;
        }
        if (k < 4) return false;
        t = new CpuTimes { Busy = total - idle, Total = total };
        return true;
    }

    /// <summary>/proc/meminfo, in bytes. Missing fields are -1.</summary>
    internal struct MemInfo
    {
        public long Total, Free, Available, Buffers, Cached, Shmem, SwapTotal, SwapFree, CommittedAs, CommitLimit;

        /// <summary>MemAvailable, or the pre-3.14 estimate free + buffers + cache - shmem.</summary>
        public readonly long AvailableOrEstimate =>
            Available >= 0 ? Available : Math.Max(0, Pos(Free) + Pos(Buffers) + Pos(Cached) - Pos(Shmem));

        /// <summary>Reclaimable file cache: Cached + Buffers - Shmem (tmpfs/shared memory is in Cached but can't be dropped).</summary>
        public readonly long Cache => Math.Max(0, Pos(Cached) + Pos(Buffers) - Pos(Shmem));

        static long Pos(long v) => v < 0 ? 0 : v;
    }

    internal static MemInfo ParseMeminfo(ReadOnlySpan<byte> s)
    {
        var m = new MemInfo
        {
            Total = -1, Free = -1, Available = -1, Buffers = -1, Cached = -1, Shmem = -1,
            SwapTotal = -1, SwapFree = -1, CommittedAs = -1, CommitLimit = -1,
        };
        int pos = 0;
        while (pos < s.Length)
        {
            var line = NextLine(s, ref pos);
            int c = line.IndexOf((byte)':');
            if (c <= 0) continue;
            var k = line.Slice(0, c);
            long v = ToLong(line.Slice(c + 1)) * 1024; // kB
            if (k.SequenceEqual("MemTotal"u8)) m.Total = v;
            else if (k.SequenceEqual("MemFree"u8)) m.Free = v;
            else if (k.SequenceEqual("MemAvailable"u8)) m.Available = v;
            else if (k.SequenceEqual("Buffers"u8)) m.Buffers = v;
            else if (k.SequenceEqual("Cached"u8)) m.Cached = v;
            else if (k.SequenceEqual("Shmem"u8)) m.Shmem = v;
            else if (k.SequenceEqual("SwapTotal"u8)) m.SwapTotal = v;
            else if (k.SequenceEqual("SwapFree"u8)) m.SwapFree = v;
            else if (k.SequenceEqual("CommitLimit"u8)) m.CommitLimit = v;
            else if (k.SequenceEqual("Committed_AS"u8)) m.CommittedAs = v;
        }
        return m;
    }

    /// <summary>/proc/vmstat pswpin / pswpout: cumulative pages read from / written to swap (-1 if absent).</summary>
    internal static void ParseVmstat(ReadOnlySpan<byte> s, out long pswpin, out long pswpout)
    {
        pswpin = LineValue(s, "pswpin"u8);
        pswpout = LineValue(s, "pswpout"u8);
    }

    /// <summary>/proc/pressure/{cpu,memory,io}: "some avg10=1.23 avg60=… total=…" and "full avg10=…".</summary>
    internal static bool ParsePsi(ReadOnlySpan<byte> s, out double some10, out double? full10)
    {
        some10 = 0; full10 = null;
        bool any = false;
        int pos = 0;
        while (pos < s.Length)
        {
            var line = NextLine(s, ref pos);
            bool some = line.StartsWith("some "u8), full = line.StartsWith("full "u8);
            if (!some && !full) continue;
            int a = line.IndexOf("avg10="u8);
            if (a < 0 || !Utf8Parser.TryParse(line.Slice(a + 6), out double v, out _)) continue;
            if (some) { some10 = v; any = true; }
            else full10 = v;
        }
        return any;
    }

    /// <summary>Linux PSI verdict: 2 critical when memory "full" avg10 >= 10 % or "some" >= 40 %, 1 when "some" >= 10 %.</summary>
    internal static int MemoryPressure(double some10, double? full10) =>
        (full10 ?? 0) >= 10 || some10 >= 40 ? 2 : some10 >= 10 ? 1 : 0;

    /// <summary>Average of the "cpu MHz : 3400.000" lines of /proc/cpuinfo, or 0.</summary>
    internal static double ParseCpuinfoMHz(ReadOnlySpan<byte> s)
    {
        double sum = 0;
        int n = 0, pos = 0;
        while (pos < s.Length)
        {
            var line = NextLine(s, ref pos);
            if (!line.StartsWith("cpu MHz"u8)) continue;
            int c = line.IndexOf((byte)':');
            if (c < 0) continue;
            var v = line.Slice(c + 1).TrimStart(" \t"u8);
            if (Utf8Parser.TryParse(v, out double mhz, out _) && mhz > 0) { sum += mhz; n++; }
        }
        return n > 0 ? sum / n : 0;
    }

    // ------------------------------------------------------------------ /proc/diskstats

    /// <summary>
    /// One /proc/diskstats line: major minor name, then reads, reads merged, sectors read (field 6), ms reading,
    /// writes, writes merged, sectors written (field 10), ms writing, in flight, io_ticks ms (field 13), …
    /// Kernels 4.18+ add 4 discard fields, 5.5+ 2 flush fields (20 in all); only the first 13 are used.
    /// </summary>
    internal static bool ParseDiskstats(ReadOnlySpan<byte> line, out int nameStart, out int nameLength, out long sectors, out long ioTicksMs)
    {
        nameStart = nameLength = 0; sectors = ioTicksMs = 0;
        int i = 0, k = 0;
        long rd = 0, wr = 0;
        while (NextToken(line, ref i, out var t))
        {
            switch (k)
            {
                case 2: nameStart = i - t.Length; nameLength = t.Length; break;
                case 5: rd = ToLong(t); break;
                case 9: wr = ToLong(t); break;
                case 12: ioTicksMs = ToLong(t); sectors = rd + wr; return nameLength > 0;
            }
            k++;
        }
        return false;
    }

    /// <summary>1 = whole physical disk (sdX, vdX, xvdX, hdX, nvmeXnY, mmcblkX), 2 = virtual (dm-X, mdX), 0 = skip (partitions, loop, ram, zram, sr…).</summary>
    internal static int DiskKind(ReadOnlySpan<byte> n)
    {
        if (LettersAfter(n, "sd"u8) || LettersAfter(n, "vd"u8) || LettersAfter(n, "xvd"u8) || LettersAfter(n, "hd"u8)) return 1;
        if (n.StartsWith("nvme"u8))
        {
            // nvme<ctrl>n<ns>; "nvme0c0n1" (multipath path) and "nvme0n1p1" (partition) are not whole disks
            int i = 4;
            if (Digits(n, ref i) == 0 || i >= n.Length || n[i] != (byte)'n') return 0;
            i++;
            return Digits(n, ref i) > 0 && i == n.Length ? 1 : 0;
        }
        if (n.StartsWith("mmcblk"u8)) { int i = 6; return Digits(n, ref i) > 0 && i == n.Length ? 1 : 0; }
        if (n.StartsWith("dm-"u8)) { int i = 3; return Digits(n, ref i) > 0 && i == n.Length ? 2 : 0; }
        if (n.StartsWith("md"u8)) { int i = 2; return Digits(n, ref i) > 0 && i == n.Length ? 2 : 0; }
        return 0;
    }

    static bool LettersAfter(ReadOnlySpan<byte> n, ReadOnlySpan<byte> prefix)
    {
        if (n.Length <= prefix.Length || !n.StartsWith(prefix)) return false;
        foreach (byte b in n.Slice(prefix.Length)) if (b < (byte)'a' || b > (byte)'z') return false;
        return true;
    }

    static int Digits(ReadOnlySpan<byte> n, ref int i)
    {
        int start = i;
        while (i < n.Length && (uint)(n[i] - (byte)'0') <= 9) i++;
        return i - start;
    }

    // ------------------------------------------------------------------ mounts, swaps

    internal readonly record struct Mount(string Source, string MountPoint, string Type);

    /// <summary>/proc/self/mounts: "source mountpoint fstype options 0 0", with \040-style octal escapes.</summary>
    internal static List<Mount> ParseMounts(string text)
    {
        var list = new List<Mount>();
        foreach (var line in text.Split('\n'))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 3) continue;
            list.Add(new Mount(UnescapeOctal(f[0]), UnescapeOctal(f[1]), f[2]));
        }
        return list;
    }

    /// <summary>/proc/swaps: paths of swap files (Type "file"; swap partitions are "partition").</summary>
    internal static List<string> ParseSwapFiles(string text)
    {
        var list = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var f = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 2 || f[0] == "Filename") continue;
            if (f[1] == "file") list.Add(UnescapeOctal(f[0]));
        }
        return list;
    }

    internal static string UnescapeOctal(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length && IsOct(s[i + 1]) && IsOct(s[i + 2]) && IsOct(s[i + 3]))
            {
                sb.Append((char)(((s[i + 1] - '0') << 6) | ((s[i + 2] - '0') << 3) | (s[i + 3] - '0')));
                i += 3;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    static bool IsOct(char c) => c >= '0' && c <= '7';

    // ------------------------------------------------------------------ CPU temperature sensors

    internal readonly record struct TempSensor(string Chip, string? Label, string Path);

    /// <summary>
    /// Preference among hwmon chips (and thermal zones, Chip "zone:&lt;type&gt;"): AMD k10temp/zenpower, Intel coretemp,
    /// ARM cpu_thermal, Intel x86_pkg_temp zone, then ACPI acpitz as a last resort. 0 = not a CPU sensor.
    /// </summary>
    internal static int TempChipRank(string chip) => chip switch
    {
        "k10temp" or "zenpower" => 6,
        "coretemp" => 5,
        "cpu_thermal" or "cpu-thermal" or "soc_thermal" or "zone:cpu-thermal" or "zone:cpu_thermal" => 4,
        "zone:x86_pkg_temp" => 3,
        "acpitz" => 1,
        _ => 0,
    };

    /// <summary>
    /// Input files to read (the hottest wins: several sockets) and a source label, from the best chip present.
    /// k10temp / zenpower: "Tdie", else "Tctl". coretemp: "Package id N".
    /// </summary>
    internal static List<string> ChooseCpuTemp(List<TempSensor> all, out string? source)
    {
        source = null;
        var paths = new List<string>();
        int best = 0;
        foreach (var t in all) best = Math.Max(best, TempChipRank(t.Chip));
        if (best == 0) return paths;

        var pick = new List<TempSensor>();
        foreach (var t in all) if (TempChipRank(t.Chip) == best) pick.Add(t);
        pick.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        string chip = pick[0].Chip;

        List<TempSensor> sel = pick;
        if (chip is "k10temp" or "zenpower")
        {
            var tdie = pick.FindAll(t => t.Label == "Tdie");
            var tctl = pick.FindAll(t => t.Label == "Tctl");
            sel = tdie.Count > 0 ? tdie : tctl.Count > 0 ? tctl : pick;
        }
        else if (chip == "coretemp")
        {
            var pkg = pick.FindAll(t => t.Label != null && t.Label.StartsWith("Package id", StringComparison.Ordinal));
            if (pkg.Count > 0) sel = pkg;
        }

        foreach (var t in sel) paths.Add(t.Path);
        string name = chip.StartsWith("zone:", StringComparison.Ordinal) ? "thermal zone " + chip.Substring(5) : chip;
        source = !string.IsNullOrEmpty(sel[0].Label) ? $"{name} {sel[0].Label}" : name;
        return paths;
    }
}
