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
            File.WriteAllText(path, Report.Text(s));
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

sealed class Settings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Compact { get; set; }
    public bool Topmost { get; set; } = true;
    public double Opacity { get; set; } = 1.0;
    public int AppRows { get; set; } = 5;

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfWidget");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

static class StartWithWindows
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "PerfWidget";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string;
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, false);
    }
}

static class Log
{
    public static void Error(Exception e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfWidget");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTime.Now:s} {e}\n");
        }
        catch { }
    }
}
