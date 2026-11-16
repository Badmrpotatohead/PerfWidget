using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace PerfWidget;

static class Pal
{
    static IBrush B(uint argb) => new ImmutableSolidColorBrush(Color.FromUInt32(argb));

    public static readonly IBrush Text = B(0xFFE8EBF0), Text2 = B(0xFFB4BCC8), Dim = B(0xFF7C8596),
        Track = B(0x1AFFFFFF), Hover = B(0x12FFFFFF), Share = B(0x2E6CB4FF), Spark = B(0x80FFFFFF),
        Green = B(0xFF3DD68C), Amber = B(0xFFF5B841), Red = B(0xFFF0605D), Bg = B(0xFF101318), Border = B(0x2EFFFFFF),
        Clear = Brushes.Transparent;

    public static IBrush Level(double pct) => pct >= 90 ? Red : pct >= 70 ? Amber : Green;
    public static IBrush Sev(int s) => s >= 3 ? Red : s >= 2 ? Amber : Green;
    public static Color SevColor(int s) => s >= 3 ? Color.FromRgb(240, 96, 93) : s >= 2 ? Color.FromRgb(245, 184, 65) : Color.FromRgb(61, 214, 140);

    public static readonly Cursor Hand = new(StandardCursorType.Hand);
}

/// <summary>One line per resource: LABEL  [bar + 60 s sparkline]  value  who/extra. Details live in the tooltip.</summary>
sealed class ResourceRow
{
    const double BarW = 120, BarH = 10;
    public readonly Grid Root = new() { Height = 24, Background = Pal.Clear };
    readonly TextBlock value = new(), extra = new();
    readonly Rectangle fill = new() { RadiusX = 2.5, RadiusY = 2.5, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    readonly Polyline spark = new() { StrokeThickness = 1, Stroke = Pal.Spark, StrokeJoin = PenLineJoin.Round };
    readonly double[] hist = new double[60];
    int count;

    public ResourceRow(string label)
    {
        foreach (var w in new[] { 46.0, BarW + 8, 46 }) Root.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(w)));
        Root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        Root.Children.Add(new TextBlock { Text = label, Foreground = Pal.Dim, FontSize = 10.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var track = new Grid { Width = BarW, Height = BarH, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(new Rectangle { RadiusX = 2.5, RadiusY = 2.5, Fill = Pal.Track });
        track.Children.Add(fill);
        track.Children.Add(spark);
        Grid.SetColumn(track, 1);
        Root.Children.Add(track);

        value.FontSize = 12.5;
        value.FontWeight = FontWeight.SemiBold;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 2);
        Root.Children.Add(value);

        extra.FontSize = 11.5;
        extra.Margin = new Thickness(12, 0, 0, 0);
        extra.VerticalAlignment = VerticalAlignment.Center;
        extra.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(extra, 3);
        Root.Children.Add(extra);
    }

    public void Update(double pct, string extraText, bool highlight = false, IBrush? extraBrush = null, string? valueText = null)
    {
        pct = Math.Clamp(double.IsFinite(pct) ? pct : 0, 0, 100);
        Array.Copy(hist, 1, hist, 0, hist.Length - 1);
        hist[^1] = pct;
        count = Math.Min(hist.Length, count + 1);

        fill.Width = BarW * pct / 100;
        fill.Fill = Pal.Level(pct);
        var pts = new List<Point>(count);
        double step = BarW / (hist.Length - 1);
        for (int i = hist.Length - count; i < hist.Length; i++)
            pts.Add(new Point(i * step, BarH - 1 - (BarH - 2) * hist[i] / 100));
        spark.Points = pts;

        value.Text = valueText ?? $"{pct:0}%";
        extra.Text = extraText;
        extra.Foreground = extraBrush ?? (highlight ? Pal.Text : Pal.Text2);
    }

    public void SetTip(string tip) => ToolTip.SetTip(Root, tip);
}

/// <summary>An app in the list: name, one number (whatever the list is ranked by), share bar behind. Click to expand.</summary>
sealed class AppRow
{
    public const double RowWidth = 346;
    public readonly StackPanel Root = new();
    readonly Grid main = new() { Background = Pal.Clear, Cursor = Pal.Hand, Height = 24 };
    readonly Rectangle share = new() { HorizontalAlignment = HorizontalAlignment.Left, RadiusX = 3, RadiusY = 3, Margin = new Thickness(0, 2, 0, 2), Fill = Pal.Share };
    readonly TextBlock name, val;
    readonly StackPanel kids = new() { Margin = new Thickness(14, 0, 0, 6), IsVisible = false };
    bool expanded;
    AppGroup? lastGroup;
    string lastMetric = "ram";

