using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace PerfWidget;

static class Theme
{
    static SolidColorBrush B(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    public static readonly Brush Text = B(0xFFE8EBF0), Text2 = B(0xFFB4BCC8), Dim = B(0xFF7C8596),
        Track = B(0x1AFFFFFF), Hover = B(0x12FFFFFF), Share = B(0x2E6CB4FF), Spark = B(0x80FFFFFF),
        Green = B(0xFF3DD68C), Amber = B(0xFFF5B841), Red = B(0xFFF0605D);

    public static Brush Level(double pct) => pct >= 90 ? Red : pct >= 70 ? Amber : Green;
    public static Brush Sev(int s) => s >= 3 ? Red : s >= 2 ? Amber : Green;

    public static Drawing.Color SevColor(int s) =>
        s >= 3 ? Drawing.Color.FromArgb(240, 96, 93) : s >= 2 ? Drawing.Color.FromArgb(245, 184, 65) : Drawing.Color.FromArgb(61, 214, 140);
}

/// <summary>One line per resource: LABEL  [bar + 60 s sparkline]  value  who/extra. Details live in the tooltip.</summary>
sealed class ResourceRow
{
    const double BarW = 120, BarH = 10;
    public readonly Grid Root = new() { Height = 24, Background = Brushes.Transparent };
    readonly TextBlock value = new(), extra = new();
    readonly Rectangle fill = new() { RadiusX = 2.5, RadiusY = 2.5, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    readonly Polyline spark = new() { StrokeThickness = 1, Stroke = Theme.Spark, StrokeLineJoin = PenLineJoin.Round };
    readonly double[] hist = new double[60];
    int count;

    public ResourceRow(string label)
    {
        foreach (var w in new[] { 46.0, BarW + 8, 46 }) Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        Root.ColumnDefinitions.Add(new ColumnDefinition());

        Root.Children.Add(new TextBlock { Text = label, Foreground = Theme.Dim, FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var track = new Grid { Width = BarW, Height = BarH, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(new Rectangle { RadiusX = 2.5, RadiusY = 2.5, Fill = Theme.Track });
        track.Children.Add(fill);
        track.Children.Add(spark);
        Grid.SetColumn(track, 1);
        Root.Children.Add(track);

        value.FontSize = 12.5;
        value.FontWeight = FontWeights.SemiBold;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        Grid.SetColumn(value, 2);
        Root.Children.Add(value);

        extra.FontSize = 11.5;
        extra.Margin = new Thickness(12, 0, 0, 0);
        extra.VerticalAlignment = VerticalAlignment.Center;
        extra.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(extra, 3);
        Root.Children.Add(extra);
    }

    public void Update(double pct, string extraText, bool highlight = false, Brush? extraBrush = null)
    {
        pct = Math.Clamp(double.IsFinite(pct) ? pct : 0, 0, 100);
        Array.Copy(hist, 1, hist, 0, hist.Length - 1);
        hist[^1] = pct;
        count = Math.Min(hist.Length, count + 1);

        fill.Width = BarW * pct / 100;
        fill.Fill = Theme.Level(pct);
        var pts = new PointCollection(count);
        double step = BarW / (hist.Length - 1);
        for (int i = hist.Length - count; i < hist.Length; i++)
            pts.Add(new Point(i * step, BarH - 1 - (BarH - 2) * hist[i] / 100));
        spark.Points = pts;

        value.Text = $"{pct:0}%";
        extra.Text = extraText;
        extra.Foreground = extraBrush ?? (highlight ? Theme.Text : Theme.Text2);
    }

    public void SetTip(string tip) => Root.ToolTip = tip;
}

/// <summary>An app in the list: name, one number (whatever the list is ranked by), share bar behind. Click to expand.</summary>
sealed class AppRow
{
    public const double RowWidth = 346;
    public readonly StackPanel Root = new();
    readonly Grid main = new() { Background = Brushes.Transparent, Cursor = Cursors.Hand, Height = 24 };
    readonly Rectangle share = new() { HorizontalAlignment = HorizontalAlignment.Left, RadiusX = 3, RadiusY = 3, Margin = new Thickness(0, 2, 0, 2), Fill = Theme.Share };
    readonly TextBlock name, val;
    readonly StackPanel kids = new() { Margin = new Thickness(14, 0, 0, 6), Visibility = Visibility.Collapsed };
    bool expanded;
    AppGroup? lastGroup;
    string lastMetric = "ram";

    static void Columns(Grid g)
    {
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
    }

    static TextBlock Cell(Grid g, int col, double size)
    {
        var t = new TextBlock
        {
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = new Thickness(col == 0 ? 8 : 0, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        Grid.SetColumn(t, col);
        g.Children.Add(t);
        return t;
    }

    public AppRow()
    {
        Columns(main);
        Grid.SetColumnSpan(share, 2);
        main.Children.Add(share);
        name = Cell(main, 0, 12.5);
        val = Cell(main, 1, 12.5);
        val.FontWeight = FontWeights.SemiBold;
        main.MouseEnter += (_, _) => main.Background = Theme.Hover;
        main.MouseLeave += (_, _) => main.Background = Brushes.Transparent;
        main.MouseLeftButtonUp += (_, e) =>
        {
            expanded = !expanded;
            kids.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (expanded && lastGroup != null) RenderKids(lastGroup, lastMetric);
            e.Handled = true;
        };
        Root.Children.Add(main);
        Root.Children.Add(kids);
    }

    public static double Metric(AppGroup g, string m) => m switch
    {
        "cpu" => g.CpuPct, "gpu" => g.GpuPct, "io" => g.IoBps, _ => g.Ram,
    };

    static double PMetric(ProcInfo p, string m) => m switch
    {
        "cpu" => p.CpuPct, "gpu" => p.GpuPct, "io" => p.IoBps, _ => p.PrivateWs,
    };

    static string FormatMetric(double v, string m) => m switch
    {
        "cpu" => Fmt.Pct(v),
        "gpu" => v >= 0.5 ? $"{v:0}%" : "–",
        "io" => v >= 10 * Fmt.KiB ? Fmt.Rate(v) : "–",
        _ => Fmt.Bytes(v),
    };

    public void Update(AppGroup g, string metric, double shareFrac)
    {
        lastGroup = g;
        lastMetric = metric;
        share.Width = RowWidth * Math.Clamp(double.IsFinite(shareFrac) ? shareFrac : 0, 0, 1);

        name.Inlines.Clear();
        name.Inlines.Add(new Run(g.Name) { Foreground = Theme.Text });
        if (g.Members.Count > 1) name.Inlines.Add(new Run($"  {g.Members.Count}") { Foreground = Theme.Dim, FontSize = 10 });
        if (g.MaxThreadPct >= 90) name.Inlines.Add(new Run("  1 core maxed") { Foreground = Theme.Amber, FontSize = 10 });
        val.Text = FormatMetric(Metric(g, metric), metric);

        double cores = g.CpuPct * Environment.ProcessorCount / 100;
        main.ToolTip =
            $"{g.Name}  ·  {g.Members.Count} process{(g.Members.Count == 1 ? "" : "es")}\n\n" +
            $"CPU      {g.CpuPct:0.0}% of machine ({cores:0.0} cores)\n" +
            $"         busiest thread {g.MaxThreadPct:0}% of a core\n" +
            $"RAM      {Fmt.Bytes(g.Ram)}  (commit {Fmt.Bytes(g.Commit)})\n" +
            $"GPU      {g.GpuPct:0}%  ·  VRAM {Fmt.Bytes(g.Vram)}\n" +
            $"I/O      {Fmt.Rate(g.IoBps)}  ·  hard faults {g.HardFaultsPs:0}/s\n\n" +
            (expanded ? "Click to collapse" : "Click to see its processes");

        if (expanded) RenderKids(g, metric);
    }

    void RenderKids(AppGroup g, string metric)
    {
        kids.Children.Clear();
        var ms = g.Members.OrderByDescending(p => PMetric(p, metric)).ThenByDescending(p => p.PrivateWs).ToList();
        foreach (var p in ms.Take(8))
        {
            var row = new Grid { Height = 19 };
            Columns(row);
            var n = Cell(row, 0, 11);
            n.Inlines.Add(new Run(p.Name) { Foreground = Theme.Text2 });
            if (p.Summary.Length > 0) n.Inlines.Add(new Run("  " + p.Summary) { Foreground = Theme.Dim });
            var v = Cell(row, 1, 11);
            v.Text = FormatMetric(PMetric(p, metric), metric);
            v.Foreground = Theme.Text2;

            string cmd = p.CommandLine ?? "(command line not readable)";
            if (cmd.Length > 500) cmd = cmd[..500] + "…";
            row.ToolTip = new TextBlock
            {
                MaxWidth = 520,
                TextWrapping = TextWrapping.Wrap,
                Text = $"{p.Name}  ·  PID {p.Pid}\n" +
                       $"CPU {p.CpuPct:0.0}%  ·  busiest thread {p.MaxThreadPct:0}% of a core  ·  {p.Threads} threads\n" +
                       $"RAM {Fmt.Bytes(p.PrivateWs)}  ·  commit {Fmt.Bytes(p.Commit)}  ·  VRAM {Fmt.Bytes(p.Vram)}\n\n{cmd}",
            };
            kids.Children.Add(row);
        }
        if (ms.Count > 8)
            kids.Children.Add(new TextBlock { Text = $"+ {ms.Count - 8} more", FontSize = 10.5, Foreground = Theme.Dim, Margin = new Thickness(8, 1, 0, 0) });
    }
}

public partial class MainWindow : Window
{
    readonly Settings settings = Settings.Load();
    readonly ResourceRow cpuRow = new("CPU"), ramRow = new("RAM"), swapRow = new("SWAP"), gpuRow = new("GPU"), diskRow = new("DISK");
    readonly Dictionary<string, AppRow> appRows = new();
    List<string> shownKeys = new();
    static readonly string[] SortCycle = { "auto", "cpu", "ram", "gpu", "io" };
    static readonly Dictionary<string, string> MetricLabel = new() { ["cpu"] = "CPU", ["ram"] = "RAM", ["gpu"] = "GPU", ["io"] = "I/O" };
    string sortMode = "auto";
    Snapshot? last;
    WinForms.NotifyIcon? tray;
    int trayLevel = -1;
    volatile bool running = true;
    MenuItem? miCompact, miTop, miStartup;

    public MainWindow()
    {
        InitializeComponent();
        foreach (var r in new[] { cpuRow, ramRow, swapRow, gpuRow, diskRow }) ResourcePanel.Children.Add(r.Root);
        BuildMenu();
        BuildTray();
        PlaceWindow();
        ApplySettings();

        HeaderGrid.MouseLeftButtonDown += Drag;
        ResourcePanel.MouseLeftButtonDown += Drag;
        CompactBtn.Click += (_, _) => { settings.Compact = !settings.Compact; ApplySettings(); settings.Save(); };
        HideBtn.Click += (_, _) => Hide();
        SortLabel.MouseLeftButtonUp += (_, _) =>
        {
            sortMode = SortCycle[(Array.IndexOf(SortCycle, sortMode) + 1) % SortCycle.Length];
            if (last != null) RenderApps(last);
        };
        SourceInitialized += (_, _) =>
        {
            // tool window: no Alt+Tab entry
            var h = new WindowInteropHelper(this).Handle;
            Win32.SetWindowLong(h, -20, Win32.GetWindowLong(h, -20) | 0x80);
        };

        new Thread(Loop) { IsBackground = true, Name = "collector" }.Start();
    }

    // ------------------------------------------------------------------ window plumbing

    void PlaceWindow()
    {
        var wa = SystemParameters.WorkArea;
        double l = settings.Left ?? (wa.Right - Width - 16);
        double t = settings.Top ?? (wa.Top + 16);
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                          SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!vs.Contains(new Point(l + 40, t + 10))) { l = wa.Right - Width - 16; t = wa.Top + 16; }
        Left = l;
        Top = t;
    }

    void ApplySettings()
    {
        FullPanel.Visibility = settings.Compact ? Visibility.Collapsed : Visibility.Visible;
        CompactBtn.Content = settings.Compact ? "" : "";
        Topmost = settings.Topmost;
        Opacity = Math.Clamp(settings.Opacity, 0.3, 1);
    }

    void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
        settings.Left = Left;
        settings.Top = Top;
        settings.Save();
    }

    void BuildMenu()
    {
        var m = new ContextMenu();
        MenuItem Check(string header, Action<bool> set)
        {
            var mi = new MenuItem { Header = header, IsCheckable = true };
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
        miStartup = Check("Start with Windows", v => { try { Autostart.Set(v); } catch (Exception ex) { Log.Error(ex); } });

        var op = new MenuItem { Header = "Opacity" };
        foreach (var v in new[] { 1.0, 0.9, 0.8, 0.7 })
            op.Items.Add(Item($"{v * 100:0}%", () => { settings.Opacity = v; ApplySettings(); settings.Save(); }));
        m.Items.Add(op);

        var rows = new MenuItem { Header = "Apps shown" };
        foreach (var n in new[] { 3, 5, 8, 12 })
            rows.Items.Add(Item(n.ToString(), () => { settings.AppRows = n; settings.Save(); if (last != null) RenderApps(last); }));
        m.Items.Add(rows);

        m.Items.Add(new Separator());
        m.Items.Add(Item("Open history folder", () =>
        {
            System.IO.Directory.CreateDirectory(HistoryLog.Dir);
            Process.Start(new ProcessStartInfo(HistoryLog.Dir) { UseShellExecute = true });
        }));
        m.Items.Add(Item("Open Task Manager", () => Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })));
        m.Items.Add(Item("Open Resource Monitor", () => Process.Start(new ProcessStartInfo("resmon.exe") { UseShellExecute = true })));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Hide to tray", Hide));
        m.Items.Add(Item("Exit", ExitApp));

        m.Opened += (_, _) =>
        {
            miCompact!.IsChecked = settings.Compact;
            miTop!.IsChecked = settings.Topmost;
            miStartup!.IsChecked = Autostart.IsEnabled();
        };
        Shell.ContextMenu = m;
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
                System.IO.File.WriteAllText(path, text);
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error(ex); }
        });
    }

