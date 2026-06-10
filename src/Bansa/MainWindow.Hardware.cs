using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using Bansa.Services;
using Bansa.ViewModels;
using Bansa.Views;

// Pin ambiguous types to their WPF variants (System.Drawing brings duplicates via UseWindowsForms).
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Bansa;

public partial class MainWindow
{
    // ────────── Hardware monitor panel ──────────

    private void InitHardwarePanel()
    {
        HwHintBanner.Visibility = App.Settings.HardwareHintDismissed
            ? Visibility.Collapsed : Visibility.Visible;

        if (HardwareMonitor.Instance is not { } hw) return;

        // Feed the latest reading immediately (sensor might already have data)
        if (hw.Latest != HardwareSnapshot.Empty)
            UpdateHardwarePanel(hw.Latest);

        // Subscribe for live updates
        hw.Sampled += snap => Dispatcher.InvokeAsync(() => UpdateHardwarePanel(snap));

        // Updates are skipped while hidden — repaint from the latest sample as soon as
        // the window comes back (restore from tray) so gauges are never visibly stale.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && HardwareMonitor.Instance is { } m && m.Latest != HardwareSnapshot.Empty)
                UpdateHardwarePanel(m.Latest, pushBuffers: false);
        };
    }

    private void OnHwHintGotIt(object sender, RoutedEventArgs e)
    {
        HwHintBanner.Visibility = Visibility.Collapsed;
        App.Settings.HardwareHintDismissed = true;
        SettingsManager.Save(App.Settings);
    }

    private void OnHwHintOpenHistory(object sender, RoutedEventArgs e)
    {
        OnHwHintGotIt(sender, e);
        NavigateToPanel(4);   // Hardware domain → shows the hardware history (session recorder)
    }

    // ── Color heat helpers ────────────────────────────────────────────────────
    private static Color LerpColor(Color a, Color b, double t)
        => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    private static Color ParseHex(string? hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }
    // ≤40 ms → PingGoodColor, 40–120 ms → lerp, 120 ms+ → PingBadColor
    private static Color PingHeatColor(int ms)
    {
        var good = ParseHex(App.Settings?.PingGoodColorHex, Color.FromRgb(0x10, 0xB9, 0x81));
        var bad  = ParseHex(App.Settings?.PingBadColorHex,  Color.FromRgb(0xF4, 0x3F, 0x5E));
        return LerpColor(good, bad, Math.Clamp((ms - 40.0) / 80.0, 0, 1));
    }
    // Cool (user-set "blue") → bright yellow → bright red. See Services/HeatColors.
    private static Color TempHeatColor(double tempC) => HeatColors.Temp(tempC);

    private void UpdatePingColor(int pingMs)
    {
        Brush brush;
        if (pingMs < 0)
        {
            brush = (Brush?)TryFindResource("MutedTextBrush")
                    ?? new SolidColorBrush(Color.FromArgb(96, 128, 128, 128));
        }
        else
        {
            brush = new SolidColorBrush(PingHeatColor(pingMs));
        }
        SidebarPingText.Foreground = brush;
        SidebarPingUnit.Foreground = brush;
        DashPingText.Foreground    = brush;
        DashPingUnit.Foreground    = brush;

        // Record sample; only redraw the sparkline when the Dashboard card is on screen.
        RecordPingSample(pingMs);
        if (pingMs >= 0 && DashboardPanel.Visibility == Visibility.Visible)
            DrawDashPingSparkline(brush);
    }

    /// <summary>Pushes a ping sample into the sparkline ring buffer — kept separate from
    /// drawing so the history stays continuous while the window is hidden.</summary>
    private void RecordPingSample(int pingMs)
    {
        if (pingMs < 0) return;
        _pingBuf[_pingBufHead] = pingMs;
        _pingBufHead = (_pingBufHead + 1) % PingHistLen;
        if (_pingBufCount < PingHistLen) _pingBufCount++;
    }

    private void DrawDashPingSparkline(Brush stroke)
    {
        if (_pingBufCount < 2 || DashPingSparkCanvas == null) return;

        double w = DashPingSparkCanvas.ActualWidth;
        double h = DashPingSparkCanvas.ActualHeight;
        if (w <= 0 || h <= 0) { w = 120; h = 20; }

        int start = _pingBufCount < PingHistLen ? 0 : _pingBufHead;
        int peak  = 1;
        for (int i = 0; i < _pingBufCount; i++)
            peak = Math.Max(peak, _pingBuf[(start + i) % PingHistLen]);

        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            for (int i = 0; i < _pingBufCount; i++)
            {
                double x = i * (w / (_pingBufCount - 1));
                double y = h - (_pingBuf[(start + i) % PingHistLen] / (double)peak) * h;
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else        ctx.LineTo(new Point(x, y), true, false);
            }
        }
        sg.Freeze();
        DashPingSparkPath.Data   = sg;
        DashPingSparkPath.Stroke = stroke;
    }

    /// <param name="snap">Latest sensor snapshot.</param>
    /// <param name="pushBuffers">False for forced repaints (navigation, window restore) so
    /// the same 2 s sample isn't pushed into the history ring buffers twice.</param>
    private void UpdateHardwarePanel(HardwareSnapshot snap, bool pushBuffers = true)
    {
        // ── Ring buffers ALWAYS advance on real samples ───────────────────────
        // Temperature/usage history must stay continuous even while the window is
        // hidden or another panel is active, so this runs before any visibility gate.
        if (pushBuffers)
        {
            _cpuTempBuf[_tempBufHead] = snap.CpuTemp  > 0 ? (float)snap.CpuTemp : 0f;
            _gpuTempBuf[_tempBufHead] = snap.GpuTemp  > 0 ? (float)snap.GpuTemp : 0f;
            _ramPctBuf[_tempBufHead]  = snap.RamTotalGb > 0 ? (float)snap.RamPct : 0f;
            _tempBufHead = (_tempBufHead + 1) % TempHistLen;
            if (_tempBufCount < TempHistLen) _tempBufCount++;
        }

        // Window hidden (tray) → nothing on screen to paint.
        if (!IsVisible) return;

        // The sidebar STATUS gauges are visible on every panel.
        RedrawSidebarThermals(snap);

        // Everything below only exists on the Hardware panel.
        if (HardwareMonitorPanel.Visibility != Visibility.Visible) return;

        bool hasAnyData = snap.CpuLoad > 0 || snap.GpuLoad > 0 || snap.RamTotalGb > 0;
        HwNoDataNotice.Visibility = hasAnyData ? Visibility.Collapsed : Visibility.Visible;

        // ── CPU ──────────────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(snap.CpuName))
            CpuCardName.Text = snap.CpuName;
        CpuUsageBar.Value = snap.CpuLoad;
        CpuPct.Text       = snap.CpuTemp > 0 ? $"{snap.CpuTemp:0}" : "—";
        CpuLoadLabel.Text = $"{snap.CpuLoad:0}";
        if (snap.CpuTemp > 0)
        {
            var cpuB = new SolidColorBrush(TempHeatColor(snap.CpuTemp));
            CpuPct.Foreground      = cpuB;
            CpuTempSign.Foreground = cpuB;
        }
        // Show average frequency; if boost differs, append boost in parens
        if (snap.CpuFreqMHz > 0)
        {
            string avgStr = $"{snap.CpuFreqMHz / 1000f:0.00} GHz";
            if (snap.CpuBoostMHz > 0 && snap.CpuBoostMHz > snap.CpuFreqMHz + 100)
                avgStr += $" (↑{snap.CpuBoostMHz / 1000f:0.0})";
            CpuFreqLabel.Text = avgStr;
        }
        else
        {
            CpuFreqLabel.Text = "—";
        }

        // ── GPU ──────────────────────────────────────────────────────────────
        GpuUsageBar.Value = snap.GpuLoad;
        GpuPct.Text       = snap.GpuTemp > 0 ? $"{snap.GpuTemp:0}" : "—";
        GpuLoadLabel.Text = $"{snap.GpuLoad:0}";
        if (snap.GpuTemp > 0)
        {
            var gpuB = new SolidColorBrush(TempHeatColor(snap.GpuTemp));
            GpuPct.Foreground      = gpuB;
            GpuTempSign.Foreground = gpuB;
        }
        if (!string.IsNullOrEmpty(snap.GpuName))
            GpuCardName.Text = snap.GpuName;
        SyncGpuPicker(snap.GpuName);

        // VRAM sub-row
        if (snap.GpuVramTotalMb > 0)
        {
            GpuVramSection.Visibility = Visibility.Visible;
            GpuVramBar.Maximum        = snap.GpuVramTotalMb;
            GpuVramBar.Value          = snap.GpuVramUsedMb;
            GpuVramText.Text          = $"{snap.GpuVramUsedMb / 1024f:0.0} / {snap.GpuVramTotalMb / 1024f:0.0} GB";
        }
        else
        {
            GpuVramSection.Visibility = Visibility.Collapsed;
        }

        // Clocks + Fan row
        bool hasFreqOrFan = snap.GpuCoreMHz > 0 || snap.GpuFanRpm > 0 || snap.GpuPowerW > 0;
        GpuFreqFanRow.Visibility = hasFreqOrFan ? Visibility.Visible : Visibility.Collapsed;
        if (snap.GpuCoreMHz > 0)
        {
            GpuCoreMHzText.Text = $"Core  {snap.GpuCoreMHz:0} MHz";
            GpuMemMHzText.Text  = snap.GpuMemMHz > 0 ? $"Mem  {snap.GpuMemMHz:0} MHz" : "";
        }
        if (snap.GpuFanRpm > 0)
        {
            GpuFanRpmText.Text = snap.GpuPowerW > 0
                ? $"{snap.GpuFanRpm:0} RPM  ·  {snap.GpuPowerW:0} W"
                : $"{snap.GpuFanRpm:0} RPM";
            GpuFanPctText.Text = snap.GpuFanPct > 0 ? $"{snap.GpuFanPct:0}%" : "";
        }
        else if (snap.GpuPowerW > 0)
        {
            GpuFanRpmText.Text = $"{snap.GpuPowerW:0} W";
            GpuFanPctText.Text = "";
        }
        else
        {
            GpuFanRpmText.Text = snap.GpuCoreMHz > 0 ? "—" : "";
            GpuFanPctText.Text = "";
        }

        // ── RAM ──────────────────────────────────────────────────────────────
        if (snap.RamTotalGb > 0)
        {
            RamUsageBar.Maximum = snap.RamTotalGb;
            RamUsageBar.Value   = snap.RamUsedGb;
            RamUsedText.Text    = $"{snap.RamUsedGb:0.0}";
            string speedSuffix  = snap.RamSpeedMHz > 0 ? $"  ·  {snap.RamSpeedMHz:0} MHz" : "";
            RamTotalText.Text   = $"of {snap.RamTotalGb:0.0} GB total{speedSuffix}";
            RamPct.Text         = $"{snap.RamPct:0}";
            float freeGb        = snap.RamTotalGb - snap.RamUsedGb;
            RamFreeText.Text    = $"{freeGb:0.0}";
            RamSpeedLabel.Text  = snap.RamSpeedMHz > 0 ? $"{snap.RamSpeedMHz:0}" : "—";
            // Update subtitle: "System Memory" → "System Memory  ·  3200 MHz" if speed available
            if (snap.RamSpeedMHz > 0)
                RamCardSubtitle.Text = $"System Memory  ·  {snap.RamSpeedMHz:0} MHz";
        }

        // ── System Info (static — populated once) ────────────────────────────
        if (!string.IsNullOrEmpty(snap.MotherboardName))
            MoboNameText.Text = snap.MotherboardName;
        if (!string.IsNullOrEmpty(snap.BiosVersion))
            BiosVersionText.Text = snap.BiosVersion;

        // ── Min / Avg / Max temperature labels ───────────────────────────────
        if (_tempBufCount > 0)
        {
            float cpuMin = float.MaxValue, cpuMax = 0, cpuSum = 0; int cpuN = 0;
            float gpuMin = float.MaxValue, gpuMax = 0, gpuSum = 0; int gpuN = 0;
            for (int i = 0; i < _tempBufCount; i++)
            {
                int idx = (_tempBufHead - 1 - i + TempHistLen) % TempHistLen;
                float ct = _cpuTempBuf[idx];
                if (ct > 0) { if (ct < cpuMin) cpuMin = ct; if (ct > cpuMax) cpuMax = ct; cpuSum += ct; cpuN++; }
                float gt = _gpuTempBuf[idx];
                if (gt > 0) { if (gt < gpuMin) gpuMin = gt; if (gt > gpuMax) gpuMax = gt; gpuSum += gt; gpuN++; }
            }
            CpuStatMax.Text = cpuN > 0 ? $"max {cpuMax:0}°" : "max —°";
            CpuStatAvg.Text = cpuN > 0 ? $"avg {cpuSum / cpuN:0}°" : "avg —°";
            CpuStatMin.Text = cpuN > 0 ? $"min {cpuMin:0}°" : "min —°";
            GpuStatMax.Text = gpuN > 0 ? $"max {gpuMax:0}°" : "max —°";
            GpuStatAvg.Text = gpuN > 0 ? $"avg {gpuSum / gpuN:0}°" : "avg —°";
            GpuStatMin.Text = gpuN > 0 ? $"min {gpuMin:0}°" : "min —°";
        }

        DrawTempChart(CpuTempChart,  _cpuTempBuf, _tempBufHead, _tempBufCount,
                      (Application.Current.TryFindResource("ChartCpuBrush") as SolidColorBrush)?.Color
                      ?? Color.FromRgb(0x5D, 0xAD, 0xE2), "°");
        DrawTempChart(GpuTempChart,  _gpuTempBuf, _tempBufHead, _tempBufCount,
                      (Application.Current.TryFindResource("ChartGpuBrush") as SolidColorBrush)?.Color
                      ?? Color.FromRgb(0xFF, 0x88, 0x32), "°");
        DrawTempChart(RamUsageChart, _ramPctBuf,  _tempBufHead, _tempBufCount,
                      (Application.Current.TryFindResource("ChartRamBrush") as SolidColorBrush)?.Color
                      ?? Color.FromRgb(0x10, 0xB9, 0x81), "%");

        // Hero: thermal radial gauges + overlaid CPU/GPU temperature timeline
        RedrawHwHero(snap);

        // Pulse the refresh dot so users can see data is live
        HwRefreshDot.Opacity = 1.0;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.35,
            new Duration(TimeSpan.FromMilliseconds(600)));
        HwRefreshDot.BeginAnimation(OpacityProperty, fade);
    }

    // ── Multi-GPU picker ──────────────────────────────────────────────────────

    private string[] _gpuPickerNames = Array.Empty<string>();
    private bool     _gpuPickerSyncing;

    /// <summary>
    /// Shows a GPU selector in the GPU card header when 2+ GPUs are detected
    /// (laptop iGPU+dGPU etc.). Re-populated only when the detected set changes.
    /// </summary>
    private void SyncGpuPicker(string activeGpuName)
    {
        var names = HardwareMonitor.Instance?.GpuNames;
        if (names is null || names.Count < 2)
        {
            GpuPickerCombo.Visibility = Visibility.Collapsed;
            GpuCardName.Visibility    = Visibility.Visible;
            return;
        }

        if (!names.SequenceEqual(_gpuPickerNames))
        {
            _gpuPickerNames   = names.ToArray();
            _gpuPickerSyncing = true;
            GpuPickerCombo.ItemsSource = _gpuPickerNames;
            _gpuPickerSyncing = false;
        }

        GpuPickerCombo.Visibility = Visibility.Visible;
        GpuCardName.Visibility    = Visibility.Collapsed;

        // Keep selection in sync with the GPU actually shown (unless a dropdown is open).
        if (!GpuPickerCombo.IsDropDownOpen &&
            !string.Equals(GpuPickerCombo.SelectedItem as string, activeGpuName, StringComparison.OrdinalIgnoreCase))
        {
            _gpuPickerSyncing = true;
            GpuPickerCombo.SelectedItem = _gpuPickerNames.FirstOrDefault(n =>
                string.Equals(n, activeGpuName, StringComparison.OrdinalIgnoreCase));
            _gpuPickerSyncing = false;
        }
    }

    private void OnGpuPickerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_gpuPickerSyncing || GpuPickerCombo.SelectedItem is not string picked) return;
        if (string.Equals(App.Settings.PreferredGpuName, picked, StringComparison.OrdinalIgnoreCase)) return;
        App.Settings.PreferredGpuName = picked;
        SettingsManager.Save(App.Settings);
        Vm.StatusText = $"GPU display switched to {picked} — updates within ~2 s.";
    }

    // ── Temperature sparkline helper ──────────────────────────────────────────

    /// <summary>
    /// Draws a compact sparkline into <paramref name="canvas"/> from a circular ring buffer.
    /// Adds Y-axis min/max labels and a live-value dot at the newest sample.
    /// <paramref name="unit"/> is appended to the Y-axis labels (e.g. "°" or "%").
    /// </summary>
    private static void DrawTempChart(Canvas canvas, float[] buf, int head, int count,
                                      Color lineColor, string unit = "")
    {
        canvas.Children.Clear();
        if (count < 2) return;
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        int len = buf.Length;

        // Find the min/max over the visible window (save before headroom for labels)
        float dataMin = float.MaxValue, dataMax = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            float v = buf[(head - count + i + len) % len];
            if (v < dataMin) dataMin = v;
            if (v > dataMax) dataMax = v;
        }
        if (dataMax <= dataMin) dataMax = dataMin + 1;

        // Add 10 % headroom top and bottom so the line doesn't touch the edges
        float range = dataMax - dataMin;
        float scaledMin = dataMin - range * 0.10f;
        float scaledMax = dataMax + range * 0.10f;

        // Build point list (chronological, left → right)
        var pts = new List<Point>(count);
        for (int i = 0; i < count; i++)
        {
            float v = buf[(head - count + i + len) % len];
            double x = count == 1 ? 0 : i * (w / (count - 1));
            double y = h - ((v - scaledMin) / (scaledMax - scaledMin)) * h;
            pts.Add(new Point(x, Math.Clamp(y, 0, h)));
        }

        // Translucent fill area
        var fillFig = new PathFigure { StartPoint = pts[0], IsFilled = true };
        for (int i = 1; i < pts.Count; i++)
            fillFig.Segments.Add(new LineSegment(pts[i], true));
        fillFig.Segments.Add(new LineSegment(new Point(pts[^1].X, h), false));
        fillFig.Segments.Add(new LineSegment(new Point(pts[0].X,  h), false));
        fillFig.IsClosed = true;
        canvas.Children.Add(new Path
        {
            Data = new PathGeometry(new[] { fillFig }),
            Fill = new SolidColorBrush(Color.FromArgb(45, lineColor.R, lineColor.G, lineColor.B))
        });

        // Solid stroke
        var lineFig = new PathFigure { StartPoint = pts[0] };
        for (int i = 1; i < pts.Count; i++)
            lineFig.Segments.Add(new LineSegment(pts[i], true));
        canvas.Children.Add(new Path
        {
            Data            = new PathGeometry(new[] { lineFig }),
            Stroke          = new SolidColorBrush(lineColor),
            StrokeThickness = 1.5,
            StrokeLineJoin  = PenLineJoin.Round
        });

        // Live-value dot at the rightmost point
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width  = 6, Height = 6,
            Fill   = new SolidColorBrush(lineColor)
        };
        Canvas.SetLeft(dot, pts[^1].X - 3);
        Canvas.SetTop(dot,  pts[^1].Y - 3);
        canvas.Children.Add(dot);

        // ── Y-axis labels (max at top-left, min at bottom-left) ─────────────
        string maxLabel = $"{dataMax:0}{unit}";
        string minLabel = $"{dataMin:0}{unit}";

        var maxTb = new TextBlock
        {
            Text = maxLabel, FontSize = 8,
            Foreground = ChartChrome("SubtleTextBrush", Color.FromArgb(110, 200, 210, 220)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(maxTb, 2); Canvas.SetTop(maxTb, 1);
        canvas.Children.Add(maxTb);

        var minTb = new TextBlock
        {
            Text = minLabel, FontSize = 8,
            Foreground = ChartChrome("SubtleTextBrush", Color.FromArgb(110, 200, 210, 220)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(minTb, 2); Canvas.SetTop(minTb, h - 11);
        canvas.Children.Add(minTb);
    }

    // ── Sidebar status: compact CPU/GPU temp gauges ───────────────────────────

    private void OnSidebarGaugeSizeChanged(object sender, SizeChangedEventArgs e)
        => RedrawSidebarThermals(HardwareMonitor.Instance?.Latest ?? HardwareSnapshot.Empty);

    private void RedrawSidebarThermals(HardwareSnapshot snap)
    {
        void DrawSb(Canvas c, double temp, Color col)
        {
            c.Children.Clear();
            double w = c.ActualWidth, h = c.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double cx = w / 2, cy = h / 2, r = Math.Min(w, h) / 2 - 5;
            if (r <= 0) return;
            AddArc(c, cx, cy, r, 225, 270, ChartChrome("BgBrush", Color.FromRgb(0x0B, 0x0F, 0x14)), 6);
            if (temp > 0)
            {
                double frac = Math.Clamp((temp - 30) / 65.0, 0, 1);
                if (frac > 0.002) AddArc(c, cx, cy, r, 225, 270 * frac, new SolidColorBrush(col), 6);
            }
        }

        if (snap.CpuTemp > 0)
        {
            var col = TempHeatColor(snap.CpuTemp);
            SbCpuTemp.Text = $"{snap.CpuTemp:0}°";
            SbCpuTemp.Foreground = new SolidColorBrush(col);
            DrawSb(SbCpuGauge, snap.CpuTemp, col);
        }
        else { SbCpuTemp.Text = "—"; DrawSb(SbCpuGauge, 0, default); }

        if (snap.GpuTemp > 0)
        {
            var col = TempHeatColor(snap.GpuTemp);
            SbGpuTemp.Text = $"{snap.GpuTemp:0}°";
            SbGpuTemp.Foreground = new SolidColorBrush(col);
            DrawSb(SbGpuGauge, snap.GpuTemp, col);
        }
        else { SbGpuTemp.Text = "—"; DrawSb(SbGpuGauge, 0, default); }
    }

    // ── Hardware hero: thermal radial gauges + overlaid CPU/GPU temp timeline ──

    private void OnHwHeroSizeChanged(object sender, SizeChangedEventArgs e)
        => RedrawHwHero(HardwareMonitor.Instance?.Latest ?? HardwareSnapshot.Empty);

    private void RedrawHwHero(HardwareSnapshot snap)
    {
        // Ring fill uses each component's assigned color (consistent with RAM and the cards
        // below); the center number stays thermal-heat-colored so temperature still reads hot/cold.
        var cpuRing = (TryFindResource("ChartCpuBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x5D, 0xAD, 0xE2);
        var gpuRing = (TryFindResource("ChartGpuBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0xFF, 0x88, 0x32);

        // CPU — temperature gauge mapped across 30–95 °C
        if (snap.CpuTemp > 0)
        {
            CpuGaugeVal.Text = $"{snap.CpuTemp:0}";
            CpuGaugeVal.Foreground = new SolidColorBrush(TempHeatColor(snap.CpuTemp));
            CpuGaugeSub.Text = $"load {snap.CpuLoad:0}%";
            DrawGauge(CpuGauge, snap.CpuTemp, 30, 95, cpuRing);
        }
        else { CpuGaugeVal.Text = "—"; CpuGaugeSub.Text = "load —%"; DrawGauge(CpuGauge, 0, 30, 95, default); }

        // GPU
        if (snap.GpuTemp > 0)
        {
            GpuGaugeVal.Text = $"{snap.GpuTemp:0}";
            GpuGaugeVal.Foreground = new SolidColorBrush(TempHeatColor(snap.GpuTemp));
            GpuGaugeSub.Text = $"load {snap.GpuLoad:0}%";
            DrawGauge(GpuGauge, snap.GpuTemp, 30, 95, gpuRing);
        }
        else { GpuGaugeVal.Text = "—"; GpuGaugeSub.Text = "load —%"; DrawGauge(GpuGauge, 0, 30, 95, default); }

        // RAM — % used, colored with the RAM chart color
        if (snap.RamTotalGb > 0)
        {
            var c = (TryFindResource("ChartRamBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x3D, 0xBA, 0x6F);
            RamGaugeVal.Text = $"{snap.RamUsedGb:0}";
            RamGaugeVal.Foreground = new SolidColorBrush(c);
            RamGaugeSub.Text = $"{snap.RamPct:0}% · {snap.RamTotalGb:0} GB";
            DrawGauge(RamGauge, snap.RamPct, 0, 100, c);
        }
        else { RamGaugeVal.Text = "—"; RamGaugeSub.Text = "—%"; DrawGauge(RamGauge, 0, 0, 100, default); }

        DrawOverlayTempChart(OverlayTempChart);
    }

    /// <summary>270° radial gauge: subtle track + value arc, gap centered at the bottom.</summary>
    private static void DrawGauge(Canvas c, double value, double min, double max, Color color)
    {
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double cx = w / 2, cy = h / 2, r = Math.Min(w, h) / 2 - 10;
        if (r <= 0) return;

        AddArc(c, cx, cy, r, 225, 270, ChartChrome("PanelAltBrush", Color.FromRgb(0x1C, 0x25, 0x30)), 9);

        double frac = max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;
        if (frac > 0.002)
            AddArc(c, cx, cy, r, 225, 270 * frac, new SolidColorBrush(color), 9);
    }

    /// <summary>Stroke an arc starting at <paramref name="startDeg"/> sweeping clockwise by <paramref name="sweepDeg"/>.</summary>
    private static void AddArc(Canvas c, double cx, double cy, double r,
                               double startDeg, double sweepDeg, Brush stroke, double thick)
    {
        double a0 = startDeg * Math.PI / 180.0;
        double a1 = (startDeg - sweepDeg) * Math.PI / 180.0;   // clockwise = decreasing angle
        var p0 = new Point(cx + r * Math.Cos(a0), cy - r * Math.Sin(a0));
        var p1 = new Point(cx + r * Math.Cos(a1), cy - r * Math.Sin(a1));
        var fig = new PathFigure { StartPoint = p0, IsFilled = false };
        fig.Segments.Add(new ArcSegment(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true));
        c.Children.Add(new Path
        {
            Data = new PathGeometry(new[] { fig }),
            Stroke = stroke,
            StrokeThickness = thick,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
    }

    /// <summary>CPU and GPU temperature lines overlaid on one shared time axis (WiFiman-style).</summary>
    private void DrawOverlayTempChart(Canvas canvas)
    {
        canvas.Children.Clear();
        int count = _tempBufCount, head = _tempBufHead, len = TempHistLen;
        if (count < 2) return;
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        float dMin = float.MaxValue, dMax = float.MinValue;
        void Scan(float[] buf)
        {
            for (int i = 0; i < count; i++)
            {
                float v = buf[(head - count + i + len) % len];
                if (v > 0) { if (v < dMin) dMin = v; if (v > dMax) dMax = v; }
            }
        }
        Scan(_cpuTempBuf); Scan(_gpuTempBuf);
        if (dMax < dMin) { dMin = 0; dMax = 1; }
        if (dMax <= dMin) dMax = dMin + 1;
        float range = dMax - dMin, sMin = dMin - range * 0.12f, sMax = dMax + range * 0.12f;
        _ovSMin = sMin; _ovSMax = sMax;   // cache for the crosshair

        var grid = ChartChrome("BorderBrush", Color.FromArgb(40, 255, 255, 255));
        for (int g = 1; g <= 3; g++)
        {
            double yy = h * g / 4.0;
            canvas.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = w, Y1 = yy, Y2 = yy, Stroke = grid, StrokeThickness = 1 });
        }

        void DrawSeries(float[] buf, Color col)
        {
            // Collect contiguous runs (a gap is opened wherever a sample is unavailable).
            var runs = new List<List<Point>>();
            List<Point>? cur = null;
            Point last = new(double.NaN, double.NaN);
            for (int i = 0; i < count; i++)
            {
                float v = buf[(head - count + i + len) % len];
                if (v <= 0) { cur = null; continue; }   // gap over unavailable samples
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
            var stroke = new SolidColorBrush(col);

            foreach (var run in runs)
            {
                if (run.Count >= 2)
                    canvas.Children.Add(new Path { Data = SmoothTempPath(run, true, h), Fill = grad });
                canvas.Children.Add(new Path
                {
                    Data = SmoothTempPath(run, false, h),
                    Stroke = stroke, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round
                });
            }
            if (!double.IsNaN(last.X))
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(col) };
                Canvas.SetLeft(dot, last.X - 3.5); Canvas.SetTop(dot, last.Y - 3.5);
                canvas.Children.Add(dot);
            }
        }

        var cpuC = (TryFindResource("ChartCpuBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x5D, 0xAD, 0xE2);
        var gpuC = (TryFindResource("ChartGpuBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0xFF, 0x88, 0x32);
        DrawSeries(_cpuTempBuf, cpuC);
        DrawSeries(_gpuTempBuf, gpuC);

        var lblBrush = ChartChrome("SubtleTextBrush", Color.FromArgb(140, 200, 210, 220));
        void Label(string t, double top)
        {
            var tb = new TextBlock { Text = t, FontSize = 9, Foreground = lblBrush, IsHitTestVisible = false };
            Canvas.SetLeft(tb, 3); Canvas.SetTop(tb, top);
            canvas.Children.Add(tb);
        }
        Label($"{dMax:0}°", 1);
        Label($"{dMin:0}°", h - 13);
    }

    /// <summary>
    /// Catmull-Rom → cubic-bezier smoothing for a temperature run. When
    /// <paramref name="fillToBottom"/> is set, the figure is closed down to the baseline
    /// (y = h) so it can be filled with a gradient.
    /// </summary>
    private static PathGeometry SmoothTempPath(List<Point> pts, bool fillToBottom, double h)
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


    // ── Hardware chart crosshair ──────────────────────────────────────────────

    private void OnCpuChartMouseMove(object sender, MouseEventArgs e)
        => DrawHwCrosshair(CpuTempOverlay, _cpuTempBuf, _tempBufHead, _tempBufCount,
                           e.GetPosition(CpuTempOverlay), "°");

    private void OnGpuChartMouseMove(object sender, MouseEventArgs e)
        => DrawHwCrosshair(GpuTempOverlay, _gpuTempBuf, _tempBufHead, _tempBufCount,
                           e.GetPosition(GpuTempOverlay), "°");

    private void OnRamChartMouseMove(object sender, MouseEventArgs e)
        => DrawHwCrosshair(RamUsageOverlay, _ramPctBuf, _tempBufHead, _tempBufCount,
                           e.GetPosition(RamUsageOverlay), "%");

    private void OnHwChartMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Canvas c) c.Children.Clear();
    }

    private static void DrawHwCrosshair(Canvas overlay, float[] buf, int head, int count,
                                        Point pos, string unit)
    {
        overlay.Children.Clear();
        if (count < 2) return;
        double w = overlay.ActualWidth;
        double h = overlay.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Map mouse X → sample index
        int idx = (int)Math.Round(pos.X / w * (count - 1));
        idx = Math.Clamp(idx, 0, count - 1);
        float value = buf[(head - count + idx + buf.Length) % buf.Length];

        // Vertical crosshair line
        double lineX = count == 1 ? 0 : idx * (w / (count - 1));
        overlay.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = lineX, Y1 = 0, X2 = lineX, Y2 = h,
            Stroke = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)),
            StrokeThickness = 1, IsHitTestVisible = false,
            StrokeDashArray = _dashTwo
        });

        // Value bubble
        string text = unit == "%" ? $"{value:0.0}%" : $"{value:0.0}{unit}";
        var bubble = new Border
        {
            Child = new TextBlock
            {
                Text = text, FontSize = 9,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = new SolidColorBrush(Colors.White),
                IsHitTestVisible = false
            },
            Background      = new SolidColorBrush(Color.FromArgb(210, 14, 16, 26)),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(5, 2, 5, 2),
            IsHitTestVisible = false
        };
        bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bw = bubble.DesiredSize.Width;
        double bx = lineX + 5;
        if (bx + bw > w) bx = lineX - bw - 5;
        Canvas.SetLeft(bubble, Math.Max(0, bx));
        Canvas.SetTop(bubble, 2);
        overlay.Children.Add(bubble);
    }

    // Crosshair for the overlaid CPU/GPU timeline — reads out BOTH series at the hovered time.
    private void OnOverlayTempMouseMove(object sender, MouseEventArgs e)
    {
        var overlay = OverlayTempOverlay;
        overlay.Children.Clear();
        int count = _tempBufCount, head = _tempBufHead, len = TempHistLen;
        if (count < 2) return;
        double w = overlay.ActualWidth, h = overlay.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var pos = e.GetPosition(overlay);
        int idx = Math.Clamp((int)Math.Round(pos.X / w * (count - 1)), 0, count - 1);
        double lineX = idx * (w / (count - 1));
        float cpu = _cpuTempBuf[(head - count + idx + len) % len];
        float gpu = _gpuTempBuf[(head - count + idx + len) % len];

        overlay.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = lineX, Y1 = 0, X2 = lineX, Y2 = h,
            Stroke = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)),
            StrokeThickness = 1, IsHitTestVisible = false, StrokeDashArray = _dashTwo
        });

        var cpuC = (TryFindResource("ChartCpuBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x5D, 0xAD, 0xE2);
        var gpuC = (TryFindResource("ChartGpuBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0xFF, 0x88, 0x32);

        void Dot(float v, Color col)
        {
            if (v <= 0) return;
            double y = Math.Clamp(h - ((v - _ovSMin) / (_ovSMax - _ovSMin)) * h, 0, h);
            var d = new System.Windows.Shapes.Ellipse
            {
                Width = 9, Height = 9,
                Stroke = new SolidColorBrush(col), StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(230, 14, 16, 26)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(d, lineX - 4.5); Canvas.SetTop(d, y - 4.5);
            overlay.Children.Add(d);
        }
        Dot(cpu, cpuC); Dot(gpu, gpuC);

        var sp = new StackPanel();
        if (cpu > 0) sp.Children.Add(new TextBlock { Text = $"CPU {cpu:0}°", FontSize = 9, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Foreground = new SolidColorBrush(cpuC), IsHitTestVisible = false });
        if (gpu > 0) sp.Children.Add(new TextBlock { Text = $"GPU {gpu:0}°", FontSize = 9, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Foreground = new SolidColorBrush(gpuC), IsHitTestVisible = false });
        if (sp.Children.Count == 0) return;

        var bubble = new Border
        {
            Child = sp,
            Background = new SolidColorBrush(Color.FromArgb(210, 14, 16, 26)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3, 6, 3), IsHitTestVisible = false
        };
        bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double bx = lineX + 7;
        if (bx + bubble.DesiredSize.Width > w) bx = lineX - bubble.DesiredSize.Width - 7;
        Canvas.SetLeft(bubble, Math.Max(0, bx));
        Canvas.SetTop(bubble, 2);
        overlay.Children.Add(bubble);
    }


}
