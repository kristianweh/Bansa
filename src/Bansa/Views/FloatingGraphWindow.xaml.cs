using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Bansa.Services;
using Bansa.ViewModels;

using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using WinForms = System.Windows.Forms;

namespace Bansa.Views;

/// <summary>
/// Small always-on-top floating overlay: sparkline chart + live ping + top 5 apps
/// + optional hardware stats panel (CPU / GPU / RAM).
///
/// Drag:   WindowChrome CaptionHeight="28"
/// Resize: WindowChrome ResizeBorderThickness="6"
/// Snap:   LocationChanged nudges window to screen edge within SnapDistance DIPs.
/// </summary>
public partial class FloatingGraphWindow : Window
{
    private const double SnapDistance  = 16.0;
    private const double SparkWidth    = 190.0;   // below: chart-only spark mode
    private const double CompactWidth  = 260.0;   // below: apps list hidden

    private enum LayoutMode { Full, Compact, Spark }
    private LayoutMode _layoutMode = LayoutMode.Full;

    private IReadOnlyList<(long Down, long Up)> _history = Array.Empty<(long, long)>();
    private Color _downColor = Color.FromRgb(0x5D, 0xAD, 0xE2);
    private Color _upColor   = Color.FromRgb(0xF3, 0x9C, 0x12);

    // ── Cached chart brushes — recreated only when colors change ────────────
    // Grid/label/tick colours are sourced from the app theme on first draw,
    // so they adapt correctly to both light and dark themes.
    private System.Windows.Media.Brush? _gridBrush;
    private System.Windows.Media.Brush? _labelBrush;
    private System.Windows.Media.Brush? _tickBrush;

    // Color-dependent brushes — rebuilt when _downColor/_upColor changes.
    private SolidColorBrush? _downStroke, _upStroke, _downFill, _upFill;
    private Color _cachedDownColor, _cachedUpColor;

    // ── App list ─────────────────────────────────────────────────────────────
    private readonly ObservableCollection<AppRowViewModel> _topApps = new();
    private readonly Dictionary<string, (AppRowViewModel Vm, DateTime LastActive)> _appHold =
        new(StringComparer.OrdinalIgnoreCase);
    private const double HoldSeconds  = 8.0;
    private int _rankTick;
    private const int RankInterval = 10;

    // ── Chart smoothing ───────────────────────────────────────────────────────
    private double _smoothPeakDown = 1;
    private double _smoothPeakUp   = 1;

    // ── Ping history ─────────────────────────────────────────────────────────
    private readonly Queue<int> _pingHistory = new();
    private const int PingHistoryLen = 10;

    // ── Edge-snap guard ───────────────────────────────────────────────────────
    private bool _suppressLocationChanged;