    static void Columns(Grid g)
    {
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(72)));
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
        val.FontWeight = FontWeight.SemiBold;
        main.PointerEntered += (_, _) => main.Background = Pal.Hover;
        main.PointerExited += (_, _) => main.Background = Pal.Clear;
        main.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            expanded = !expanded;
            kids.IsVisible = expanded;
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

        var inl = new InlineCollection { new Run(g.Name) { Foreground = Pal.Text } };
        if (g.Members.Count > 1) inl.Add(new Run($"  {g.Members.Count}") { Foreground = Pal.Dim, FontSize = 10 });
        if (g.MaxThreadPct >= 90) inl.Add(new Run("  1 core maxed") { Foreground = Pal.Amber, FontSize = 10 });
        name.Inlines = inl;
        val.Text = FormatMetric(Metric(g, metric), metric);

        double cores = g.CpuPct * Environment.ProcessorCount / 100;
        ToolTip.SetTip(main,
            $"{g.Name}  ·  {g.Members.Count} process{(g.Members.Count == 1 ? "" : "es")}\n\n" +
            $"CPU      {g.CpuPct:0.0}% of machine ({cores:0.0} cores)\n" +
            $"         busiest thread {g.MaxThreadPct:0}% of a core\n" +
            $"RAM      {Fmt.Bytes(g.Ram)}  (commit {Fmt.Bytes(g.Commit)})\n" +
            $"GPU      {g.GpuPct:0}%  ·  VRAM {Fmt.Bytes(g.Vram)}\n" +
            $"I/O      {Fmt.Rate(g.IoBps)}  ·  hard faults {g.HardFaultsPs:0}/s\n\n" +
            (expanded ? "Click to collapse" : "Click to see its processes"));

        if (expanded) RenderKids(g, metric);
    }

    void RenderKids(AppGroup g, string metric)
    {
        kids.Children.Clear();
        var ms = g.Members.OrderByDescending(p => PMetric(p, metric)).ThenByDescending(p => p.PrivateWs).ToList();
        foreach (var p in ms.Take(8))
        {
            var row = new Grid { Height = 19, Background = Pal.Clear };
            Columns(row);
            var n = Cell(row, 0, 11);
            var inl = new InlineCollection { new Run(p.Name) { Foreground = Pal.Text2 } };
            if (p.Summary.Length > 0) inl.Add(new Run("  " + p.Summary) { Foreground = Pal.Dim });
            n.Inlines = inl;
            var v = Cell(row, 1, 11);
            v.Text = FormatMetric(PMetric(p, metric), metric);
            v.Foreground = Pal.Text2;

            string cmd = p.CommandLine ?? "(command line not readable)";
            if (cmd.Length > 500) cmd = cmd[..500] + "…";
            ToolTip.SetTip(row, new TextBlock
            {
                MaxWidth = 520,
                TextWrapping = TextWrapping.Wrap,
                Text = $"{p.Name}  ·  PID {p.Pid}\n" +
                       $"CPU {p.CpuPct:0.0}%  ·  busiest thread {p.MaxThreadPct:0}% of a core  ·  {p.Threads} threads\n" +
                       $"RAM {Fmt.Bytes(p.PrivateWs)}  ·  commit {Fmt.Bytes(p.Commit)}  ·  VRAM {Fmt.Bytes(p.Vram)}\n\n{cmd}",
            });
            kids.Children.Add(row);
        }
        if (ms.Count > 8)
            kids.Children.Add(new TextBlock { Text = $"+ {ms.Count - 8} more", FontSize = 10.5, Foreground = Pal.Dim, Margin = new Thickness(8, 1, 0, 0) });
    }
}