    void BuildTray()
    {
        tray = new WinForms.NotifyIcon { Text = "PerfWidget", Visible = true, Icon = MakeIcon(0) };
        tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ToggleVisible(); };
        var cms = new WinForms.ContextMenuStrip();
        cms.Items.Add("Show / hide", null, (_, _) => ToggleVisible());
        cms.Items.Add("Exit", null, (_, _) => ExitApp());
        tray.ContextMenuStrip = cms;
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
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        Application.Current.Shutdown();
    }

    static Drawing.Icon MakeIcon(int severity)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var br = new Drawing.SolidBrush(Theme.SevColor(severity));
            g.FillRectangle(br, 3, 18, 7, 11);
            g.FillRectangle(br, 12, 10, 7, 19);
            g.FillRectangle(br, 21, 3, 7, 26);
        }
        IntPtr h = bmp.GetHicon();
        var icon = (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone();
        Win32.DestroyIcon(h);
        return icon;
    }

    void UpdateTray(int level, string text)
    {
        if (tray == null) return;
        if (level != trayLevel)
        {
            var old = tray.Icon;
            tray.Icon = MakeIcon(level);
            old?.Dispose();
            trayLevel = level;
        }
        tray.Text = text.Length > 127 ? text[..126] + "…" : text;
    }

    // ------------------------------------------------------------------ sampling loop

    void Loop()
    {
        Collector collector;
        try { collector = new Collector(); }
        catch (Exception ex)
        {
            Log.Error(ex);
            Dispatcher.BeginInvoke(() => Headline.Text = "Collector failed to start: " + ex.Message);
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
                Dispatcher.BeginInvoke(() => Render(s, tempSource));
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

        // verdict
        var top = s.Issues.FirstOrDefault();
        int level = top?.Severity ?? 0;
        Headline.Text = top?.Title ?? "All clear";
        HeadDetail.Text = top?.Detail ?? "Nothing is holding the machine back.";
        StatusDot.Fill = Theme.Sev(level);

        IssuePanel.Children.Clear();
        foreach (var i in s.Issues.Skip(1).Take(2))
        {
            var tb = new TextBlock { FontSize = 11.5, Foreground = Theme.Text2, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = i.Detail, Margin = new Thickness(0, 1, 0, 0) };
            tb.Inlines.Add(new Run("● ") { Foreground = Theme.Sev(i.Severity), FontSize = 9 });
            tb.Inlines.Add(new Run(i.Title));
            IssuePanel.Children.Add(tb);
        }
        if (s.Issues.Count > 3)
            IssuePanel.Children.Add(new TextBlock { FontSize = 10.5, Foreground = Theme.Dim, Text = $"+ {s.Issues.Count - 3} more", ToolTip = string.Join("\n", s.Issues.Skip(3).Select(i => i.Text)) });
        IssuePanel.Visibility = IssuePanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        string metric = sortMode == "auto" ? AutoMetric(s.Bottleneck) : sortMode;

        // CPU: who's using it (or which app is stuck on one core), temp if known
        var pinned = s.Groups.Where(g => g.MaxThreadPct >= 90).OrderByDescending(g => g.MaxThreadPct).FirstOrDefault();
        var cpuTop = TopBy(s, g => g.CpuPct);
        string cpuWho = pinned != null && s.CpuPct < 75 ? $"{pinned.Name} maxing 1 core"
            : cpuTop != null && cpuTop.CpuPct >= 1 ? $"{cpuTop.Name}  {Fmt.Pct(cpuTop.CpuPct)}" : "idle";
        cpuRow.Update(s.CpuPct, (s.CpuTemp is double ct ? $"{ct:0}°C  ·  " : "") + cpuWho,
            s.Bottleneck == "cpu", pinned != null && s.CpuPct < 75 ? Theme.Amber : null);
        cpuRow.SetTip(
            $"CPU  {s.CpuPct:0}% of all {s.LogicalCpus} threads\n" +
            $"Clock {s.CpuMHz / 1000:0.00} GHz (base {s.CpuBaseMHz / 1000:0.00})  ·  firmware limit {s.CpuPerfLimit:0}%\n" +
            (s.CpuTemp is double t ? $"Temperature {t:0}°C (via {tempSource})\n" : "Temperature: not available. Run LibreHardwareMonitor and it shows up here.\n") +
            "\nTop:  " + string.Join(",  ", s.Groups.OrderByDescending(g => g.CpuPct).Take(4).Select(g => $"{g.Name} {Fmt.Pct(g.CpuPct)}")) +
            (pinned != null ? $"\n\n{pinned.Name} has one thread at {pinned.MaxThreadPct:0}% of a core:\n{pinned.MaxThreadProc}" : ""));

        // RAM
        double ramPct = s.RamTotal > 0 ? 100.0 * s.RamUsed / s.RamTotal : 0;
        var ramTop = TopBy(s, g => g.Ram);
        ramRow.Update(ramPct, ramTop != null ? $"{ramTop.Name}  {Fmt.Bytes(ramTop.Ram)}" : "", s.Bottleneck == "ram");
        ramRow.SetTip(
            $"RAM  {Fmt.GB(s.RamUsed)} of {Fmt.GB(s.RamTotal)} GB in use  ({Fmt.Bytes(s.Cache)} of cache Windows can free instantly)\n\n" +
            "Top:  " + string.Join(",  ", s.Groups.OrderByDescending(g => g.Ram).Take(4).Select(g => $"{g.Name} {Fmt.Bytes(g.Ram)}")));

        // SWAP (commit + paging)
        double commitPct = s.CommitLimit > 0 ? 100.0 * s.CommitTotal / s.CommitLimit : 0;
        bool swapping = s.PagesInBps >= 5 * Fmt.MiB && ramPct >= 80;
        swapRow.Update(commitPct,
            swapping ? $"swapping {Fmt.Rate(s.PagesInBps)}" : s.PagefileTotal > 0 ? $"page file {Fmt.Bytes(s.PagefileUsed)} used" : "no page file",
            s.Bottleneck == "commit", swapping ? Theme.Red : null);
        swapRow.SetTip(
            $"Virtual memory (commit): {Fmt.GB(s.CommitTotal)} of {Fmt.GB(s.CommitLimit)} GB reserved by programs, backed by RAM + page file.\n" +
            "At 100% new allocations fail and apps crash.\n\n" +
            $"Page file  {Fmt.Bytes(s.PagefileUsed)} of {Fmt.Bytes(s.PagefileTotal)} used\n" +
            $"Paging in {Fmt.Rate(s.PagesInBps)}  ·  out {Fmt.Rate(s.PagesOutBps)}  ·  hard faults {s.HardFaultsPs:0}/s\n" +
            "(Paging in only means swapping when RAM is also nearly full.)");

        // GPU
        var gpuTop = TopBy(s, g => g.GpuPct);
        gpuRow.Update(s.GpuPct,
            (s.GpuTemp is double gt ? $"{gt:0}°C" : "") +
            (gpuTop != null && gpuTop.GpuPct >= 5 ? (s.GpuTemp != null ? "  ·  " : "") + $"{gpuTop.Name}  {gpuTop.GpuPct:0}%" : ""),
            s.Bottleneck is "gpu" or "vram");
        gpuRow.SetTip(
            $"{s.GpuName}  {s.GpuPct:0}% (busiest engine)\n" +
            (s.VramTotal > 0 ? $"VRAM {Fmt.GB(s.VramUsed)} of {Fmt.GB(s.VramTotal)} GB\n" : "") +
            (s.GpuTemp is double gt2 ? $"Temperature {gt2:0}°C" + (s.GpuSlowdownTemp is double sd ? $" (slows down at {sd:0}°C)" : "") + "\n" : "") +
            (s.GpuPowerW is double pw ? $"Power {pw:0} of {s.GpuPowerLimitW:0} W  ·  clock {s.GpuClockMHz:0} MHz (max {s.GpuMaxClockMHz:0})  ·  fan {s.GpuFanPct:0}%\n" : "") +
            $"Clock limits active: {GpuSensors.Describe(s.GpuThrottleReasons) ?? "none"}\n\n" +
            "Top:  " + string.Join(",  ", s.Groups.Where(g => g.GpuPct >= 0.5 || g.Vram > 0).OrderByDescending(g => g.GpuPct).ThenByDescending(g => g.Vram).Take(4)
                .Select(g => $"{g.Name} {g.GpuPct:0}% ({Fmt.Bytes(g.Vram)})")));

        // DISK
        var busiest = s.Disks.OrderByDescending(d => d.BusyPct).FirstOrDefault();
        diskRow.Update(busiest?.BusyPct ?? 0, busiest == null ? "" : $"{busiest.Name}  {Fmt.Rate(busiest.Bps)}", s.Bottleneck == "disk");
        diskRow.SetTip(
            "Busiest physical disk (% of time servicing requests):\n" +
            string.Join("\n", s.Disks.Where(d => d.BusyPct >= 1 || d.Bps > 0).OrderByDescending(d => d.BusyPct).Select(d => $"  {d.Name}  {d.BusyPct:0}%  {Fmt.Rate(d.Bps)}")) +
            "\n\nMost I/O (file + network + pipes, so it can exceed the disk rate):\n  " +
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
        SortLabel.Text = $"{MetricLabel[metric]}{(sortMode == "auto" ? " · auto" : "")} ▾";

        var groups = s.Groups.OrderByDescending(g => AppRow.Metric(g, metric)).ThenByDescending(g => g.Ram)
                             .Take(Math.Max(1, settings.AppRows)).ToList();
        double maxIo = Math.Max(1, s.Groups.Count > 0 ? s.Groups.Max(g => g.IoBps) : 1);

        var keys = groups.Select(g => g.Key).ToList();
        if (!keys.SequenceEqual(shownKeys))
        {
            AppList.Children.Clear();
            foreach (var k in keys)
            {
                if (!appRows.TryGetValue(k, out var row)) appRows[k] = row = new AppRow();
                AppList.Children.Add(row.Root);
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
        DriveText.Inlines.Clear();
        DriveText.Inlines.Add(new Run("Free  ") { Foreground = Theme.Dim });
        bool firstDrive = true;
        foreach (var d in s.Drives)
        {
            double pctFree = d.Total > 0 ? 100.0 * d.Free / d.Total : 100;
            if (!firstDrive) DriveText.Inlines.Add(new Run("   "));
            firstDrive = false;
            DriveText.Inlines.Add(new Run(d.Name + " ") { Foreground = Theme.Dim });
            DriveText.Inlines.Add(new Run(Fmt.Bytes(d.Free))
            {
                Foreground = pctFree < 5 ? Theme.Red : pctFree < 10 ? Theme.Amber : Theme.Text2,
            });
        }
        DriveText.ToolTip =
            string.Join("\n", s.Drives.Select(d => $"{d.Name}  {Fmt.Bytes(d.Free)} free of {Fmt.Bytes(d.Total)}" + (d.HasPagefile ? "  (page file)" : ""))) +
            $"\n\n{s.ProcessCount} processes  ·  {s.ThreadCount:N0} threads  ·  this monitor uses {s.SelfCpuPct:0.0}% CPU";
    }
}
