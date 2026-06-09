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

namespace Bansa.Views;

/// <summary>
/// Small always-on-top floating overlay: sparkline chart + live ping + top 5 apps
/// + optional hardware stats panel (CPU / GPU / RAM).
///
/// Drag:   rates bar + bottom mode-switch bar (DragMove)
/// Resize: WindowChrome ResizeBorderThickness="6"
/// </summary>
public partial class FloatingGraphWindow : Window
{
    private const double SparkWidth    = 190.0;   // below: chart-only spark mode
    private const double CompactWidth  = 260.0;   // below: apps list hidden
    private const double TabIconWidth  = 320.0;   // below: mode tabs show icons, not text
    private bool _floatTabsIcon;

    private enum LayoutMode { Full, Compact, Spark }
    private LayoutMode _layoutMode = LayoutMode.Full;

    // Mockup HUD tabs: Net = graph+ping+apps · Temp = thermals · Both = everything
    private enum FloatMode { Net, Temp, Both }

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

    // ── Temp history for the Temp-tab gauges + overlaid CPU/GPU graph ──────────
    private const int FloatTempLen = 60;
    private readonly float[] _fCpuTemp = new float[FloatTempLen];
    private readonly float[] _fGpuTemp = new float[FloatTempLen];
    private int _fTempHead, _fTempCount;

