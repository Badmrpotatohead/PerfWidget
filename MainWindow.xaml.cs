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
        Track = B(0x1FFFFFFF), Hover = B(0x14FFFFFF), Share = B(0x336CB4FF), Spark = B(0xB3FFFFFF),
        Green = B(0xFF3DD68C), Amber = B(0xFFF5B841), Red = B(0xFFF0605D);

    public static Brush Level(double pct) => pct >= 90 ? Red : pct >= 70 ? Amber : Green;
    public static Brush Sev(int s) => s >= 3 ? Red : s >= 2 ? Amber : Green;

    public static Drawing.Color SevColor(int s) =>
        s >= 3 ? Drawing.Color.FromArgb(240, 96, 93) : s >= 2 ? Drawing.Color.FromArgb(245, 184, 65) : Drawing.Color.FromArgb(61, 214, 140);
}

/// <summary>One resource line: label, bar with a 60-second sparkline, value, and "who is using it".</summary>
sealed class ResourceRow
{
    public const double BarW = 150, BarH = 14;
    public readonly Grid Root = new() { Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
    readonly TextBlock value = new(), detail = new();
    readonly Rectangle fill = new() { RadiusX = 3, RadiusY = 3, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    readonly Polyline spark = new() { StrokeThickness = 1, Stroke = Theme.Spark, StrokeLineJoin = PenLineJoin.Round };
    readonly double[] hist = new double[60];
    int count;

    public ResourceRow(string label, string tip)
    {
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarW + 10) });
        Root.ColumnDefinitions.Add(new ColumnDefinition());
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lab = new TextBlock { Text = label, Foreground = Theme.Dim, FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Root.Children.Add(lab);

        var track = new Grid { Width = BarW, Height = BarH, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(new Rectangle { RadiusX = 3, RadiusY = 3, Fill = Theme.Track });
        track.Children.Add(fill);
        track.Children.Add(spark);
        Grid.SetColumn(track, 1);
        Root.Children.Add(track);

        value.FontWeight = FontWeights.SemiBold;
        value.VerticalAlignment = VerticalAlignment.Center;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        Grid.SetColumn(value, 2);
        Root.Children.Add(value);

        detail.FontSize = 10.5;
        detail.Foreground = Theme.Dim;
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.Margin = new Thickness(0, 2, 0, 0);
        Grid.SetRow(detail, 1);
        Grid.SetColumn(detail, 1);
        Grid.SetColumnSpan(detail, 2);
        Root.Children.Add(detail);

        Root.ToolTip = tip;
    }

    public void Update(double pct, string valueText, string detailText)
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
            pts.Add(new Point(i * step, BarH - 1.5 - (BarH - 3) * hist[i] / 100));
        spark.Points = pts;

        value.Text = valueText;
        detail.Text = detailText;
        detail.Visibility = string.IsNullOrEmpty(detailText) ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>A row in the app table; click to expand into its processes.</summary>
sealed class AppRow
{
    public const double RowWidth = 390;
    public readonly StackPanel Root = new();
    readonly Grid main = new() { Background = Brushes.Transparent, Cursor = Cursors.Hand, Height = 22 };
    readonly Rectangle share = new() { HorizontalAlignment = HorizontalAlignment.Left, RadiusX = 3, RadiusY = 3, Margin = new Thickness(0, 2, 0, 2), Fill = Theme.Share };
    readonly TextBlock name, cpu, ram, gpu, io;
    readonly StackPanel kids = new() { Margin = new Thickness(12, 0, 0, 4), Visibility = Visibility.Collapsed };
    bool expanded;

    public static void SetupColumns(Grid g)
    {
        g.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var w in new[] { 52.0, 66, 44, 60 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
    }

    public static TextBlock Cell(Grid g, int col, double size = 12)
    {
        var t = new TextBlock
        {
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = new Thickness(col == 0 ? 6 : 0, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        Grid.SetColumn(t, col);
        g.Children.Add(t);
        return t;
    }

    public AppRow()
    {
        SetupColumns(main);
        Grid.SetColumnSpan(share, 5);
        main.Children.Add(share);
        name = Cell(main, 0); cpu = Cell(main, 1); ram = Cell(main, 2); gpu = Cell(main, 3); io = Cell(main, 4);
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

    AppGroup? lastGroup;
    string lastMetric = "ram";

    public static double Metric(AppGroup g, string m) => m switch
    {
        "cpu" => g.CpuPct, "gpu" => g.GpuPct, "io" => g.IoBps, _ => g.Ram,
    };

    static double PMetric(ProcInfo p, string m) => m switch
    {
        "cpu" => p.CpuPct, "gpu" => p.GpuPct, "io" => p.IoBps, _ => p.PrivateWs,
    };

    public void Update(AppGroup g, string metric, double shareFrac)
    {
        lastGroup = g;
        lastMetric = metric;
        share.Width = RowWidth * Math.Clamp(double.IsFinite(shareFrac) ? shareFrac : 0, 0, 1);

        name.Inlines.Clear();
        name.Inlines.Add(new Run(g.Name));
        if (g.Members.Count > 1) name.Inlines.Add(new Run($"  {g.Members.Count}") { Foreground = Theme.Dim, FontSize = 10 });

        cpu.Text = Fmt.Pct(g.CpuPct);
        ram.Text = Fmt.Bytes(g.Ram);
        gpu.Text = g.GpuPct >= 0.5 ? $"{g.GpuPct:0}%" : "–";
        io.Text = Fmt.Short(g.IoBps);
        name.Foreground = Theme.Text;
        cpu.Foreground = metric == "cpu" ? Theme.Text : Theme.Text2;
        ram.Foreground = metric == "ram" ? Theme.Text : Theme.Text2;
        gpu.Foreground = metric == "gpu" ? Theme.Text : Theme.Text2;
        io.Foreground = metric == "io" ? Theme.Text : Theme.Text2;

        double cores = g.CpuPct * Environment.ProcessorCount / 100;
        main.ToolTip =
            $"{g.Name}: {g.Members.Count} process{(g.Members.Count == 1 ? "" : "es")}\n" +
            $"RAM (private) {Fmt.Bytes(g.Ram)}  ·  commit {Fmt.Bytes(g.Commit)}\n" +
            $"CPU {g.CpuPct:0.0}% of machine ({cores:0.0} cores)\n" +
            $"GPU {g.GpuPct:0}%  ·  VRAM {Fmt.Bytes(g.Vram)}\n" +
            $"I/O {Fmt.Rate(g.IoBps)}  ·  hard page faults {g.HardFaultsPs:0}/s\n" +
            (expanded ? "Click to collapse" : "Click to see its processes");

        if (expanded) RenderKids(g, metric);
    }

    void RenderKids(AppGroup g, string metric)
    {
        kids.Children.Clear();
        var ms = g.Members.OrderByDescending(p => PMetric(p, metric)).ThenByDescending(p => p.PrivateWs).ToList();
        foreach (var p in ms.Take(10))
        {
            var row = new Grid { Height = 18 };
            SetupColumns(row);
            var n = Cell(row, 0, 10.5); n.Text = p.Name; n.Foreground = Theme.Text2;
            var c = Cell(row, 1, 10.5); c.Text = Fmt.Pct(p.CpuPct); c.Foreground = Theme.Dim;
            var r = Cell(row, 2, 10.5); r.Text = Fmt.Bytes(p.PrivateWs); r.Foreground = Theme.Dim;
            var gp = Cell(row, 3, 10.5); gp.Text = p.GpuPct >= 0.5 ? $"{p.GpuPct:0}%" : "–"; gp.Foreground = Theme.Dim;
            var i = Cell(row, 4, 10.5); i.Text = Fmt.Short(p.IoBps); i.Foreground = Theme.Dim;
            row.ToolTip = $"{p.Name}  ·  PID {p.Pid}\nRAM (private) {Fmt.Bytes(p.PrivateWs)}  ·  commit {Fmt.Bytes(p.Commit)}  ·  working set {Fmt.Bytes(p.WorkingSet)}\nVRAM {Fmt.Bytes(p.Vram)}  ·  threads {p.Threads}  ·  hard faults {p.HardFaultsPs:0}/s";
            kids.Children.Add(row);
        }
        if (ms.Count > 10)
            kids.Children.Add(new TextBlock { Text = $"+ {ms.Count - 10} more", FontSize = 10.5, Foreground = Theme.Dim, Margin = new Thickness(6, 1, 0, 0) });
    }
}

public partial class MainWindow : Window
{
    readonly Settings settings = Settings.Load();
    readonly ResourceRow cpuRow = new("CPU", "Share of all processor capacity in use. Below: the apps using it."),
        ramRow = new("RAM", "Physical memory in use (excludes standby cache Windows can drop instantly). Below: private RAM per app."),
        commitRow = new("COMMIT", "Memory programs have reserved, backed by RAM + page file. At 100% allocations fail and apps crash. Paging in/out = memory being read from / written to disk."),
        gpuRow = new("GPU", "Busiest GPU engine, plus dedicated video memory in use."),
        diskRow = new("DISK", "Busiest physical disk (% of time it is servicing requests) and its throughput.\nBelow: apps doing the most I/O. Per-app I/O includes file, network and pipe traffic, so it can exceed the disk's own rate.");
    readonly Dictionary<string, AppRow> appRows = new();
    List<string> shownKeys = new();
    readonly TextBlock[] headerCells = new TextBlock[5];
    static readonly string[] HeaderNames = { "APPS", "CPU", "RAM", "GPU", "I/O /s" };
    static readonly string[] HeaderKeys = { "auto", "cpu", "ram", "gpu", "io" };
    string sortMode = "auto";
    Snapshot? last;
    WinForms.NotifyIcon? tray;
    int trayLevel = -1;
    volatile bool running = true;
    MenuItem? miCompact, miTop, miStartup;

    public MainWindow()
    {
        InitializeComponent();
        foreach (var r in new[] { cpuRow, ramRow, commitRow, gpuRow, diskRow }) ResourcePanel.Children.Add(r.Root);
        BuildHeader();
        BuildMenu();
        BuildTray();
        PlaceWindow();
        ApplySettings();

        HeaderGrid.MouseLeftButtonDown += Drag;
        ResourcePanel.MouseLeftButtonDown += Drag;
        IssuePanel.MouseLeftButtonDown += Drag;
        CompactBtn.Click += (_, _) => { settings.Compact = !settings.Compact; ApplySettings(); settings.Save(); };
        HideBtn.Click += (_, _) => Hide();
        SourceInitialized += (_, _) =>
        {
            // tool window: no Alt+Tab entry
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, -20, Native.GetWindowLong(h, -20) | 0x80);
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

        miCompact = Check("Compact mode", v => settings.Compact = v);
        miTop = Check("Always on top", v => settings.Topmost = v);
        miStartup = Check("Start with Windows", v => { try { StartWithWindows.Set(v); } catch (Exception ex) { Log.Error(ex); } });

        var op = new MenuItem { Header = "Opacity" };
        foreach (var v in new[] { 1.0, 0.9, 0.8, 0.7 })
            op.Items.Add(Item($"{v * 100:0}%", () => { settings.Opacity = v; ApplySettings(); settings.Save(); }));
        m.Items.Add(op);

        var rows = new MenuItem { Header = "Apps shown" };
        foreach (var n in new[] { 5, 8, 12, 20 })
            rows.Items.Add(Item(n.ToString(), () => { settings.Rows = n; settings.Save(); if (last != null) RenderApps(last); }));
        m.Items.Add(rows);

        m.Items.Add(new Separator());
        m.Items.Add(Item("Open Task Manager", () => Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })));
        m.Items.Add(Item("Open Resource Monitor", () => Process.Start(new ProcessStartInfo("resmon.exe") { UseShellExecute = true })));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Hide to tray", Hide));
        m.Items.Add(Item("Exit", ExitApp));

        m.Opened += (_, _) =>
        {
            miCompact!.IsChecked = settings.Compact;
            miTop!.IsChecked = settings.Topmost;
            miStartup!.IsChecked = StartWithWindows.IsEnabled();
        };
        Shell.ContextMenu = m;
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
            // three rising bars
            g.FillRectangle(br, 3, 18, 7, 11);
            g.FillRectangle(br, 12, 10, 7, 19);
            g.FillRectangle(br, 21, 3, 7, 26);
        }
        IntPtr h = bmp.GetHicon();
        var icon = (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone();
        Native.DestroyIcon(h);
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
                Dispatcher.BeginInvoke(() => Render(s));
            }
            catch (Exception ex) { Log.Error(ex); }
        }
        collector.Dispose();
    }

    // ------------------------------------------------------------------ rendering

    static string TopList(Snapshot s, Func<AppGroup, double> f, Func<AppGroup, string> fmt, double min, int n = 3) =>
        string.Join("  ·  ", s.Groups.Where(g => f(g) >= min).OrderByDescending(f).Take(n).Select(g => $"{g.Name} {fmt(g)}"));

    void Render(Snapshot s)
    {
        last = s;

        var top = s.Issues.FirstOrDefault();
        int level = top?.Severity ?? 0;
        Headline.Text = top?.Text ?? "All clear: nothing is holding the machine back.";
        StatusDot.Fill = Theme.Sev(level);

        IssuePanel.Children.Clear();
        foreach (var i in s.Issues.Skip(1).Take(3))
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Theme.Text2, Margin = new Thickness(0, 0, 0, 2) };
            tb.Inlines.Add(new Run("● ") { Foreground = Theme.Sev(i.Severity), FontSize = 9 });
            tb.Inlines.Add(new Run(i.Text));
            IssuePanel.Children.Add(tb);
        }
        IssuePanel.Visibility = IssuePanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        cpuRow.Update(s.CpuPct, $"{s.CpuPct:0}%",
            TopList(s, g => g.CpuPct, g => Fmt.Pct(g.CpuPct), 0.5));

        double ramPct = s.RamTotal > 0 ? 100.0 * s.RamUsed / s.RamTotal : 0;
        ramRow.Update(ramPct, $"{Fmt.GB(s.RamUsed)} / {Fmt.GB(s.RamTotal)} GB",
            TopList(s, g => g.Ram, g => Fmt.Bytes(g.Ram), 50 * Fmt.MiB));

        double commitPct = s.CommitLimit > 0 ? 100.0 * s.CommitTotal / s.CommitLimit : 0;
        commitRow.Update(commitPct, $"{Fmt.GB(s.CommitTotal)} / {Fmt.GB(s.CommitLimit)} GB",
            (s.PagefileTotal > 0 ? $"page file {Fmt.Bytes(s.PagefileUsed)} of {Fmt.Bytes(s.PagefileTotal)}" : "no page file") +
            $"  ·  paging in {Fmt.Rate(s.PagesInBps)}  ·  out {Fmt.Rate(s.PagesOutBps)}");

        gpuRow.Update(s.GpuPct,
            s.VramTotal > 0 ? $"{s.GpuPct:0}%  ·  {Fmt.GB(s.VramUsed)}/{Fmt.GB(s.VramTotal)} GB" : $"{s.GpuPct:0}%",
            TopList(s, g => g.GpuPct, g => $"{g.GpuPct:0}%" + (g.Vram >= 256 * Fmt.MiB ? $" ({Fmt.Bytes(g.Vram)})" : ""), 1));

        var busiest = s.Disks.OrderByDescending(d => d.BusyPct).FirstOrDefault();
        diskRow.Update(busiest?.BusyPct ?? 0,
            busiest == null ? "–" : $"{busiest.Name} {busiest.BusyPct:0}%  ·  {Fmt.Rate(busiest.Bps)}",
            "most I/O: " + TopList(s, g => g.IoBps, g => Fmt.Rate(g.IoBps), Fmt.MiB));

        RenderApps(s);
        RenderDrives(s);
        FooterText.Text = $"{s.ProcessCount} processes  ·  {s.ThreadCount:N0} threads  ·  this monitor uses {s.SelfCpuPct:0.0}% CPU";
        FooterText.ToolTip = s.GpuName;
        UpdateTray(level, top?.Text ?? "PerfWidget: all clear");
    }

