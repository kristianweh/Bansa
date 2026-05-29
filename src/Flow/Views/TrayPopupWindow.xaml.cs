using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Flow.Services;
using Flow.ViewModels;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;

namespace Flow.Views;

public partial class TrayPopupWindow : Window
{
    private readonly DispatcherTimer _hideTimer;
    private readonly HistoryStore _history = new();
    private DateTime _lastDayQuery = DateTime.MinValue;
    private long _dayDown, _dayUp;
    private bool _isFading;
    private bool _clickThrough;
    private bool _clickThroughRestored;   // ensures saved setting is applied on first show

    // Stable list updated in-place — same hold-time pattern as FloatingGraphWindow
    private readonly ObservableCollection<AppRowViewModel> _topApps = new();
    private readonly Dictionary<string, (AppRowViewModel Vm, DateTime LastActive)> _appHold =
        new(StringComparer.OrdinalIgnoreCase);
    private const double HoldSeconds = 8.0;   // keep apps visible 8 s after last active tick
    private int _rankTick;
    private const int RankInterval = 10;   // reorder only every 5 s to prevent positional jumping

    public TrayPopupWindow()
    {
        InitializeComponent();

        // 500ms grace before fading starts — long enough that normal hover pauses
        // won't dismiss the popup, short enough that leaving feels responsive.
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            DoFadeOut();
        };

        // MouseEnter cancels any pending fade; MouseLeave is handled by the
        // cursor-poll in TrayIconManager so we don't double-dismiss here.
        MouseEnter += (_, _) => CancelHide();
        // Don't wire MouseLeave→BeginFadeOut — that fought with the cursor poll
        // and caused the popup to flicker when the mouse moved within the window.
        Deactivated += (_, _) => BeginFadeOut();

        // Bind stable collection once — never replace ItemsSource
        TopAppsList.ItemsSource = _topApps;

