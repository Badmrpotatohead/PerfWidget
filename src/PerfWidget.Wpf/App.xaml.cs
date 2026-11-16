using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace PerfWidget;

public partial class App : Application
{
    Mutex? mutex;

    void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) => { Log.Error(ex.Exception); ex.Handled = true; };

        int dump = Array.IndexOf(e.Args, "--dump");
        if (dump >= 0)
        {
            // Headless self-check: write a text report of a few samples and exit.
            string path = dump + 1 < e.Args.Length ? e.Args[dump + 1] : "perfwidget-dump.txt";
            using var c = new Collector();
            Snapshot s = c.Sample();
            for (int i = 0; i < 4; i++) { Thread.Sleep(1000); s = c.Sample(); }
            File.WriteAllText(path, Report.Text(s, c.Platform.CpuTempHint));
            Shutdown();
            return;
        }

        int json = Array.IndexOf(e.Args, "--json");
        if (json >= 0)
        {
            // PerfWidget.exe --json [outfile]  : one live snapshot in the live.json schema
            string path = json + 1 < e.Args.Length ? e.Args[json + 1] : "perfwidget-now.json";
            using var c = new Collector();
            Snapshot s = c.Sample();
            for (int i = 0; i < 5; i++) { Thread.Sleep(1000); s = c.Sample(); }
            File.WriteAllBytes(path, LiveFeed.Json(s, c.CpuTempSource));
            Shutdown();
            return;
        }

        int hist = Array.IndexOf(e.Args, "--history");
        if (hist >= 0)
        {
            // PerfWidget.exe --history [minutes=60] [outfile]
            double mins = hist + 1 < e.Args.Length && double.TryParse(e.Args[hist + 1], out var m) ? m : 60;
            string path = hist + 2 < e.Args.Length ? e.Args[hist + 2] : "perfwidget-history.txt";
            File.WriteAllText(path, HistoryLog.Summary(DateTime.Now.AddMinutes(-mins), DateTime.Now));
            Shutdown();
            return;
        }

        mutex = new Mutex(true, @"Local\PerfWidget.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }
        new MainWindow().Show();
    }
}


static class Win32
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetWindowLong(System.IntPtr h, int idx);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int SetWindowLong(System.IntPtr h, int idx, int v);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool DestroyIcon(System.IntPtr h);
}