    static string AutoMetric(string bottleneck) => bottleneck switch
    {
        "cpu" => "cpu", "gpu" or "vram" => "gpu", "disk" => "io", _ => "ram",
    };

    void BuildHeader()
    {
        AppRow.SetupColumns(AppHeader);
        for (int i = 0; i < 5; i++)
        {
            var tb = AppRow.Cell(AppHeader, i, 10);
            tb.Text = HeaderNames[i];
            tb.FontWeight = FontWeights.SemiBold;
            tb.Foreground = Theme.Dim;
            tb.Cursor = Cursors.Hand;
            tb.Margin = new Thickness(i == 0 ? 6 : 0, 0, 6, 3);
            tb.ToolTip = i == 0
                ? "Auto sort follows whatever is the bottleneck right now. Click to return to auto."
                : $"Sort by {HeaderNames[i]} (click again for auto)";
            string key = HeaderKeys[i];
            tb.MouseLeftButtonUp += (_, _) =>
            {
                sortMode = sortMode == key || key == "auto" ? "auto" : key;
                if (last != null) RenderApps(last);
            };
            headerCells[i] = tb;
        }
    }

    void RenderApps(Snapshot s)
    {
        string metric = sortMode == "auto" ? AutoMetric(s.Bottleneck) : sortMode;
        for (int i = 1; i < 5; i++)
        {
            bool on = HeaderKeys[i] == metric;
            headerCells[i].Text = on ? HeaderNames[i] + " ▾" : HeaderNames[i];
            headerCells[i].Foreground = on ? Theme.Text : Theme.Dim;
        }
        headerCells[0].Text = sortMode == "auto" ? "APPS · auto" : "APPS";

        var groups = s.Groups.OrderByDescending(g => AppRow.Metric(g, metric)).ThenByDescending(g => g.Ram)
                             .Take(Math.Max(1, settings.Rows)).ToList();
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
        DrivePanel.Children.Clear();
        foreach (var d in s.Drives)
        {
            double pctFree = d.Total > 0 ? 100.0 * d.Free / d.Total : 100;
            var tb = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 14, 2) };
            tb.Inlines.Add(new Run(d.Name + " ") { FontWeight = FontWeights.SemiBold, Foreground = Theme.Text2 });
            tb.Inlines.Add(new Run($"{Fmt.Bytes(d.Free)} free")
            {
                Foreground = pctFree < 5 ? Theme.Red : pctFree < 10 ? Theme.Amber : Theme.Dim,
            });
            tb.ToolTip = $"{d.Name}  {Fmt.Bytes(d.Free)} free of {Fmt.Bytes(d.Total)} ({pctFree:0}% free)" +
                         (d.HasPagefile ? "\nHolds a page file: if this fills up, Windows can't grow virtual memory." : "");
            if (d.HasPagefile) tb.Inlines.Add(new Run(" ⇄") { Foreground = Theme.Dim, FontSize = 10 });
            DrivePanel.Children.Add(tb);
        }
    }
}
