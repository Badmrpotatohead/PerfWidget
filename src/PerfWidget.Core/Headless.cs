using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace PerfWidget;

/// <summary>
/// Window-less modes shared by the widget and the hidden sampler (what Orbit runs):
///   --json [path|-]          one live.json-format snapshot (~6 s)
///   --dump [path|-]          the same as readable text, every app and its processes
///   --history [min] [path|-] digest of the recorded history
///   --feed [--parent-pid N]  keep live.json and history up to date, like the widget does, until N exits
/// </summary>
public static class Headless
{
    /// <summary>Runs a one-shot mode if args ask for one; null when they don't.</summary>
    public static int? TryRun(string[] args)
    {
        int i;
        if ((i = Array.IndexOf(args, "--dump")) >= 0)
        {
            using var c = new Collector();
            var s = Warm(c, 4);
            Write(Arg(args, i, "perfwidget-dump.txt"), Report.Text(s, c.Platform.CpuTempHint));
            return 0;
        }
        if ((i = Array.IndexOf(args, "--json")) >= 0)
        {
            using var c = new Collector();
            var s = Warm(c, 5);
            var bytes = LiveFeed.Json(s, c.CpuTempSource);
            string path = Arg(args, i, "perfwidget-now.json");
            if (path == "-") { using var o = Console.OpenStandardOutput(); o.Write(bytes); }
            else File.WriteAllBytes(path, bytes);
            return 0;
        }
        if ((i = Array.IndexOf(args, "--history")) >= 0)
        {
            double mins = i + 1 < args.Length && double.TryParse(args[i + 1], out var m) ? m : 60;
            string path = i + 2 < args.Length ? args[i + 2] : "perfwidget-history.txt";
            Write(path, HistoryLog.Summary(DateTime.Now.AddMinutes(-mins), DateTime.Now));
            return 0;
        }
        return null;
    }

    /// <summary>
    /// Samples once a second, writing live.json (every 2 s) and history (every 10 s) to the normal data folder.
    /// Steps aside while another live process (an open widget) is writing the feed, and exits when the parent
    /// process (Orbit) is gone. Exit codes: 0 parent gone, 2 the collector could not start.
    /// </summary>
    public static int Feed(int? parentPid)
    {
        LiveFeed.Writer = "sampler";
        try { using var me = Process.GetCurrentProcess(); me.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        DateTime? parentStart = parentPid is int pp ? StartTimeOf(pp) : null;
        if (parentPid is int p0 && !Alive(p0, parentStart)) return 0;

        Collector? c = null;
        HistoryLog? history = null;
        var sw = Stopwatch.StartNew();
        long next = sw.ElapsedMilliseconds;
        try
        {
            while (true)
            {
                next += 1000;
                long wait = next - sw.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait); else next = sw.ElapsedMilliseconds;

                if (parentPid is int p && !Alive(p, parentStart)) return 0;

                if (OtherWriterActive())
                {
                    // The widget is open and doing the same job: release the collector and wait.
                    c?.Dispose(); c = null; history = null;
                    continue;
                }
                if (c == null)
                {
                    try { c = new Collector(); }
                    catch (Exception ex) { Log.Error(ex); return 2; }
                    history = new HistoryLog();
                }
                try
                {
                    var s = c.Sample();
                    history!.Add(s);
                    LiveFeed.MaybeWrite(s, c.CpuTempSource);
                }
                catch (Exception ex) { Log.Error(ex); }
            }
        }
        finally { c?.Dispose(); }
    }

    /// <summary>True when live.json was written in the last 6 s by another process that is still running.</summary>
    static bool OtherWriterActive()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(LiveFeed.FilePath));
            var root = doc.RootElement;
            int pid = root.GetProperty("pid").GetInt32();
            if (pid == Environment.ProcessId) return false;
            var t = root.GetProperty("t").GetDateTime().ToUniversalTime();
            if ((DateTime.UtcNow - t).TotalSeconds > 6) return false;
            return Alive(pid, null);
        }
        catch { return false; }
    }

    static DateTime? StartTimeOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime; } catch { return null; }
    }

    /// <summary>Alive and, when known, the same process (not a reused pid).</summary>
    static bool Alive(int pid, DateTime? started)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (started is DateTime st)
            {
                try { if (Math.Abs((p.StartTime - st).TotalSeconds) > 1) return false; } catch { }
            }
            return true;
        }
        catch { return false; }
    }

    static string Arg(string[] a, int i, string dflt) => i + 1 < a.Length ? a[i + 1] : dflt;

    static void Write(string path, string text)
    {
        if (path == "-") Console.Out.Write(text); else File.WriteAllText(path, text);
    }

    static Snapshot Warm(Collector c, int seconds)
    {
        Snapshot s = c.Sample();
        for (int k = 0; k < seconds; k++) { Thread.Sleep(1000); s = c.Sample(); }
        return s;
    }
}
