using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace PerfWidget;

/// <summary>Where PerfWidget keeps its files on each OS.</summary>
public static class AppPaths
{
    /// <summary>
    /// Windows: %APPDATA%\PerfWidget. macOS: ~/Library/Application Support/PerfWidget.
    /// Linux: $XDG_DATA_HOME/PerfWidget (default ~/.local/share/PerfWidget).
    /// </summary>
    public static string DataDir { get; } = Resolve();

    public static string HistoryDir => Path.Combine(DataDir, "history");
    public static string LiveFeedFile => Path.Combine(DataDir, "live.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string ErrorLog => Path.Combine(DataDir, "error.log");

    static string Resolve()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfWidget");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "PerfWidget");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "PerfWidget");
    }
}

public static class Log
{
    public static void Error(Exception e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(AppPaths.ErrorLog, $"{DateTime.Now:s} {e}\n");
        }
        catch { }
    }
}

public sealed class Settings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Compact { get; set; }
    public bool Topmost { get; set; } = true;
    public double Opacity { get; set; } = 1.0;
    public int AppRows { get; set; } = 5;

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>Start at login: Windows HKCU Run key, macOS LaunchAgent, Linux XDG autostart entry.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "PerfWidget";
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string MacPlist => Path.Combine(Home, "Library", "LaunchAgents", "com.sivants.perfwidget.plist");
    static string LinuxDesktop => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"),
        "autostart", "perfwidget.desktop");

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(Name) is string;
            }
            return File.Exists(OperatingSystem.IsMacOS() ? MacPlist : LinuxDesktop);
        }
        catch { return false; }
    }

    public static void Set(bool on)
    {
        string exe = Environment.ProcessPath ?? "";
        if (OperatingSystem.IsWindows())
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(Name, $"\"{exe}\""); else k.DeleteValue(Name, false);
            return;
        }
        string file = OperatingSystem.IsMacOS() ? MacPlist : LinuxDesktop;
        if (!on) { if (File.Exists(file)) File.Delete(file); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (OperatingSystem.IsMacOS())
            File.WriteAllText(file, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict>
                  <key>Label</key><string>com.sivants.perfwidget</string>
                  <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(exe)}</string></array>
                  <key>RunAtLoad</key><true/>
                  <key>ProcessType</key><string>Interactive</string>
                </dict></plist>
                """);
        else
            File.WriteAllText(file, $"""
                [Desktop Entry]
                Type=Application
                Name=PerfWidget
                Comment=What is hammering this machine
                Exec="{exe}"
                X-GNOME-Autostart-enabled=true
                """);
    }
}