    // ── Construction ──────────────────────────────────────────────────────────
    public FloatingGraphWindow()
    {
        InitializeComponent();
        TopAppsList.ItemsSource = _topApps;


        var s = App.Settings;
        Width  = Math.Max(220, s.FloatGraphW);
        Height = Math.Max(180, s.FloatGraphH);

        if (s.FloatGraphX >= 0 && s.FloatGraphY >= 0)
        {
            Left = s.FloatGraphX;
            Top  = s.FloatGraphY;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width  - 16;
            Top  = wa.Top   + 16;
        }

        Topmost = s.FloatGraphTopmost;

        // Apply initial section layout (Graph + Apps always visible; HW based on settings)
        InitSections(s.ShowHardwarePanel);

        // Subscribe to hardware monitor — fires every ~2 s on a background thread
        if (HardwareMonitor.Instance is { } hw)
        {
            hw.Sampled += snap =>
                Dispatcher.InvokeAsync(() => UpdateHardwareDisplay(snap));
        }

        LocationChanged += OnLocationChanged;
        SizeChanged     += OnSizeChanged;
        Closed          += OnClosed;

        // Invalidate cached decoration brushes when theme toggles
        ThemeManager.ThemeChanged += _ => { _gridBrush = null; _labelBrush = null; _tickBrush = null; };
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public void UpdateChart(IReadOnlyList<(long Down, long Up)> history,
                            Color downColor, Color upColor,
                            int pingMs = -1,
                            IEnumerable<AppRowViewModel>? apps = null)
    {
        _history   = history;

        // Invalidate cached brushes only when user changes colors
        if (downColor != _cachedDownColor || upColor != _cachedUpColor)
        {
            _cachedDownColor = downColor;
            _cachedUpColor   = upColor;
            _downColor = downColor;
            _upColor   = upColor;
            RebuildColorBrushes();
        }

        if (history.Count > 0)
        {
            var last     = history[^1];
            var downRate = Format.Rate(last.Down);
            var upRate   = Format.Rate(last.Up);
            var di = downRate.LastIndexOf(' ');
            var ui = upRate.LastIndexOf(' ');
            DownRateValue.Text = di >= 0 ? downRate[..di]       : downRate;
            DownRateUnit.Text  = di >= 0 ? downRate[(di + 1)..] : "";
            UpRateValue.Text   = ui >= 0 ? upRate[..ui]         : upRate;
            UpRateUnit.Text    = ui >= 0 ? upRate[(ui + 1)..]   : "";
        }

        UpdatePingDisplay(pingMs);

        if (apps is not null)
        {
            var now = DateTime.UtcNow;
            _rankTick++;

            foreach (var a in apps)
            {
                if (a.BytesInPerSec + a.BytesOutPerSec >= 5120)
                    _appHold[a.Name] = (a, now);
                else if (_appHold.TryGetValue(a.Name, out var ex))
                    _appHold[a.Name] = (a, ex.LastActive);
            }

            var stale = _appHold
                .Where(kv => (now - kv.Value.LastActive).TotalSeconds > HoldSeconds)
                .Select(kv => kv.Key).ToList();
            foreach (var k in stale) _appHold.Remove(k);

            var ranked = _appHold.Values
                .Select(v => v.Vm)
                .OrderByDescending(a => a.BytesInPerSec + a.BytesOutPerSec)
                .Take(5).ToList();

            for (int i = _topApps.Count - 1; i >= 0; i--)
                if (!ranked.Contains(_topApps[i])) _topApps.RemoveAt(i);

            if (_rankTick % RankInterval == 0)
            {
                for (int i = 0; i < ranked.Count; i++)
                {
                    var vm  = ranked[i];
                    int cur = _topApps.IndexOf(vm);
                    if (cur < 0)        _topApps.Insert(Math.Min(i, _topApps.Count), vm);
                    else if (cur != i)  _topApps.Move(cur, i);
                }
            }
            else
            {
                foreach (var vm in ranked)
                    if (!_topApps.Contains(vm)) _topApps.Add(vm);
            }
        }

        DrawChart();
    }

    // ── Brush helpers ──────────────────────────────────────────────────────────

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void RebuildColorBrushes()
    {
        _downStroke = Frozen(_downColor);
        _upStroke   = Frozen(_upColor);
        _downFill   = Frozen(Color.FromArgb(80, _downColor.R, _downColor.G, _downColor.B));
        _upFill     = Frozen(Color.FromArgb(48, _upColor.R,   _upColor.G,   _upColor.B));
    }

    private SolidColorBrush DownStroke => _downStroke ??= (_cachedDownColor == default
        ? (_downStroke = Frozen(_downColor)) : Frozen(_downColor));
    private SolidColorBrush UpStroke   => _upStroke   ??= Frozen(_upColor);
    private SolidColorBrush DownFill   => _downFill   ??= Frozen(Color.FromArgb(80, _downColor.R, _downColor.G, _downColor.B));
    private SolidColorBrush UpFill     => _upFill     ??= Frozen(Color.FromArgb(48, _upColor.R,   _upColor.G,   _upColor.B));

    // ── Color heat helpers ────────────────────────────────────────────────────
    private static Color LerpColor(Color a, Color b, double t)
        => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    private static Color ParseHex(string? hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        try { return (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }
    private static Color PingHeatColor(int ms)
    {
        var good = ParseHex(App.Settings?.PingGoodColorHex, Color.FromRgb(0x10, 0xB9, 0x81));
        var bad  = ParseHex(App.Settings?.PingBadColorHex,  Color.FromRgb(0xF4, 0x3F, 0x5E));
        return LerpColor(good, bad, Math.Clamp((ms - 40.0) / 80.0, 0, 1));
    }
    private static Color TempHeatColor(double tempC)
    {
        var cold = ParseHex(App.Settings?.TempColdColorHex, Color.FromRgb(0x70, 0xC8, 0xFF));
        var hot  = ParseHex(App.Settings?.TempHotColorHex,  Color.FromRgb(0xFF, 0x80, 0x80));
        return LerpColor(cold, hot, Math.Clamp((tempC - 50.0) / 40.0, 0, 1));
    }

    // ── Ping ──────────────────────────────────────────────────────────────────

    private void UpdatePingDisplay(int pingMs)
    {
        PingText.Text = pingMs < 0 ? "—" : $"{pingMs}";

        // Ping target label — show friendly label (if set) or raw address
        string pingTarget = "";
        var s = App.Settings;
        if (s?.PingTargetLabels != null &&
            s.PingTargetLabels.TryGetValue(s.PingTarget ?? "", out var lbl) &&
            !string.IsNullOrEmpty(lbl))
            pingTarget = lbl;
        else
            pingTarget = s?.PingTarget ?? "";
        PingTargetLabel.Text = pingTarget;

        if (pingMs < 0)
        {
            var muted = Application.Current.TryFindResource("MutedTextBrush") as System.Windows.Media.Brush
                        ?? new SolidColorBrush(Color.FromArgb(96, 128, 128, 128));
            PingDot.Fill             = muted;
            PingText.Foreground      = muted;
            FloatPingUnit.Foreground = muted;
            PingJitterText.Text      = "";
            return;
        }

        _pingHistory.Enqueue(pingMs);
        while (_pingHistory.Count > PingHistoryLen) _pingHistory.Dequeue();

        var brush = new SolidColorBrush(PingHeatColor(pingMs));
        PingDot.Fill             = brush;
        PingText.Foreground      = brush;
        FloatPingUnit.Foreground = brush;

        PingJitterText.Text = _pingHistory.Count >= 4
            ? $"±{_pingHistory.Max() - _pingHistory.Min()} ms"
            : (pingMs < 40 ? "great" : pingMs < 80 ? "ok" : "high");
    }

    // ── Chart rendering ────────────────────────────────────────────────────────

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();
        if (_history.Count < 2)
        {
            DownPeakLabel.Text = "";
            UpPeakLabel.Text   = "";
            return;
        }

        double w = ChartCanvas.ActualWidth;
        double h = ChartCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        long rawDown = 1, rawUp = 1;
        foreach (var (d, u) in _history)
        {
            if (d > rawDown) rawDown = d;
            if (u > rawUp)   rawUp   = u;
        }

        // EMA peak — rises fast on spike, decays slowly
        double aD = rawDown > _smoothPeakDown ? 0.4 : 0.06;
        double aU = rawUp   > _smoothPeakUp   ? 0.4 : 0.06;
        _smoothPeakDown = _smoothPeakDown * (1 - aD) + rawDown * aD;
        _smoothPeakUp   = _smoothPeakUp   * (1 - aU) + rawUp   * aU;
        long peakDown = Math.Max(1, (long)_smoothPeakDown);
        long peakUp   = Math.Max(1, (long)_smoothPeakUp);
        long peak     = Math.Max(peakDown, peakUp);

        DownPeakLabel.Text = "↓ " + Format.Rate(peakDown);
        UpPeakLabel.Text   = "↑ " + Format.Rate(peakUp);

        // Lazy-init theme-aware chart decoration brushes
        // Resolved from Application.Current.Resources so they match dark/light theme.
        // Re-resolved after a theme change (ThemeChanged clears them in the ctor handler).
        _gridBrush  ??= (Application.Current.TryFindResource("BorderBrush")      as System.Windows.Media.Brush)
                        ?? Frozen(Color.FromArgb(40, 128, 160, 192));
        _labelBrush ??= (Application.Current.TryFindResource("SubtleTextBrush") as System.Windows.Media.Brush)
                        ?? Frozen(Color.FromArgb(160, 130, 180, 210));
        _tickBrush  ??= (Application.Current.TryFindResource("BorderBrush")      as System.Windows.Media.Brush)
                        ?? Frozen(Color.FromArgb(50, 128, 160, 192));

        // ── Horizontal grid lines + Y-axis labels ───────────────────────────
        for (int li = 1; li <= 3; li++)
        {
            double y = h * li / 4.0;
            ChartCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = _gridBrush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            });
            long labelVal = (long)(peak * (4 - li) / 4.0);
            var lbl = new TextBlock
            {
                Text = Format.Rate(labelVal), FontSize = 8,
                Foreground = _labelBrush, IsHitTestVisible = false
            };
            Canvas.SetLeft(lbl, 2);
            Canvas.SetTop(lbl, y - 10);
            ChartCanvas.Children.Add(lbl);
        }

        // ── Time markers ────────────────────────────────────────────────────────
        int n = _history.Count;
        for (int sAgo = 10; sAgo < 30; sAgo += 10)
        {
            int sIdx = n - 1 - sAgo * 2;
            if (sIdx <= 0) continue;
            double tx = sIdx * (w / (n - 1));
            ChartCanvas.Children.Add(new Line
            {
                X1 = tx, Y1 = 0, X2 = tx, Y2 = h,
                Stroke = _tickBrush, StrokeThickness = 1
            });
            var tLbl = new TextBlock
            {
                Text = $"-{sAgo}s", FontSize = 8,
                Foreground = _labelBrush, IsHitTestVisible = false
            };
            Canvas.SetLeft(tLbl, tx + 2);
            Canvas.SetTop(tLbl, h - 14);
            ChartCanvas.Children.Add(tLbl);
        }

        // ── Sparklines ─────────────────────────────────────────────────────────
        var downPts = new List<Point>(n);
        var upPts   = new List<Point>(n);
        for (int i = 0; i < n; i++)
        {
            double x = n == 1 ? 0 : i * (w / (n - 1));
            downPts.Add(new Point(x, h - ((double)_history[i].Down / peak) * h * 0.90));
            upPts.Add(  new Point(x, h - ((double)_history[i].Up   / peak) * h * 0.90));
        }

        ChartCanvas.Children.Add(new Path { Data = SmoothPath(downPts, true,  h), Fill   = DownFill   });
        ChartCanvas.Children.Add(new Path { Data = SmoothPath(upPts,   true,  h), Fill   = UpFill     });
        ChartCanvas.Children.Add(new Path { Data = SmoothPath(downPts, false, h), Stroke = DownStroke, StrokeThickness = 1.8 });
        ChartCanvas.Children.Add(new Path { Data = SmoothPath(upPts,   false, h), Stroke = UpStroke,   StrokeThickness = 1.8 });

        // ── Live-rate dots ──────────────────────────────────────────────────────
        var dDot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = DownStroke };
        Canvas.SetLeft(dDot, downPts[^1].X - 3.5); Canvas.SetTop(dDot, downPts[^1].Y - 3.5);
        ChartCanvas.Children.Add(dDot);

