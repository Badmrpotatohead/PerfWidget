using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace PerfWidget;

static class Program
{
    static Mutex? mutex;

    [STAThread]
    public static int Main(string[] args)
    {
        // Headless modes (also what CI and Orbit use): no window, no display needed.
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

        mutex = new Mutex(true, "PerfWidget.SingleInstance", out bool created);
        if (!created) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
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

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .WithInterFont()
            .LogToTrace();
}

sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        Name = "PerfWidget";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var w = new MainWindow();
            desktop.MainWindow = w;
            w.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
