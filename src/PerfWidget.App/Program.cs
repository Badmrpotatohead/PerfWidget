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
        // Headless modes (shared with the hidden sampler): no window, no display needed.
        if (Headless.TryRun(args) is int rc) return rc;

        mutex = new Mutex(true, "PerfWidget.SingleInstance", out bool created);
        if (!created) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
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