    // ── Edge-snap guard (kept for future use; snap removed per user request) ──
    // private bool _suppressLocationChanged;

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
            // Clamp: the saved position might be off-screen on a different PC
            // (fewer monitors, different DPI, different resolution).
            EnsureOnScreen();
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width  - 16;
            Top  = wa.Top   + 16;
        }

        Topmost = s.FloatGraphTopmost;

        // Select the saved view mode tab — its Checked handler applies the section layout.
        var mode = Enum.TryParse<FloatMode>(s.FloatViewMode, out var fm) ? fm : FloatMode.Net;
        switch (mode)
        {
            case FloatMode.Temp: TabTemp.IsChecked = true; break;
            case FloatMode.Both: TabBoth.IsChecked = true; break;
            default:             TabNet.IsChecked  = true; break;
        }

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

    // How many samples the floating graph displays (same 30 s window as pre-scroll-feature).
    private const int FloatWindowSize = 60;

    public void UpdateChart(IReadOnlyList<(long Down, long Up)> history,
                            Color downColor, Color upColor,
                            int pingMs = -1,
                            IEnumerable<AppRowViewModel>? apps = null)
    {
        // Always show only the latest 60 samples (30 s) — the main chart may now carry
        // up to 7 200 samples for its drag-scroll feature, but the float graph keeps its
        // original fixed 30-second window.
        if (history.Count > FloatWindowSize)
        {
            int start = history.Count - FloatWindowSize;
            var buf = new (long Down, long Up)[FloatWindowSize];
            for (int i = 0; i < FloatWindowSize; i++) buf[i] = history[start + i];
            _history = buf;
        }
        else
        {
            _history = history;
        }

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

            // Mirror the Network tab's threshold filter: the passed list is already
            // text/hide-local filtered upstream, and this floor matches HideBelowKBps so
            // the overlay shows the same apps the Network tab does (0 = any active app).
            long floor = (long)((App.Settings?.HideBelowKBps ?? 0) * 1024);
            foreach (var a in apps)
            {
                long activity = a.BytesInPerSec + a.BytesOutPerSec;
                if (activity > 0 && activity >= floor)
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
    // Cool (user-set "blue") → bright yellow → bright red. See Services/HeatColors.
    private static Color TempHeatColor(double tempC) => HeatColors.Temp(tempC);

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

        // Smooth the plotted series so steady transfers draw as a flat line (the raw
        // _history is kept intact for the live rate readout above).
        var draw = ChartFx.Smooth(_history);

        long rawDown = 1, rawUp = 1;
        foreach (var (d, u) in draw)
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
        int n = draw.Count;
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
            downPts.Add(new Point(x, h - ((double)draw[i].Down / peak) * h * 0.90));
            upPts.Add(  new Point(x, h - ((double)draw[i].Up   / peak) * h * 0.90));
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
        // Accumulate temp history even while the section is hidden so the graph is
        // ready the instant the user switches to the Temp/Both tab.
        if (snap != HardwareSnapshot.Empty)
        {
            _fCpuTemp[_fTempHead] = snap.CpuTemp > 0 ? (float)snap.CpuTemp : 0f;
            _fGpuTemp[_fTempHead] = snap.GpuTemp > 0 ? (float)snap.GpuTemp : 0f;
            _fTempHead = (_fTempHead + 1) % FloatTempLen;
            if (_fTempCount < FloatTempLen) _fTempCount++;
        }

        if (HwSection.Visibility != Visibility.Visible) return;

        // Gauge ring uses each component's assigned color (matches the Hardware tab); the
        // value text stays thermal-heat-colored so temperature still reads hot/cold at a glance.
        var cpuRing = ChartColorOf("ChartCpuBrush", Color.FromRgb(0x5D, 0xAD, 0xE2));
        var gpuRing = ChartColorOf("ChartGpuBrush", Color.FromRgb(0xFF, 0x88, 0x32));

        // ── CPU gauge ──────────────────────────────────────────────────────────
        if (snap.CpuTemp > 0)
        {
            CpuTempFloat.Text = $"{snap.CpuTemp:0}°";
            CpuTempFloat.Foreground = new SolidColorBrush(TempHeatColor(snap.CpuTemp));
            CpuLoadFloat.Text = $"load {snap.CpuLoad:0}%";
            DrawFloatGauge(CpuGaugeFloat, snap.CpuTemp, 30, 95, cpuRing);
        }
        else { CpuTempFloat.Text = "—"; CpuLoadFloat.Text = "load —%"; DrawFloatGauge(CpuGaugeFloat, 0, 30, 95, default); }

        // ── GPU gauge ──────────────────────────────────────────────────────────
        if (snap.GpuTemp > 0)
        {
            GpuTempFloat.Text = $"{snap.GpuTemp:0}°";
            GpuTempFloat.Foreground = new SolidColorBrush(TempHeatColor(snap.GpuTemp));
            GpuLoadFloat.Text = $"load {snap.GpuLoad:0}%";
            DrawFloatGauge(GpuGaugeFloat, snap.GpuTemp, 30, 95, gpuRing);
        }
        else { GpuTempFloat.Text = "—"; GpuLoadFloat.Text = "load —%"; DrawFloatGauge(GpuGaugeFloat, 0, 30, 95, default); }

        DrawFloatTempChart();

        // ── RAM (unchanged) ──────────────────────────────────────────────────
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

    private void OnFloatGaugeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (HardwareMonitor.Instance is { Latest: var snap } && snap != HardwareSnapshot.Empty)
            UpdateHardwareDisplay(snap);
    }

    private static Color ChartColorOf(string key, Color fb)
        => (Application.Current.TryFindResource(key) as SolidColorBrush)?.Color ?? fb;

    /// <summary>270° radial gauge: subtle track + value arc (gap at the bottom), matching the Hardware tab.</summary>
    private void DrawFloatGauge(Canvas c, double value, double min, double max, Color color)
    {
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double cx = w / 2, cy = h / 2, r = Math.Min(w, h) / 2 - 6;
        if (r <= 0) return;

        var track = Application.Current.TryFindResource("BgBrush") as System.Windows.Media.Brush
                    ?? Frozen(Color.FromRgb(0x0B, 0x0C, 0x11));
        AddFloatArc(c, cx, cy, r, 225, 270, track, 7);

        double frac = max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;
        if (frac > 0.002) AddFloatArc(c, cx, cy, r, 225, 270 * frac, new SolidColorBrush(color), 7);
    }

    private static void AddFloatArc(Canvas c, double cx, double cy, double r,
                                    double startDeg, double sweepDeg, System.Windows.Media.Brush stroke, double thick)
    {
        double a0 = startDeg * Math.PI / 180.0;
        double a1 = (startDeg - sweepDeg) * Math.PI / 180.0;   // clockwise = decreasing angle
        var p0 = new Point(cx + r * Math.Cos(a0), cy - r * Math.Sin(a0));
        var p1 = new Point(cx + r * Math.Cos(a1), cy - r * Math.Sin(a1));
        var fig = new PathFigure { StartPoint = p0, IsFilled = false };
        fig.Segments.Add(new ArcSegment(p1, new System.Windows.Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true));
        c.Children.Add(new Path
        {
            Data = new PathGeometry(new[] { fig }),
            Stroke = stroke, StrokeThickness = thick,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        });
    }

    /// <summary>CPU + GPU temperature lines overlaid on one shared time axis.</summary>
    private void DrawFloatTempChart()
    {
        var canvas = FloatTempChart;
        canvas.Children.Clear();
        int count = _fTempCount, head = _fTempHead, len = FloatTempLen;
        if (count < 2) return;
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        float dMin = float.MaxValue, dMax = float.MinValue;
        void Scan(float[] b)
        {
            for (int i = 0; i < count; i++)
            {
                float v = b[(head - count + i + len) % len];
                if (v > 0) { if (v < dMin) dMin = v; if (v > dMax) dMax = v; }
            }
        }
        Scan(_fCpuTemp); Scan(_fGpuTemp);
        if (dMax < dMin) { dMin = 0; dMax = 1; }
        if (dMax <= dMin) dMax = dMin + 1;
        float range = dMax - dMin, sMin = dMin - range * 0.12f, sMax = dMax + range * 0.12f;

        var grid = Application.Current.TryFindResource("BorderBrush") as System.Windows.Media.Brush
                   ?? Frozen(Color.FromArgb(40, 255, 255, 255));
        for (int g = 1; g <= 2; g++)
        {
            double yy = h * g / 3.0;
            canvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = yy, Y2 = yy, Stroke = grid, StrokeThickness = 1 });
        }

        void Draw(float[] b, Color col)
        {
            // Collect contiguous runs (gap wherever a sample is unavailable).
            var runs = new List<List<Point>>();
            List<Point>? cur = null;
            Point last = new(double.NaN, double.NaN);
            for (int i = 0; i < count; i++)
            {
                float v = b[(head - count + i + len) % len];
                if (v <= 0) { cur = null; continue; }
                double x = i * (w / (count - 1));
                double y = h - ((v - sMin) / (sMax - sMin)) * h;
                var p = new Point(x, Math.Clamp(y, 0, h));
                if (cur == null) { cur = new List<Point>(); runs.Add(cur); }
                cur.Add(p);
                last = p;
            }
            if (runs.Count == 0) return;

            // Soft vertical gradient under the line — fades to transparent at the bottom (mockup look).
            var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, col.R, col.G, col.B), 0));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x14, col.R, col.G, col.B), 0.7));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, col.R, col.G, col.B), 1));
            var strokeBrush = new SolidColorBrush(col);

            foreach (var run in runs)
            {
                if (run.Count >= 2)
                    canvas.Children.Add(new Path { Data = SmoothPath(run, true, h), Fill = grad });
                canvas.Children.Add(new Path { Data = SmoothPath(run, false, h), Stroke = strokeBrush, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round });
            }
            if (!double.IsNaN(last.X))
            {
                var d = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(col) };
                Canvas.SetLeft(d, last.X - 3); Canvas.SetTop(d, last.Y - 3);
                canvas.Children.Add(d);
            }
        }
        Draw(_fCpuTemp, ChartColorOf("ChartCpuBrush", Color.FromRgb(0x5D, 0xAD, 0xE2)));
        Draw(_fGpuTemp, ChartColorOf("ChartGpuBrush", Color.FromRgb(0xFF, 0x88, 0x32)));

        var lbl = Application.Current.TryFindResource("SubtleTextBrush") as System.Windows.Media.Brush
                  ?? Frozen(Color.FromArgb(140, 200, 210, 220));
        void Label(string t, double top)
        {
            var tb = new TextBlock { Text = t, FontSize = 8, Foreground = lbl, IsHitTestVisible = false };
            Canvas.SetLeft(tb, 2); Canvas.SetTop(tb, top);
            canvas.Children.Add(tb);
        }
        Label($"{dMax:0}°", 0);
        Label($"{dMin:0}°", h - 11);
    }

    // ── Section layout initializer ─────────────────────────────────────────────

    private void OnFloatModeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<FloatMode>(tag, out var mode)) return;
        ApplyViewMode(mode);
        if (App.Settings is not null)
        {
            App.Settings.FloatViewMode = mode.ToString();
            SettingsManager.Save(App.Settings);
        }
    }

    /// <summary>
    /// Applies section visibility / row heights for the selected HUD tab.
    /// Net = graph + ping + apps · Temp = thermals only · Both = everything (resizable).
    /// </summary>
    private void ApplyViewMode(FloatMode mode)
    {
        bool showGraph = mode != FloatMode.Temp;
        bool showApps  = mode != FloatMode.Temp;
        bool showHw    = mode != FloatMode.Net;

        GraphSection.Visibility = showGraph ? Visibility.Visible : Visibility.Collapsed;
        GraphRow.Height    = showGraph ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        GraphRow.MinHeight = showGraph ? 130 : 0;

        bool s1 = showGraph && showApps;
        Splitter1.Visibility = s1 ? Visibility.Visible : Visibility.Collapsed;
        S1Row.Height = s1 ? new GridLength(8) : new GridLength(0);

        AppsSection.Visibility = showApps ? Visibility.Visible : Visibility.Collapsed;
        AppsRow.Height    = showApps ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        AppsRow.MinHeight = showApps ? 60 : 0;

        bool s2 = showApps && showHw;
        Splitter2.Visibility = s2 ? Visibility.Visible : Visibility.Collapsed;
        S2Row.Height = s2 ? new GridLength(8) : new GridLength(0);

        HwSection.Visibility = showHw ? Visibility.Visible : Visibility.Collapsed;
        // Both: gauges + RAM are fixed-height, so size the row to its content (Auto) — a star
        // height would leave dead space below RAM, pushing the bottom toggle bar too far down.
        // Temp: the temp graph is a star row that should grow with the window, so HwRow = star.
        if (!showHw)                     { HwRow.Height = new GridLength(0);                       HwRow.MinHeight = 0;  }
        else if (mode == FloatMode.Both) { HwRow.Height = GridLength.Auto;                         HwRow.MinHeight = 0;  }
        else                             { HwRow.Height = new GridLength(2, GridUnitType.Star);    HwRow.MinHeight = 60; }

        // The big CPU/GPU temp graph belongs to the Temp tab only. In Both we keep the
        // old compact layout (graph + apps + CPU/GPU donuts + RAM), no temp graph.
        bool tempGraph = mode == FloatMode.Temp;
        FloatTempGraphBox.Visibility = tempGraph ? Visibility.Visible : Visibility.Collapsed;
        FloatTempGraphRow.Height = tempGraph ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        if (showHw &&
            HardwareMonitor.Instance is { Latest: var snap } &&
            snap != HardwareSnapshot.Empty)
        {
            UpdateHardwareDisplay(snap);
        }
        if (showGraph)
            Dispatcher.BeginInvoke(DrawChart, System.Windows.Threading.DispatcherPriority.Render);
    }

    // ── Edge snapping ──────────────────────────────────────────────────────────

    private void OnLocationChanged(object? sender, EventArgs e)
    {
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

        UpdateFloatTabMode(w);

        // No caption drag region — the top would otherwise swallow chart/crosshair input.
        // Dragging is handled explicitly by the rates bar and the bottom switch bar.
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome != null) chrome.CaptionHeight = 0.0;

        Dispatcher.BeginInvoke(DrawChart, System.Windows.Threading.DispatcherPriority.Render);
    }

    // Width-driven density only — section layout is owned by ApplyViewMode (the tabs).
    private void ApplyLayoutMode(LayoutMode mode)
    {
        bool spark = mode == LayoutMode.Spark;
        UpRatePanel.Visibility      = spark ? Visibility.Collapsed : Visibility.Visible;
        PingTargetLabel.Visibility  = spark ? Visibility.Collapsed : Visibility.Visible;
        PingJitterText.Visibility   = spark ? Visibility.Collapsed : Visibility.Visible;
    }

    // Mode tabs show text when there's room, icons when the window gets narrow.
    private void UpdateFloatTabMode(double width)
    {
        bool icons = width < TabIconWidth;
        if (icons == _floatTabsIcon) return;
        _floatTabsIcon = icons;
        SetTab(TabNet,  icons, "", "Net");    // Globe
        SetTab(TabTemp, icons, "", "Temp");   // Processor/chip
        SetTab(TabBoth, icons, "", "Both");   // ViewAll
    }

    private static void SetTab(System.Windows.Controls.RadioButton rb, bool icon, string glyph, string text)
    {
        if (icon)
        {
            rb.FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            rb.FontSize   = 13;
            rb.FontWeight = FontWeights.Normal;   // avoid WPF synth-bolding the glyph (looks thick)
            rb.Content    = glyph;
            rb.ToolTip    = text;
        }
        else
        {
            rb.ClearValue(System.Windows.Controls.Control.FontFamilyProperty);
            rb.ClearValue(System.Windows.Controls.Control.FontSizeProperty);
            rb.ClearValue(System.Windows.Controls.Control.FontWeightProperty);
            rb.Content = text;
            rb.ToolTip = null;
        }
    }

    // ── Geometry persistence ───────────────────────────────────────────────────

    /// <summary>
    /// After restoring a saved position, verify that at least the top-left drag area
    /// (80×30 DIPs) falls somewhere on the current virtual desktop.
    /// If the saved coords come from a different PC (fewer monitors, different DPI,
    /// different resolution) the window can end up completely off-screen.
    /// </summary>
    private void EnsureOnScreen()
    {
        // SystemParameters.VirtualScreen* gives the bounding rect of all monitors
        // combined, expressed in WPF device-independent pixels — same coordinate
        // space as Left / Top, so no DPI conversion is needed.
        double vLeft   = SystemParameters.VirtualScreenLeft;
        double vTop    = SystemParameters.VirtualScreenTop;
        double vRight  = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop  + SystemParameters.VirtualScreenHeight;

        // The check requires an 80×30 DIP "grab strip" at the top-left to be on-screen
        // so the user can always reach the title bar to drag the window back.
        bool onScreen = Left + 80 > vLeft && Left  < vRight &&
                        Top  + 30 > vTop  && Top   < vBottom;

        if (!onScreen)
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width  - 16;
            Top  = wa.Top   + 16;
        }
    }

    private void PersistGeometry()
    {
        if (App.Settings is null) return;

        // WPF returns NaN for Left / Top / Width / Height after the HWND has been
        // released — typically when Closed fires.  If we blindly write NaN the next
        // restore reads an invalid value and falls back to the default position.
        // Skip the assignment when NaN so the last valid value (written by the most
        // recent LocationChanged / SizeChanged call) is preserved in App.Settings.
        if (!double.IsNaN(Left))   App.Settings.FloatGraphX = Left;
        if (!double.IsNaN(Top))    App.Settings.FloatGraphY = Top;
        if (!double.IsNaN(Width))  App.Settings.FloatGraphW = Width;
        if (!double.IsNaN(Height)) App.Settings.FloatGraphH = Height;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        PersistGeometry();
        // Persist the geometry explicitly here — do not rely on the
        // Vm.ShowFloatingGraph = false side-effect as the only save path.
        SettingsManager.Save(App.Settings);
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
    }

    private void OnAlwaysOnTopMenuClick(object sender, RoutedEventArgs e)
    {
        bool pinned = AlwaysOnTopMenu.IsChecked;
        Topmost = pinned;
        if (App.Settings is not null)
            App.Settings.FloatGraphTopmost = pinned;
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
