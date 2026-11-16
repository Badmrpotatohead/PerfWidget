using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;

namespace PerfWidget;

public sealed class MainWindow : Window
{
    readonly Settings settings = Settings.Load();
    readonly ResourceRow cpuRow = new("CPU"), ramRow = new("RAM"), swapRow = new("SWAP"), gpuRow = new("GPU"), diskRow = new("DISK");
    readonly Dictionary<string, AppRow> appRows = new();
    List<string> shownKeys = new();
    static readonly string[] SortCycle = { "auto", "cpu", "ram", "gpu", "io" };
    static readonly Dictionary<string, string> MetricLabel = new() { ["cpu"] = "CPU", ["ram"] = "RAM", ["gpu"] = "GPU", ["io"] = "I/O" };
    string sortMode = "auto";
    Snapshot? last;
    TrayIcon? tray;
    int trayLevel = -1;
    volatile bool running = true;
    MenuItem? miCompact, miTop, miStartup;
    string cpuTempHint = "";

    // controls
    readonly Border shell;
    readonly Ellipse statusDot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 10, 0), Fill = Pal.Green };
    readonly TextBlock headline = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14.5, FontWeight = FontWeight.SemiBold, Text = "Measuring…", Foreground = Pal.Text };
    readonly TextBlock headDetail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)), Margin = new Thickness(0, 1, 0, 0) };
    readonly StackPanel issuePanel = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel resourcePanel = new() { Background = Pal.Clear, Margin = new Thickness(0, 12, 0, 0) };
    readonly StackPanel fullPanel = new() { Margin = new Thickness(0, 14, 0, 0) };
    readonly StackPanel appList = new();
    readonly TextBlock sortLabel = new() { HorizontalAlignment = HorizontalAlignment.Right, FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Pal.Text2, Cursor = Pal.Hand, Text = "RAM · auto ▾" };
    readonly TextBlock driveText = new() { FontSize = 10.5, Foreground = Pal.Dim, Margin = new Thickness(0, 12, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button compactBtn, hideBtn;
    readonly Grid headerGrid = new() { Background = Pal.Clear };

    static string OsLabel => OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Windows";

    public MainWindow()
    {
        Title = "PerfWidget";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        FontSize = 12;
        Foreground = Pal.Text;

        compactBtn = ChromeButton("M 1 6 L 5 2 L 9 6", "Compact / full view");
        hideBtn = ChromeButton("M 1 4 L 9 4", "Hide (bring it back from the tray / menu bar icon)");

        // header
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        headerGrid.Children.Add(statusDot);
        var heads = new StackPanel { Children = { headline, headDetail, issuePanel } };
        Grid.SetColumn(heads, 1);
        headerGrid.Children.Add(heads);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, -6, 0), Children = { compactBtn, hideBtn } };
        Grid.SetColumn(btns, 2);
        headerGrid.Children.Add(btns);

        foreach (var r in new[] { cpuRow, ramRow, swapRow, gpuRow, diskRow }) resourcePanel.Children.Add(r.Root);

        var appsHeader = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        appsHeader.Children.Add(new TextBlock { Text = "TOP APPS", FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Pal.Dim });
        appsHeader.Children.Add(sortLabel);
        ToolTip.SetTip(sortLabel, "Click to change what apps are ranked by. Auto follows whatever is under pressure right now.");
        fullPanel.Children.Add(appsHeader);
        fullPanel.Children.Add(appList);
        fullPanel.Children.Add(driveText);

        shell = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Pal.Bg,
            BorderBrush = Pal.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12, 16, 14),
            Child = new StackPanel { Children = { headerGrid, resourcePanel, fullPanel } },
        };
        Content = shell;

        BuildMenu();
        BuildTray();
        ApplySettings();
        WindowStartupLocation = WindowStartupLocation.Manual;
        PlaceWindow();
        Opened += (_, _) => PlaceWindow();
        PositionChanged += (_, _) => { if (IsVisible) { settings.Left = Position.X; settings.Top = Position.Y; } };

        headerGrid.PointerPressed += Drag;
        resourcePanel.PointerPressed += Drag;
        compactBtn.Click += (_, _) => { settings.Compact = !settings.Compact; ApplySettings(); settings.Save(); };
        hideBtn.Click += (_, _) => Hide();
        sortLabel.PointerReleased += (_, _) =>
        {
            sortMode = SortCycle[(Array.IndexOf(SortCycle, sortMode) + 1) % SortCycle.Length];
            if (last != null) RenderApps(last);
        };

        new Thread(Loop) { IsBackground = true, Name = "collector" }.Start();
    }

    static Button ChromeButton(string geometry, string tip)
    {
        var b = new Button
        {
            Width = 24, Height = 22, Padding = new Thickness(0), Background = Pal.Clear, BorderThickness = new Thickness(0),
            Cursor = Pal.Hand, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new Path { Data = Geometry.Parse(geometry), Stroke = Pal.Dim, StrokeThickness = 1.4, Width = 10, Height = 8 },
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    // ------------------------------------------------------------------ window plumbing

    void PlaceWindow()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null) return;
        var wa = screen.WorkingArea;
        double scale = screen.Scaling;
        int w = (int)(Width * scale);
        int l = (int)(settings.Left ?? (wa.Right - w - 16 * scale));
        int t = (int)(settings.Top ?? (wa.Y + 16 * scale));
        bool onScreen = Screens.All.Any(s => s.Bounds.Contains(new PixelPoint(l + 40, t + 10)));
        if (!onScreen) { l = (int)(wa.Right - w - 16 * scale); t = (int)(wa.Y + 16 * scale); }
        Position = new PixelPoint(l, t);
    }

    void ApplySettings()
    {
        fullPanel.IsVisible = !settings.Compact;
        if (compactBtn.Content is Path p) p.Data = Geometry.Parse(settings.Compact ? "M 1 2 L 5 6 L 9 2" : "M 1 6 L 5 2 L 9 6");
        Topmost = settings.Topmost;
        Opacity = Math.Clamp(settings.Opacity, 0.3, 1);
    }

    void Drag(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        try { BeginMoveDrag(e); } catch { }
        settings.Save();
    }

    void BuildMenu()
    {
        var m = new ContextMenu();
        MenuItem Check(string header, Action<bool> set)
        {
            var mi = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox };
            mi.Click += (_, _) => { set(mi.IsChecked); ApplySettings(); settings.Save(); };
            m.Items.Add(mi);
            return mi;
        }
        MenuItem Item(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => { try { act(); } catch (Exception ex) { Log.Error(ex); } };
            return mi;
        }

        m.Items.Add(Item("What happened in the last hour…", () => ShowHistory(60)));
        m.Items.Add(Item("What happened in the last 24 hours…", () => ShowHistory(24 * 60)));
        m.Items.Add(new Separator());

        miCompact = Check("Compact mode", v => settings.Compact = v);
        miTop = Check("Always on top", v => settings.Topmost = v);
        miStartup = Check(OperatingSystem.IsMacOS() ? "Open at login" : $"Start with {OsLabel}", v => { try { Autostart.Set(v); } catch (Exception ex) { Log.Error(ex); } });

        var op = new MenuItem { Header = "Opacity" };
        foreach (var v in new[] { 1.0, 0.9, 0.8, 0.7 })
            op.Items.Add(Item($"{v * 100:0}%", () => { settings.Opacity = v; ApplySettings(); settings.Save(); }));
        m.Items.Add(op);

        var rows = new MenuItem { Header = "Apps shown" };
        foreach (var n in new[] { 3, 5, 8, 12 })
            rows.Items.Add(Item(n.ToString(), () => { settings.AppRows = n; settings.Save(); if (last != null) RenderApps(last); }));
        m.Items.Add(rows);

        m.Items.Add(new Separator());
        m.Items.Add(Item("Open history folder", () => { Directory.CreateDirectory(HistoryLog.Dir); OpenPath(HistoryLog.Dir); }));
        var (sysName, sysAct) = SystemMonitor();
        if (sysAct != null) m.Items.Add(Item($"Open {sysName}", sysAct));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Hide", Hide));
        m.Items.Add(Item("Quit", ExitApp));

        m.Opening += (_, _) =>
        {
            miCompact!.IsChecked = settings.Compact;
            miTop!.IsChecked = settings.Topmost;
            miStartup!.IsChecked = Autostart.IsEnabled();
        };
        shell.ContextMenu = m;
    }

    static (string, Action?) SystemMonitor()
    {
        if (OperatingSystem.IsWindows()) return ("Task Manager", () => Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }));
        if (OperatingSystem.IsMacOS()) return ("Activity Monitor", () => Process.Start("open", "-a \"Activity Monitor\""));
        foreach (var app in new[] { "gnome-system-monitor", "plasma-systemmonitor", "ksysguard", "xfce4-taskmanager", "mate-system-monitor" })
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
                if (dir.Length > 0 && File.Exists(System.IO.Path.Combine(dir, app)))
                    return ("System Monitor", () => Process.Start(app));
        return ("", null);
    }

    static void OpenPath(string path)
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS()) Process.Start("open", $"\"{path}\"");
        else Process.Start("xdg-open", $"\"{path}\"");
    }

    void ShowHistory(double minutes)
    {
        var to = DateTime.Now;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string text = HistoryLog.Summary(to.AddMinutes(-minutes), to);
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"perfwidget-history-{minutes:0}m.txt");
                File.WriteAllText(path, text);
                if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
                else if (OperatingSystem.IsMacOS()) Process.Start("open", $"-t \"{path}\"");
                else Process.Start("xdg-open", $"\"{path}\"");
            }
            catch (Exception ex) { Log.Error(ex); }
        });
    }

    void BuildTray()
    {
        try
        {
            tray = new TrayIcon { ToolTipText = "PerfWidget", Icon = MakeIcon(0), IsVisible = true };
            tray.Clicked += (_, _) => ToggleVisible();
            var menu = new NativeMenu();
            var show = new NativeMenuItem("Show / hide");
            show.Click += (_, _) => ToggleVisible();
            var quit = new NativeMenuItem("Quit PerfWidget");
            quit.Click += (_, _) => ExitApp();
            menu.Items.Add(show);
            menu.Items.Add(quit);
            tray.Menu = menu;
            if (Application.Current != null) TrayIcon.SetIcons(Application.Current, new TrayIcons { tray });
        }
        catch (Exception ex) { Log.Error(ex); tray = null; } // some Linux desktops have no tray
    }

    void ToggleVisible()
    {
        if (IsVisible) Hide();
        else { Show(); Activate(); }
    }

    void ExitApp()
    {
        running = false;
        settings.Save();
        if (tray != null) tray.IsVisible = false;
        (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    static WindowIcon MakeIcon(int severity)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(32, 32));
        using (var ctx = rtb.CreateDrawingContext())
        {
            var br = new SolidColorBrush(Pal.SevColor(severity));
            ctx.FillRectangle(br, new Rect(3, 18, 7, 11));
            ctx.FillRectangle(br, new Rect(12, 10, 7, 19));
            ctx.FillRectangle(br, new Rect(21, 3, 7, 26));
        }
        var ms = new MemoryStream();
        rtb.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }

    void UpdateTray(int level, string text)
    {
        if (tray == null) return;
        if (level != trayLevel) { tray.Icon = MakeIcon(level); trayLevel = level; }
        tray.ToolTipText = text.Length > 127 ? text[..126] + "…" : text;
    }

    // ------------------------------------------------------------------ sampling loop

    void Loop()
    {
        Collector collector;
        try { collector = new Collector(); cpuTempHint = collector.Platform.CpuTempHint; }
        catch (Exception ex)
        {
            Log.Error(ex);
            Dispatcher.UIThread.Post(() => headline.Text = "Collector failed to start: " + ex.Message);
            return;
        }
        var history = new HistoryLog();
        var sw = Stopwatch.StartNew();
        long next = sw.ElapsedMilliseconds;
        while (running)
        {
            next += 1000;
            long wait = next - sw.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep((int)wait); else next = sw.ElapsedMilliseconds;
            try
            {
                var s = collector.Sample();
                history.Add(s);
                string? tempSource = collector.CpuTempSource;
                LiveFeed.MaybeWrite(s, tempSource);
                Dispatcher.UIThread.Post(() => Render(s, tempSource));
            }
            catch (Exception ex) { Log.Error(ex); }
        }
        collector.Dispose();
    }

    // ------------------------------------------------------------------ rendering

    static AppGroup? TopBy(Snapshot s, Func<AppGroup, double> f) => s.Groups.OrderByDescending(f).FirstOrDefault();

    void Render(Snapshot s, string? tempSource)
    {
        last = s;
        bool win = s.Platform == "windows";

        // verdict
        var top = s.Issues.FirstOrDefault();
        int level = top?.Severity ?? 0;
        headline.Text = top?.Title ?? "All clear";
        headDetail.Text = top?.Detail ?? "Nothing is holding the machine back.";
        statusDot.Fill = Pal.Sev(level);

        issuePanel.Children.Clear();
        foreach (var i in s.Issues.Skip(1).Take(2))
        {
            var tb = new TextBlock { FontSize = 11.5, Foreground = Pal.Text2, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            tb.Inlines = new InlineCollection { new Run("● ") { Foreground = Pal.Sev(i.Severity), FontSize = 9 }, new Run(i.Title) };
            ToolTip.SetTip(tb, i.Detail);
            issuePanel.Children.Add(tb);
        }
        if (s.Issues.Count > 3)
        {
            var more = new TextBlock { FontSize = 10.5, Foreground = Pal.Dim, Text = $"+ {s.Issues.Count - 3} more" };
            ToolTip.SetTip(more, string.Join("\n", s.Issues.Skip(3).Select(i => i.Text)));
            issuePanel.Children.Add(more);
        }
        issuePanel.IsVisible = issuePanel.Children.Count > 0;

        // CPU: who's using it (or which app is stuck on one core), temp if known
        var pinned = s.Groups.Where(g => g.MaxThreadPct >= 90).OrderByDescending(g => g.MaxThreadPct).FirstOrDefault();
        var cpuTop = TopBy(s, g => g.CpuPct);
        string cpuWho = pinned != null && s.CpuPct < 75 ? $"{pinned.Name} maxing 1 core"
            : cpuTop != null && cpuTop.CpuPct >= 1 ? $"{cpuTop.Name}  {Fmt.Pct(cpuTop.CpuPct)}" : "idle";
        string thermal = s.ThermalLevel is >= 2 ? (s.ThermalLevel >= 3 ? "overheating  ·  " : "throttling  ·  ") : "";
        cpuRow.Update(s.CpuPct, thermal + (s.CpuTemp is double ct ? $"{ct:0}°C  ·  " : "") + cpuWho,
            s.Bottleneck == "cpu", thermal.Length > 0 ? Pal.Amber : pinned != null && s.CpuPct < 75 ? Pal.Amber : null);
        cpuRow.SetTip(
            $"CPU  {s.CpuPct:0}% of all {s.LogicalCpus} threads\n" +
            (s.CpuMHz > 0 ? $"Clock {s.CpuMHz / 1000:0.00} GHz" + (s.CpuBaseMHz > 0 ? $" (base {s.CpuBaseMHz / 1000:0.00})" : "") + (win ? $"  ·  firmware limit {s.CpuPerfLimit:0}%" : "") + "\n" : "") +
            (s.ThermalLevel is int tl ? $"Thermal state: {(tl switch { 0 => "nominal", 1 => "fair", 2 => "serious (throttling)", _ => "critical" })}\n" : "") +
            (s.CpuStallPct is double cs ? $"Tasks waiting for CPU {cs:0}% of the time\n" : "") +
            (s.CpuTemp is double t ? $"Temperature {t:0}°C (via {tempSource})\n" : $"Temperature: not available ({cpuTempHint}).\n") +
            "\nTop:  " + string.Join(",  ", s.Groups.OrderByDescending(g => g.CpuPct).Take(4).Select(g => $"{g.Name} {Fmt.Pct(g.CpuPct)}")) +
            (pinned != null ? $"\n\n{pinned.Name} has one thread at {pinned.MaxThreadPct:0}% of a core:\n{pinned.MaxThreadProc}" : ""));

        // RAM
        double ramPct = s.RamTotal > 0 ? 100.0 * s.RamUsed / s.RamTotal : 0;
        var ramTop = TopBy(s, g => g.Ram);
        IBrush? ramBrush = s.MemoryPressure is 2 ? Pal.Red : s.MemoryPressure is 1 ? Pal.Amber : null;
        string pressure = s.MemoryPressure is 2 ? "pressure critical  ·  " : s.MemoryPressure is 1 ? "pressure high  ·  " : "";
        ramRow.Update(ramPct, pressure + (ramTop != null ? $"{ramTop.Name}  {Fmt.Bytes(ramTop.Ram)}" : ""), s.Bottleneck == "ram", ramBrush);
        ramRow.SetTip(
            $"RAM  {Fmt.GB(s.RamUsed)} of {Fmt.GB(s.RamTotal)} GB in use  ({Fmt.Bytes(s.Cache)} of file cache the OS can free instantly)\n" +
            (s.Compressed > 0 ? $"Compressed  {Fmt.Bytes(s.Compressed)}\n" : "") +
            (s.MemoryPressure is int mp ? $"Memory pressure: {(mp switch { 0 => "normal", 1 => "elevated", _ => "critical" })}" + (s.MemStallPct is double ms ? $"  ·  apps stalled on memory {ms:0}% of the time" : "") + "\n" : "") +
            "\nTop:  " + string.Join(",  ", s.Groups.OrderByDescending(g => g.Ram).Take(4).Select(g => $"{g.Name} {Fmt.Bytes(g.Ram)}")));

        // SWAP: Windows shows commit; macOS/Linux have no enforced limit, so show swap used
        bool swapping = s.PagesInBps >= 5 * Fmt.MiB && (ramPct >= 80 || s.MemoryPressure >= 1);
        string swapWord = win ? "page file" : "swap";
        double swapBar = s.CommitLimit > 0 ? 100.0 * s.CommitTotal / s.CommitLimit
                       : s.PagefileTotal > 0 ? 100.0 * s.PagefileUsed / s.PagefileTotal : 0;
        string swapText = swapping ? $"swapping {Fmt.Rate(s.PagesInBps)}"
            : s.PagefileTotal > 0 || s.PagefileUsed > 0 ? $"{swapWord} {Fmt.Bytes(s.PagefileUsed)} used" + (s.Compressed > 0 ? $"  ·  {Fmt.Bytes(s.Compressed)} compressed" : "")
            : s.Compressed > 0 ? $"{Fmt.Bytes(s.Compressed)} compressed" : $"no {swapWord}";
        swapRow.Update(swapBar, swapText, s.Bottleneck == "commit", swapping ? Pal.Red : null);
        swapRow.SetTip(
            (s.CommitLimit > 0
                ? $"Virtual memory (commit): {Fmt.GB(s.CommitTotal)} of {Fmt.GB(s.CommitLimit)} GB reserved by programs, backed by RAM + {swapWord}.\nAt 100% new allocations fail and apps crash.\n\n"
                : $"Bar = {swapWord} in use. {OsLabel} has no hard commit limit; it swaps instead.\n\n") +
            $"{(win ? "Page file" : "Swap")}  {Fmt.Bytes(s.PagefileUsed)} of {Fmt.Bytes(s.PagefileTotal)} used\n" +
            $"Swapping in {Fmt.Rate(s.PagesInBps)}  ·  out {Fmt.Rate(s.PagesOutBps)}  ·  hard faults {s.HardFaultsPs:0}/s\n" +
            "(Reading back from swap only hurts when RAM is also nearly full.)");

        // GPU
        var gpuTop = TopBy(s, g => g.GpuPct);
        gpuRow.Update(s.GpuPct,
            (s.GpuTemp is double gt ? $"{gt:0}°C" : "") +
            (gpuTop != null && gpuTop.GpuPct >= 5 ? (s.GpuTemp != null ? "  ·  " : "") + $"{gpuTop.Name}  {gpuTop.GpuPct:0}%" : ""),
            s.Bottleneck is "gpu" or "vram");
        gpuRow.SetTip(
            $"{s.GpuName}  {s.GpuPct:0}%\n" +
            (s.VramTotal > 0 ? $"VRAM {Fmt.GB(s.VramUsed)} of {Fmt.GB(s.VramTotal)} GB\n" : s.VramUsed > 0 ? $"GPU memory in use {Fmt.Bytes(s.VramUsed)} (shared with RAM)\n" : "") +
            (s.GpuTemp is double gt2 ? $"Temperature {gt2:0}°C" + (s.GpuSlowdownTemp is double sd ? $" (slows down at {sd:0}°C)" : "") + "\n" : "") +
            (s.GpuPowerW is double pw ? $"Power {pw:0} of {s.GpuPowerLimitW:0} W  ·  clock {s.GpuClockMHz:0} MHz (max {s.GpuMaxClockMHz:0})  ·  fan {s.GpuFanPct:0}%\n" : "") +
            (s.GpuTemp != null ? $"Clock limits active: {GpuSensors.Describe(s.GpuThrottleReasons) ?? "none"}\n" : "") +
            (s.Groups.Any(g => g.GpuPct > 0 || g.Vram > 0)
                ? "\nTop:  " + string.Join(",  ", s.Groups.Where(g => g.GpuPct >= 0.5 || g.Vram > 0).OrderByDescending(g => g.GpuPct).ThenByDescending(g => g.Vram).Take(4)
                    .Select(g => $"{g.Name} {g.GpuPct:0}% ({Fmt.Bytes(g.Vram)})"))
                : "\nPer-app GPU use isn't exposed on this OS."));

        // DISK
        var busiest = s.Disks.OrderByDescending(d => d.BusyPct).FirstOrDefault();
        diskRow.Update(busiest?.BusyPct ?? 0, busiest == null ? "" : $"{busiest.Name}  {Fmt.Rate(busiest.Bps)}", s.Bottleneck == "disk");
        diskRow.SetTip(
            "Busiest physical disk (% of time servicing requests):\n" +
            string.Join("\n", s.Disks.Where(d => d.BusyPct >= 1 || d.Bps > 0).OrderByDescending(d => d.BusyPct).Select(d => $"  {d.Name}  {d.BusyPct:0}%  {Fmt.Rate(d.Bps)}")) +
            (s.IoStallPct is double io ? $"\nTasks waiting on disk {io:0}% of the time" : "") +
            (win ? "\n\nMost I/O (file + network + pipes, so it can exceed the disk rate):\n  " : "\n\nMost disk I/O:\n  ") +
            string.Join(",  ", s.Groups.OrderByDescending(g => g.IoBps).Take(3).Select(g => $"{g.Name} {Fmt.Rate(g.IoBps)}")));

        RenderApps(s);
        RenderDrives(s);
        UpdateTray(level, top?.Text ?? "PerfWidget: all clear");
    }

    static string AutoMetric(string bottleneck) => bottleneck switch
    {
        "cpu" => "cpu", "gpu" or "vram" => "gpu", "disk" => "io", _ => "ram",
    };

    void RenderApps(Snapshot s)
    {
        string metric = sortMode == "auto" ? AutoMetric(s.Bottleneck) : sortMode;
        sortLabel.Text = $"{MetricLabel[metric]}{(sortMode == "auto" ? " · auto" : "")} ▾";

        var groups = s.Groups.OrderByDescending(g => AppRow.Metric(g, metric)).ThenByDescending(g => g.Ram)
                             .Take(Math.Max(1, settings.AppRows)).ToList();
        double maxIo = Math.Max(1, s.Groups.Count > 0 ? s.Groups.Max(g => g.IoBps) : 1);

        var keys = groups.Select(g => g.Key).ToList();
        if (!keys.SequenceEqual(shownKeys))
        {
            appList.Children.Clear();
            foreach (var k in keys)
            {
                if (!appRows.TryGetValue(k, out var row)) appRows[k] = row = new AppRow();
                appList.Children.Add(row.Root);
            }
            shownKeys = keys;
        }
        foreach (var g in groups)
        {
            double share = metric switch
            {
                "cpu" => g.CpuPct / 100,
                "gpu" => g.GpuPct / 100,
                "io" => g.IoBps / maxIo,
                _ => s.RamTotal > 0 ? (double)g.Ram / s.RamTotal : 0,
            };
            appRows[g.Key].Update(g, metric, share);
        }
    }

    void RenderDrives(Snapshot s)
    {
        var inl = new InlineCollection { new Run("Free  ") { Foreground = Pal.Dim } };
        bool first = true;
        foreach (var d in s.Drives)
        {
            double pctFree = d.Total > 0 ? 100.0 * d.Free / d.Total : 100;
            if (!first) inl.Add(new Run("   "));
            first = false;
            inl.Add(new Run(d.Name + " ") { Foreground = Pal.Dim });
            inl.Add(new Run(Fmt.Bytes(d.Free)) { Foreground = pctFree < 5 ? Pal.Red : pctFree < 10 ? Pal.Amber : Pal.Text2 });
        }
        driveText.Inlines = inl;
        string swapWord = s.Platform == "windows" ? "page file" : "swap";
        ToolTip.SetTip(driveText,
            string.Join("\n", s.Drives.Select(d => $"{d.Name}  {Fmt.Bytes(d.Free)} free of {Fmt.Bytes(d.Total)}" + (d.HasPagefile ? $"  ({swapWord})" : ""))) +
            $"\n\n{s.ProcessCount} processes  ·  {s.ThreadCount:N0} threads  ·  this monitor uses {s.SelfCpuPct:0.0}% CPU");
    }
}