        var uDot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = UpStroke };
        Canvas.SetLeft(uDot, upPts[^1].X - 3.5);   Canvas.SetTop(uDot, upPts[^1].Y - 3.5);
        ChartCanvas.Children.Add(uDot);
    }

    private static PathGeometry SmoothPath(List<Point> pts, bool fillToBottom, double h)
    {
        if (pts.Count < 2) return new PathGeometry();
        var fig = new PathFigure { StartPoint = pts[0], IsFilled = fillToBottom };
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p0 = i > 0 ? pts[i - 1] : pts[i];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = i + 2 < pts.Count ? pts[i + 2] : pts[i + 1];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
            fig.Segments.Add(new BezierSegment(c1, c2, p2, true));
        }
        if (fillToBottom)
        {
            fig.Segments.Add(new LineSegment(new Point(pts[^1].X, h), false));
            fig.Segments.Add(new LineSegment(new Point(pts[0].X,  h), false));
            fig.IsClosed = true;
        }
        return new PathGeometry(new[] { fig });
    }

    // ── Hardware stats panel ───────────────────────────────────────────────────

    private void UpdateHardwareDisplay(HardwareSnapshot snap)
    {
        if (HwSection.Visibility != Visibility.Visible) return;

        // ── CPU ──────────────────────────────────────────────────────────────
        CpuTempFloat.Text = snap.CpuTemp > 0 ? $"{snap.CpuTemp:0}" : "—";
        if (snap.CpuTemp > 0)
        {
            var cpuB = new SolidColorBrush(TempHeatColor(snap.CpuTemp));
            CpuTempFloat.Foreground  = cpuB;
            CpuTempDegree.Foreground = cpuB;
        }
        double cpuBgW = CpuBarBg.ActualWidth;
        CpuBarFill.Width = cpuBgW * snap.CpuLoad / 100.0;
        var cpuLines = new System.Text.StringBuilder();
        cpuLines.Append($"{snap.CpuLoad:0}%");
        if (snap.CpuTemp > 0) cpuLines.Append($"  {snap.CpuTemp:0}°C");
        if (snap.CpuFreqMHz > 0) cpuLines.Append($"\n{snap.CpuFreqMHz / 1000f:0.0} GHz");
        CpuStatsText.Text = cpuLines.ToString();

        // ── GPU ──────────────────────────────────────────────────────────────
        GpuTempFloat.Text = snap.GpuTemp > 0 ? $"{snap.GpuTemp:0}" : "—";
        if (snap.GpuTemp > 0)
        {
            var gpuB = new SolidColorBrush(TempHeatColor(snap.GpuTemp));
            GpuTempFloat.Foreground  = gpuB;
            GpuTempDegree.Foreground = gpuB;
        }
        double gpuBgW = GpuBarBg.ActualWidth;
        GpuBarFill.Width = gpuBgW * snap.GpuLoad / 100.0;
        var gpuLines = new System.Text.StringBuilder();
        gpuLines.Append($"{snap.GpuLoad:0}%");
        if (snap.GpuTemp > 0) gpuLines.Append($"  {snap.GpuTemp:0}°C");
        if (snap.GpuPowerW > 0) gpuLines.Append($"  {snap.GpuPowerW:0}W");
        if (snap.GpuCoreMHz > 0) gpuLines.Append($"\n{snap.GpuCoreMHz:0} MHz");
        GpuStatsText.Text = gpuLines.ToString();

        // Tooltip shows the full GPU name + fan if available
        string gpuTip = snap.GpuName.Length > 0 ? snap.GpuName : "";
        if (snap.GpuFanRpm > 0) gpuTip += $"  ·  Fan {snap.GpuFanRpm:0} RPM";
        GpuRow.ToolTip = gpuTip.Length > 0 ? gpuTip : null;

        // ── VRAM sub-row (inside GPU box) ─────────────────────────────────────
        if (snap.GpuVramTotalMb > 0)
        {
            VramRow.Visibility = Visibility.Visible;
            double vramBgW = VramBarBg.ActualWidth;
            VramBarFill.Width = vramBgW * snap.GpuVramUsedMb / snap.GpuVramTotalMb;
            VramStatsText.Text = $"{snap.GpuVramUsedMb / 1024f:0.1}/{snap.GpuVramTotalMb / 1024f:0.0}G";
        }
        else
        {
            VramRow.Visibility = Visibility.Collapsed;
        }

        // ── RAM ──────────────────────────────────────────────────────────────
        double ramBgW = RamBarBg.ActualWidth;
        if (snap.RamTotalGb > 0)
        {
            RamBarFill.Width = ramBgW * snap.RamUsedGb / snap.RamTotalGb;
            string ramSpeed = snap.RamSpeedMHz > 0 ? $"  {snap.RamSpeedMHz:0}MHz" : "";
            RamStatsText.Text = $"{snap.RamUsedGb:0.0}/{snap.RamTotalGb:0.0}GB{ramSpeed}";
        }
        else
        {
            RamBarFill.Width = 0;
            RamStatsText.Text = "—";
        }
    }

    // ── Section layout initializer ─────────────────────────────────────────────

    /// <summary>
    /// Sets initial row heights and section visibility.
    /// Graph and Apps are always shown; the hardware panel respects the persisted setting.
    /// Sections are resizable via the GridSplitters.
    /// </summary>
    private void InitSections(bool showHw)
    {
        // Graph — always visible
        GraphSection.Visibility = Visibility.Visible;
        GraphRow.Height    = new GridLength(3, GridUnitType.Star);
        GraphRow.MinHeight = 130;  // chart (≥50) + rates strip (40) + ping strip (40)

        // Splitter 1 — always visible (Graph and Apps are always present)
        Splitter1.Visibility = Visibility.Visible;
        S1Row.Height = new GridLength(8);

        // Apps — always visible
        AppsSection.Visibility = Visibility.Visible;
        AppsRow.Height    = new GridLength(2, GridUnitType.Star);
        AppsRow.MinHeight = 60;

        // Hardware — driven by persisted setting
        HwSection.Visibility = showHw ? Visibility.Visible : Visibility.Collapsed;
        HwRow.Height    = showHw ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        HwRow.MinHeight = showHw ? 60 : 0;

        // Splitter 2 — only useful when HW panel is visible
        Splitter2.Visibility = showHw ? Visibility.Visible : Visibility.Collapsed;
        S2Row.Height = showHw ? new GridLength(8) : new GridLength(0);

        // Populate HW immediately if data already exists
        if (showHw &&
            HardwareMonitor.Instance is { Latest: var snap } &&
            snap != HardwareSnapshot.Empty)
        {
            UpdateHardwareDisplay(snap);
        }
    }

    // ── Edge snapping ──────────────────────────────────────────────────────────

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (_suppressLocationChanged) return;
        SnapToEdges();
        PersistGeometry();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        PersistGeometry();

        // Switch layout mode based on window width — content reflows instead of scaling.
        double w = e.NewSize.Width;
        LayoutMode target = w < SparkWidth   ? LayoutMode.Spark
                          : w < CompactWidth ? LayoutMode.Compact
                                             : LayoutMode.Full;
        if (target != _layoutMode)
        {
            _layoutMode = target;
            ApplyLayoutMode(target);
        }

        // Keep WindowChrome caption height constant — no scaling needed.
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome != null) chrome.CaptionHeight = 48.0;

        Dispatcher.BeginInvoke(DrawChart, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ApplyLayoutMode(LayoutMode mode)
    {
        switch (mode)
        {
            case LayoutMode.Full:
                UpRatePanel.Visibility      = Visibility.Visible;
                Splitter1.Visibility       = Visibility.Visible;
                S1Row.Height               = new GridLength(8);
                AppsSection.Visibility     = Visibility.Visible;
                AppsRow.Height             = new GridLength(2, GridUnitType.Star);
                AppsRow.MinHeight          = 60;
                PingTargetLabel.Visibility = Visibility.Visible;
                PingJitterText.Visibility  = Visibility.Visible;
                if (HwSection.Visibility == Visibility.Visible)
                {
                    HwRow.Height         = new GridLength(2, GridUnitType.Star);
                    HwRow.MinHeight      = 60;
                    Splitter2.Visibility = Visibility.Visible;
                    S2Row.Height         = new GridLength(8);
                }
                break;

            case LayoutMode.Compact:
                UpRatePanel.Visibility      = Visibility.Visible;
                Splitter1.Visibility       = Visibility.Visible;
                S1Row.Height               = new GridLength(8);
                AppsSection.Visibility     = Visibility.Visible;
                AppsRow.Height             = new GridLength(2, GridUnitType.Star);
                AppsRow.MinHeight          = 60;
                PingTargetLabel.Visibility = Visibility.Visible;
                PingJitterText.Visibility  = Visibility.Visible;
                if (HwSection.Visibility == Visibility.Visible)
                {
                    HwRow.Height         = GridLength.Auto;
                    HwRow.MinHeight      = 0;
                    Splitter2.Visibility = Visibility.Collapsed;
                    S2Row.Height         = new GridLength(4);
                }
                break;

            case LayoutMode.Spark:
                UpRatePanel.Visibility      = Visibility.Collapsed;
                Splitter1.Visibility       = Visibility.Visible;
                S1Row.Height               = new GridLength(8);
                AppsSection.Visibility     = Visibility.Visible;
                AppsRow.Height             = new GridLength(2, GridUnitType.Star);
                AppsRow.MinHeight          = 60;
                PingTargetLabel.Visibility = Visibility.Collapsed;
                PingJitterText.Visibility  = Visibility.Collapsed;
                if (HwSection.Visibility == Visibility.Visible)
                {
                    HwRow.Height         = GridLength.Auto;
                    HwRow.MinHeight      = 0;
                    Splitter2.Visibility = Visibility.Collapsed;
                    S2Row.Height         = new GridLength(4);
                }
                break;
        }
    }

    private void SnapToEdges()
    {
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is null) return;

        double dpiX = src.CompositionTarget.TransformToDevice.M11;
        double dpiY = src.CompositionTarget.TransformToDevice.M22;

        int physLeft   = (int)(Left          * dpiX);
        int physTop    = (int)(Top           * dpiY);
        int physRight  = (int)((Left + Width)  * dpiX);
        int physBottom = (int)((Top  + Height) * dpiY);

        int cx = (physLeft + physRight)  / 2;
        int cy = (physTop  + physBottom) / 2;
        var screen = WinForms.Screen.FromPoint(new System.Drawing.Point(cx, cy));
        var wa = screen.WorkingArea;

        int snapPx = (int)(SnapDistance * Math.Max(dpiX, dpiY));
        int newPhysLeft = physLeft;
        int newPhysTop  = physTop;
        bool snapped    = false;

        if      (Math.Abs(physLeft   - wa.Left)   < snapPx) { newPhysLeft = wa.Left;                            snapped = true; }
        else if (Math.Abs(physRight  - wa.Right)  < snapPx) { newPhysLeft = wa.Right  - (physRight - physLeft); snapped = true; }

        if      (Math.Abs(physTop    - wa.Top)    < snapPx) { newPhysTop  = wa.Top;                             snapped = true; }
        else if (Math.Abs(physBottom - wa.Bottom) < snapPx) { newPhysTop  = wa.Bottom - (physBottom - physTop); snapped = true; }

        if (snapped)
        {
            _suppressLocationChanged = true;
            Left = newPhysLeft / dpiX;
            Top  = newPhysTop  / dpiY;
            _suppressLocationChanged = false;
        }
    }

    // ── Geometry persistence ───────────────────────────────────────────────────

    private void PersistGeometry()
    {
        if (App.Settings is null) return;
        App.Settings.FloatGraphX = Left;
        App.Settings.FloatGraphY = Top;
        App.Settings.FloatGraphW = Width;
        App.Settings.FloatGraphH = Height;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        PersistGeometry();
        // ShowFloatingGraph / ShowHardwarePanel persistence is managed by MainWindow.
    }

    // ── Drag (rates bar acts as the drag handle) ──────────────────────────────

    private void OnRatesBarDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static void BringBansaToFront()
    {
        var main = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (main is null) return;
        main.Show();
        if (main.WindowState == WindowState.Minimized)
            main.WindowState = WindowState.Normal;
        main.Activate();
        main.Topmost = true;
        main.Topmost = false;
    }

    private void OnOpenBansaMenuClick(object sender, RoutedEventArgs e) => BringBansaToFront();


    // ── Window right-click context menu ───────────────────────────────────────

    /// <summary>Sync check states before the menu appears.</summary>
    private void OnWindowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        AlwaysOnTopMenu.IsChecked = Topmost;
        ShowSystemMenu.IsChecked  = HwSection.Visibility == Visibility.Visible;
    }

    private void OnAlwaysOnTopMenuClick(object sender, RoutedEventArgs e)
    {
        bool pinned = AlwaysOnTopMenu.IsChecked;
        Topmost = pinned;
        if (App.Settings is not null)
            App.Settings.FloatGraphTopmost = pinned;
    }

    private void OnShowSystemMenuClick(object sender, RoutedEventArgs e)
    {
        bool show = ShowSystemMenu.IsChecked;
        if (App.Settings is not null)
            App.Settings.ShowHardwarePanel = show;
        SettingsManager.Save(App.Settings!);
        InitSections(show);
    }

    // ── Floating chart crosshair ───────────────────────────────────────────────

    /// <summary>
    /// Draws a vertical crosshair line at the hovered history index and a compact
    /// tooltip showing ↓/↑ rates at that point. All elements are added directly to
    /// FloatChartOverlay.Children so they sit on top of ChartCanvas without z-order
    /// juggling. Cleared on MouseLeave.
    /// </summary>
    private void OnFloatChartMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        FloatChartOverlay.Children.Clear();
        if (_history.Count < 2) return;

        var pos = e.GetPosition(FloatChartOverlay);
        double w = FloatChartOverlay.ActualWidth;
        double h = FloatChartOverlay.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Map X position → history index
        int idx = (int)Math.Round(pos.X / w * (_history.Count - 1));
        idx = Math.Clamp(idx, 0, _history.Count - 1);

        // Vertical crosshair line at the exact sample position
        double lineX = idx * (w / (_history.Count - 1));
        FloatChartOverlay.Children.Add(new Line
        {
            X1 = lineX, Y1 = 0, X2 = lineX, Y2 = h,
            Stroke          = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        });

        // Small dot at crosshair top
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 5, Height = 5,
            Fill = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(dot, lineX - 2.5);
        Canvas.SetTop(dot, 0);
        FloatChartOverlay.Children.Add(dot);

        // Tooltip panel — ↓ down / ↑ up at that index
        var (down, up) = _history[idx];
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        panel.Children.Add(new TextBlock
        {
            Text       = "↓ " + Format.Rate(down),
            FontSize   = 10, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_downColor)
        });
        panel.Children.Add(new TextBlock
        {
            Text       = "↑ " + Format.Rate(up),
            FontSize   = 10, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_upColor)
        });

        var tooltip = new Border
        {
            Background   = new SolidColorBrush(Color.FromArgb(210, 18, 18, 28)),
            CornerRadius = new CornerRadius(5),
            Padding      = new Thickness(7, 4, 7, 4),
            Child        = panel,
            IsHitTestVisible = false
        };

        // Measure so we know size before positioning
        tooltip.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double tipW = tooltip.DesiredSize.Width;
        double tipH = tooltip.DesiredSize.Height;

        // Prefer right of crosshair; flip left if it would overflow
        double tipX = lineX + 8;
        if (tipX + tipW > w) tipX = lineX - tipW - 8;

        // Prefer above cursor; clamp to top edge
        double tipY = Math.Max(2, pos.Y - tipH - 6);

        Canvas.SetLeft(tooltip, Math.Clamp(tipX, 0, Math.Max(0, w - tipW)));
        Canvas.SetTop(tooltip,  tipY);
        FloatChartOverlay.Children.Add(tooltip);
    }

    private void OnFloatChartMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        FloatChartOverlay.Children.Clear();
    }
}