        // Subscribe to hardware monitor — fires every ~2 s on a background thread.
        // Dispatcher.InvokeAsync routes the UI update safely onto the UI thread.
        if (HardwareMonitor.Instance is { } hw)
        {
            hw.Sampled += snap =>
                Dispatcher.InvokeAsync(() => UpdateTrayHardware(snap));
        }
    }

    public void ShowAt(double screenX, double screenY)
    {
        var workingArea = SystemParameters.WorkArea;
        var left = screenX - Width;
        var top  = screenY - Height - 8;
        if (left < workingArea.Left) left = workingArea.Left + 4;
        if (top  < workingArea.Top)  top  = workingArea.Top + 4;
        if (left + Width > workingArea.Right)   left = workingArea.Right - Width - 4;
        if (top + Height > workingArea.Bottom)  top  = workingArea.Bottom - Height - 4;

        Left = left;
        Top  = top;

        _isFading = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        if (!IsVisible) Show();

        // Restore saved click-through preference on the first ever show.
        // The HWND is guaranteed to exist once Show() has been called.
        if (!_clickThroughRestored)
        {
            _clickThroughRestored = true;
            var saved = App.Settings?.TrayClickThrough ?? false;
            if (saved != _clickThrough)
            {
                TrayClickThroughBtn.IsChecked = saved;
                SetClickThrough(saved);
            }
        }

        // Refresh hardware bars now that layout is measured (ActualWidth is valid)
        if (HardwareMonitor.Instance is { Latest: var snap } && snap != HardwareSnapshot.Empty)
            UpdateTrayHardware(snap);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140));
        BeginAnimation(OpacityProperty, fadeIn);
    }

    public void CancelHide()
    {
        _hideTimer.Stop();
        if (_isFading)
        {
            _isFading = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        }
    }

    public void BeginFadeOut()
    {
        if (_isFading) return;
        _hideTimer.Start();
    }

    private void DoFadeOut()
    {
        if (!IsVisible) return;
        _isFading = true;
        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180));
        fadeOut.Completed += (_, _) =>
        {
            if (_isFading)
            {
                Hide();
                _isFading = false;
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    public void Update(long totalDown, long totalUp, int pingMs,
                       IReadOnlyList<(long Down, long Up)> history,
                       IEnumerable<AppRowViewModel> apps)
    {
        SplitSpeedText(Format.Rate(totalDown), TotalDown, TotalDownUnit);
        SplitSpeedText(Format.Rate(totalUp),   TotalUp,   TotalUpUnit);
        SplitSpeedText(pingMs < 0 ? "— ms" : $"{pingMs} ms", PingText, PingUnit);

        // Ping colour — applied to both value and unit TextBlocks
        Brush pingBrush = pingMs < 0
            ? (Application.Current.TryFindResource("AccentBrush") as Brush
               ?? new SolidColorBrush(Color.FromArgb(160, 150, 150, 180)))
            : pingMs < 40 ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))
            : pingMs < 80 ? new SolidColorBrush(Color.FromRgb(0xFB, 0xD2, 0x24))
                          : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        PingText.Foreground = pingBrush;
        PingUnit.Foreground = pingBrush;

        DrawChart(history);
        UpdateDailyTotals();

        var now = DateTime.UtcNow;
        _rankTick++;
        foreach (var a in apps)
        {
            // Only hold apps with at least 5 KB/s — keeps the list clean and stable
            if (a.BytesInPerSec + a.BytesOutPerSec >= 5120)
                _appHold[a.Name] = (a, now);
            // Below threshold: don't update timestamp, let hold expire naturally
        }

        var stale = _appHold
            .Where(kv => (now - kv.Value.LastActive).TotalSeconds > HoldSeconds)
            .Select(kv => kv.Key).ToList();
        foreach (var k in stale) _appHold.Remove(k);

        var ranked = _appHold.Values
            .Select(v => v.Vm)
            .OrderByDescending(a => a.BytesInPerSec + a.BytesOutPerSec)
            .Take(6)
            .ToList();

        for (int i = _topApps.Count - 1; i >= 0; i--)
            if (!ranked.Contains(_topApps[i])) _topApps.RemoveAt(i);

        if (_rankTick % RankInterval == 0)
        {
            for (int i = 0; i < ranked.Count; i++)
            {
                var vm = ranked[i];
                int cur = _topApps.IndexOf(vm);
                if (cur < 0)  _topApps.Insert(Math.Min(i, _topApps.Count), vm);
                else if (cur != i) _topApps.Move(cur, i);
            }
        }
        else
        {
            foreach (var vm in ranked)
                if (!_topApps.Contains(vm)) _topApps.Add(vm);
        }
    }

    private void UpdateDailyTotals()
    {
        // Only re-query the DB every ~10s to avoid hammering it on every popup update
        if ((DateTime.UtcNow - _lastDayQuery).TotalSeconds < 10)
        {
            SplitSpeedText(Format.Bytes(_dayDown), DayDown, DayDownUnit);
            SplitSpeedText(Format.Bytes(_dayUp),   DayUp,   DayUpUnit);
            return;
        }
        _lastDayQuery = DateTime.UtcNow;
        try
        {
            var dayStart = DateTime.UtcNow.Date;
            var rows = _history.GetTotals(dayStart, DateTime.UtcNow);
            _dayDown = rows.Sum(r => r.BytesIn);
            _dayUp   = rows.Sum(r => r.BytesOut);
        }
        catch { _dayDown = _dayUp = 0; }

        SplitSpeedText(Format.Bytes(_dayDown), DayDown, DayDownUnit);
        SplitSpeedText(Format.Bytes(_dayUp),   DayUp,   DayUpUnit);
    }

    /// <summary>Splits "12.3 MB/s" into the value TextBlock ("12.3") and unit TextBlock ("MB/s").</summary>
    private static void SplitSpeedText(string text, System.Windows.Controls.TextBlock value, System.Windows.Controls.TextBlock unit)
    {
        var idx = text.LastIndexOf(' ');
        value.Text = idx >= 0 ? text[..idx]       : text;
        unit.Text  = idx >= 0 ? text[(idx + 1)..] : "";
    }

    private void DrawChart(IReadOnlyList<(long Down, long Up)> history)
    {
        ChartCanvas.Children.Clear();
        if (history.Count < 2) { ChartPeakLabel.Text = ""; return; }

        double w = ChartCanvas.ActualWidth;
        double h = ChartCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        long peak = 1;
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i].Down > peak) peak = history[i].Down;
            if (history[i].Up   > peak) peak = history[i].Up;
        }

        ChartPeakLabel.Text = Format.Rate(peak);

        Color downColor  = ParseColor(App.Settings?.DownColorHex, Color.FromRgb(0x5D, 0xAD, 0xE2));
        Color upColor    = ParseColor(App.Settings?.UpColorHex,   Color.FromRgb(0xF3, 0x9C, 0x12));
        Brush downStroke = new SolidColorBrush(downColor);
        Brush upStroke   = new SolidColorBrush(upColor);
        Brush downFill   = new SolidColorBrush(Color.FromArgb(80, downColor.R, downColor.G, downColor.B));
        Brush upFill     = new SolidColorBrush(Color.FromArgb(48, upColor.R,   upColor.G,   upColor.B));

        var gridBrush  = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255));
        var labelBrush = new SolidColorBrush(Color.FromArgb(100, 200, 210, 220));

        // ── Horizontal grid lines + Y-axis labels ────────────────────────────────
        for (int li = 1; li <= 3; li++)
        {
            double y = h * li / 4.0;
            ChartCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            });
            long labelVal = (long)(peak * (4 - li) / 4.0);
            var lbl = new TextBlock
            {
                Text = Format.Rate(labelVal), FontSize = 9,
                Foreground = labelBrush, IsHitTestVisible = false
            };
            Canvas.SetLeft(lbl, 2); Canvas.SetTop(lbl, y - 11);
            ChartCanvas.Children.Add(lbl);
        }

        // ── Time markers (every ~10 s = every 20 samples at 500 ms) ─────────────
        int n = history.Count;
        var tickBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        for (int sAgo = 10; sAgo < 30; sAgo += 10)
        {
            int sIdx = n - 1 - sAgo * 2;
            if (sIdx <= 0) continue;
            double tx = sIdx * (w / (n - 1));
            ChartCanvas.Children.Add(new Line
            {
                X1 = tx, Y1 = 0, X2 = tx, Y2 = h,
                Stroke = tickBrush, StrokeThickness = 1
            });
            var tLbl = new TextBlock
            {
                Text = $"-{sAgo}s", FontSize = 9,
                Foreground = labelBrush, IsHitTestVisible = false
            };
            Canvas.SetLeft(tLbl, tx + 2); Canvas.SetTop(tLbl, h - 15);
            ChartCanvas.Children.Add(tLbl);
        }

        // ── Sparklines (smooth Catmull-Rom curves) ────────────────────────────────
        var downPts = new List<Point>();
        var upPts   = new List<Point>();
        for (int i = 0; i < n; i++)
        {
            double x = (n == 1) ? 0 : (i * (w / (n - 1)));
            downPts.Add(new Point(x, h - ((double)history[i].Down / peak) * h * 0.90));
            upPts.Add(  new Point(x, h - ((double)history[i].Up   / peak) * h * 0.90));
        }

        ChartCanvas.Children.Add(new Path { Data = SmoothPath(downPts, true,  h), Fill = downFill });
        ChartCanvas.Children.Add(new Path { Data = SmoothPath(upPts,   true,  h), Fill = upFill });
        ChartCanvas.Children.Add(new Path { Data = SmoothPath(downPts, false, h), Stroke = downStroke, StrokeThickness = 1.8 });
        ChartCanvas.Children.Add(new Path { Data = SmoothPath(upPts,   false, h), Stroke = upStroke,   StrokeThickness = 1.8 });

        // ── Live-rate dots ────────────────────────────────────────────────────────
        var dDot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = downStroke };
        Canvas.SetLeft(dDot, downPts[^1].X - 3.5); Canvas.SetTop(dDot, downPts[^1].Y - 3.5);
        ChartCanvas.Children.Add(dDot);

        var uDot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = upStroke };
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

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }

    // ── Hardware strip update ─────────────────────────────────────────────────

    private void UpdateTrayHardware(HardwareSnapshot snap)
    {
        // CPU
        TrayHwCpuPct.Text = $"{snap.CpuLoad:0}%";
        TrayHwCpuBarFill.Width = TrayHwCpuBarBg.ActualWidth * snap.CpuLoad / 100.0;
        var cpuDetail = new System.Text.StringBuilder();
        if (snap.CpuTemp > 0) cpuDetail.Append($"{snap.CpuTemp:0}°C");
        if (snap.CpuFreqMHz > 0)
        {
            if (cpuDetail.Length > 0) cpuDetail.Append("  ");
            cpuDetail.Append($"{snap.CpuFreqMHz / 1000f:0.0} GHz");
        }
        TrayHwCpuDetail.Text = cpuDetail.ToString();

        // GPU
        TrayHwGpuPct.Text = $"{snap.GpuLoad:0}%";
        TrayHwGpuBarFill.Width = TrayHwGpuBarBg.ActualWidth * snap.GpuLoad / 100.0;
        var gpuDetail = new System.Text.StringBuilder();
        if (snap.GpuTemp > 0) gpuDetail.Append($"{snap.GpuTemp:0}°C");
        if (snap.GpuVramUsedMb > 0 && snap.GpuVramTotalMb > 0)
        {
            if (gpuDetail.Length > 0) gpuDetail.Append("  ");
            gpuDetail.Append($"{snap.GpuVramUsedMb / 1024f:0.1}/{snap.GpuVramTotalMb / 1024f:0.0}G");
        }
        TrayHwGpuDetail.Text = gpuDetail.ToString();

        // RAM
        if (snap.RamTotalGb > 0)
        {
            TrayHwRamPct.Text = $"{snap.RamPct:0}%";
            TrayHwRamBarFill.Width = TrayHwRamBarBg.ActualWidth * snap.RamPct / 100.0;
            TrayHwRamDetail.Text   = $"{snap.RamUsedGb:0.1}/{snap.RamTotalGb:0.0} GB";
        }
        else
        {
            TrayHwRamPct.Text      = "—";
            TrayHwRamBarFill.Width = 0;
            TrayHwRamDetail.Text   = "";
        }
    }

    // ── Window toggle handlers ────────────────────────────────────────────────

    private void OnTrayPinClick(object sender, RoutedEventArgs e)
    {
        Topmost = TrayPinBtn.IsChecked == true;
    }

    private void OnTrayClickThroughClick(object sender, RoutedEventArgs e)
    {
        // The hwnd is guaranteed to exist once the button is clickable (window is visible).
        SetClickThrough(TrayClickThroughBtn.IsChecked == true);
    }

    /// <summary>
    /// Toggles WS_EX_TRANSPARENT so mouse events pass through the popup to whatever
    /// is underneath. The window already carries WS_EX_LAYERED (AllowsTransparency=True),
    /// which is required for WS_EX_TRANSPARENT to work correctly.
    /// </summary>
    private void SetClickThrough(bool enable)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE       = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;
        int style = NativeMethods.GetWindowLong(hwnd, GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, GWL_EXSTYLE,
            enable ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT);
        _clickThrough = enable;
        // Persist so the preference is restored next time the popup opens
        if (App.Settings is not null)
        {
            App.Settings.TrayClickThrough = enable;
            SettingsManager.Save(App.Settings);
        }
    }

    // ── Win32 / source initialisation ────────────────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE    = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        int style = NativeMethods.GetWindowLong(hwnd, GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
