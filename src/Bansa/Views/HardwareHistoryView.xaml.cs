using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Bansa.Services;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Path = System.Windows.Shapes.Path;

namespace Bansa.Views;

public partial class HardwareHistoryView : UserControl
{
    private readonly DispatcherTimer _elapsedTimer;

    // null = viewing the live / current recording; otherwise viewing a loaded saved session.
    private SavedSession? _viewing;
    private bool _suppressPicker;

    /// <summary>
    /// Plain (non-Visual) ComboBox item. Adding <see cref="ComboBoxItem"/> instances directly
    /// crashes the ComboBox ("disconnect child from parent Visual") because the selected
    /// container would be reparented into the selection box — so we use a data object instead.
    /// </summary>
    private sealed class PickerItem
    {
        public string? Id { get; init; }   // null = the Live / current entry
        public string Label { get; init; } = "";
        public override string ToString() => Label;
    }

    public HardwareHistoryView()
    {
        InitializeComponent();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => UpdateElapsed();
        Loaded += (_, _) =>
        {
            // Re-subscribe on (re)load; unsubscribe-first keeps it idempotent across nav cycles.
            SessionRecorder.Updated -= OnRecorderUpdated;
            SessionRecorder.Updated += OnRecorderUpdated;
            SessionStore.Changed    -= OnStoreChanged;
            SessionStore.Changed    += OnStoreChanged;
            RefreshSessionPicker();
            Reload();
        };
        Unloaded += (_, _) =>
        {
            SessionRecorder.Updated -= OnRecorderUpdated;
            SessionStore.Changed    -= OnStoreChanged;
        };
    }

    private void OnRecorderUpdated()
        => Dispatcher.BeginInvoke(new Action(Reload));

    private void OnStoreChanged()
        => Dispatcher.BeginInvoke(new Action(RefreshSessionPicker));

    // Samples currently on display: a loaded saved session, or the live recorder buffer.
    private List<SessionRecorder.Sample> CurrentSamples()
        => _viewing?.Samples ?? SessionRecorder.Snapshot();

    private void OnRecordToggle(object sender, RoutedEventArgs e)
    {
        // Recording always operates on the live session — leave any saved-session view first.
        if (_viewing is not null)
        {
            _viewing = null;
            SyncPickerSelection();
        }

        if (SessionRecorder.IsRecording)
        {
            SessionRecorder.Stop();
            _elapsedTimer.Stop();
        }
        else
        {
            SessionRecorder.Start();
            _elapsedTimer.Start();
        }
        Reload();
    }

    // ── Saved-session picker ───────────────────────────────────────────────────

    private void RefreshSessionPicker()
    {
        _suppressPicker = true;
        SessionPicker.Items.Clear();
        SessionPicker.Items.Add(new PickerItem { Id = null, Label = "● Live / current" });
        foreach (var s in SessionStore.List())
            SessionPicker.Items.Add(new PickerItem
            {
                Id = s.Id,
                Label = $"{s.StartedAt:MMM d, HH:mm}  ·  {Fmt(s.Duration)}"
            });
        _suppressPicker = false;
        SyncPickerSelection();
    }

    private void SyncPickerSelection()
    {
        _suppressPicker = true;
        int idx = 0;
        if (_viewing is not null)
            for (int i = 1; i < SessionPicker.Items.Count; i++)
                if (SessionPicker.Items[i] is PickerItem { Id: { } id } && id == _viewing.Id) { idx = i; break; }
        SessionPicker.SelectedIndex = Math.Min(idx, SessionPicker.Items.Count - 1);
        _suppressPicker = false;
    }

    private void OnSessionPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPicker) return;
        var id = (SessionPicker.SelectedItem as PickerItem)?.Id;
        _viewing = id is null ? null : SessionStore.Load(id);
        Reload();
    }

    private void OnDeleteSession(object sender, RoutedEventArgs e)
    {
        if (_viewing is null) return;
        var id = _viewing.Id;
        bool ok = ConfirmDialog.Show("Delete session?",
            $"Delete the saved session from {_viewing.StartedAt:MMM d, HH:mm}? This can't be undone.",
            "Delete", "Cancel", danger: true);
        if (!ok) return;
        SessionStore.Delete(id);
        _viewing = null;
        RefreshSessionPicker();   // also re-syncs selection back to Live
        Reload();
    }

    private static string Fmt(TimeSpan d)
        => d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{d.Minutes}m {d.Seconds}s";

    public void Reload()
    {
        bool live = _viewing is null;
        bool rec  = SessionRecorder.IsRecording;

        RecordBtn.IsEnabled = live;
        RecordBtnText.Text = rec ? "Stop recording" : "Record session";
        RecDot.Fill = rec ? new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C)) : new SolidColorBrush(Colors.White);
        DeleteBtn.IsEnabled = !live;

        var samples = CurrentSamples();
        ExportBtn.IsEnabled = samples.Count > 0;
        SampleCountText.Text = samples.Count > 0 ? $"{samples.Count} samples" : "";
        UpdateElapsed();

        EmptyNotice.Visibility = samples.Count >= 2 ? Visibility.Collapsed : Visibility.Visible;
        DrawSessionChart(samples);
        UpdateStats(samples);
        UpdateCulprits(samples);
    }

    private void UpdateElapsed()
    {
        if (_viewing is not null)
        {
            ElapsedText.Text = $"{_viewing.StartedAt:MMM d, HH:mm} · {Fmt(_viewing.Duration)}";
        }
        else if (SessionRecorder.IsRecording)
        {
            var d = DateTime.Now - SessionRecorder.StartedAt;
            ElapsedText.Text = $"● {d:hh\\:mm\\:ss}";
        }
        else
        {
            ElapsedText.Text = SessionRecorder.Count > 0 ? "stopped" : "";
        }
    }

    private Color CpuColor => (TryFindResource("ChartCpuBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x5D, 0xAD, 0xE2);
    private Color GpuColor => (TryFindResource("ChartGpuBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0xFF, 0x88, 0x32);

    private void OnChartSizeChanged(object sender, SizeChangedEventArgs e)
        => DrawSessionChart(SessionRecorder.Snapshot());

    private void DrawSessionChart(List<SessionRecorder.Sample> samples)
    {
        var canvas = SessionChart;
        canvas.Children.Clear();
        if (samples.Count < 2) return;
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        float dMin = float.MaxValue, dMax = float.MinValue;
        foreach (var s in samples)
        {
            if (s.CpuTemp > 0) { dMin = Math.Min(dMin, s.CpuTemp); dMax = Math.Max(dMax, s.CpuTemp); }
            if (s.GpuTemp > 0) { dMin = Math.Min(dMin, s.GpuTemp); dMax = Math.Max(dMax, s.GpuTemp); }
        }
        if (dMax < dMin) { dMin = 0; dMax = 1; }
        if (dMax <= dMin) dMax = dMin + 1;
        float range = dMax - dMin, sMin = dMin - range * 0.12f, sMax = dMax + range * 0.12f;

        var grid = (TryFindResource("BorderBrush") as Brush) ?? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        for (int g = 1; g <= 3; g++)
        {
            double yy = h * g / 4.0;
            canvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = yy, Y2 = yy, Stroke = grid, StrokeThickness = 1 });
        }

        int n = samples.Count;
        void DrawSeries(Func<SessionRecorder.Sample, float> sel, Color col)
        {
            // Contiguous runs (gap wherever a sample is unavailable).
            var runs = new List<List<Point>>();
            List<Point>? cur = null;
            for (int i = 0; i < n; i++)
            {
                float v = sel(samples[i]);
                if (v <= 0) { cur = null; continue; }
                double x = i * (w / (n - 1));
                double y = Math.Clamp(h - ((v - sMin) / (sMax - sMin)) * h, 0, h);
                var p = new Point(x, y);
                if (cur == null) { cur = new List<Point>(); runs.Add(cur); }
                cur.Add(p);
            }
            if (runs.Count == 0) return;

            var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, col.R, col.G, col.B), 0));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x14, col.R, col.G, col.B), 0.7));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, col.R, col.G, col.B), 1));
            var strokeBrush = new SolidColorBrush(col);

            foreach (var run in runs)
            {
                if (run.Count >= 2)
                    canvas.Children.Add(new Path { Data = SmoothPath(run, true, h), Fill = grad });
                canvas.Children.Add(new Path { Data = SmoothPath(run, false, h), Stroke = strokeBrush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
            }
        }
        DrawSeries(s => s.CpuTemp, CpuColor);
        DrawSeries(s => s.GpuTemp, GpuColor);

        var lbl = (TryFindResource("SubtleTextBrush") as Brush) ?? new SolidColorBrush(Color.FromArgb(140, 200, 210, 220));
        void Label(string t, double top)
        {
            var tb = new TextBlock { Text = t, FontSize = 9, Foreground = lbl, IsHitTestVisible = false };
            Canvas.SetLeft(tb, 3); Canvas.SetTop(tb, top);
            canvas.Children.Add(tb);
        }
        Label($"{dMax:0}°", 1);
        Label($"{dMin:0}°", h - 13);
    }

    /// <summary>Catmull-Rom → cubic-bezier smoothing; optionally closed to the baseline for fill.</summary>
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

    private void UpdateStats(List<SessionRecorder.Sample> samples)
    {
        if (samples.Count == 0)
        {
            CpuStatsText.Text = "—"; GpuStatsText.Text = "—"; PeakText.Text = "";
            return;
        }
        (float mn, float mx, float avg) Stat(Func<SessionRecorder.Sample, float> sel)
        {
            float mn = float.MaxValue, mx = 0, sum = 0; int c = 0;
            foreach (var s in samples) { float v = sel(s); if (v > 0) { mn = Math.Min(mn, v); mx = Math.Max(mx, v); sum += v; c++; } }
            return c > 0 ? (mn, mx, sum / c) : (0, 0, 0);
        }
        var cpu = Stat(s => s.CpuTemp);
        var gpu = Stat(s => s.GpuTemp);
        CpuStatsText.Text = cpu.mx > 0 ? $"min {cpu.mn:0}°  avg {cpu.avg:0}°  max {cpu.mx:0}°" : "—";
        GpuStatsText.Text = gpu.mx > 0 ? $"min {gpu.mn:0}°  avg {gpu.avg:0}°  max {gpu.mx:0}°" : "—";

        // Peak GPU moment + the app that was foreground then
        var peak = samples.Where(s => s.GpuTemp > 0).OrderByDescending(s => s.GpuTemp).FirstOrDefault();
        var dur = samples[^1].Time - samples[0].Time;
        string peakApp = peak is not null && !string.IsNullOrEmpty(peak.Foreground) ? $" while {peak.Foreground} was active" : "";
        PeakText.Text = peak is not null
            ? $"Duration {dur:hh\\:mm\\:ss}.  Peak GPU {peak.GpuTemp:0}°{peakApp}."
            : $"Duration {dur:hh\\:mm\\:ss}.";
    }

    private void UpdateCulprits(List<SessionRecorder.Sample> samples)
    {
        CulpritsPanel.Children.Clear();
        var groups = samples
            .Where(s => !string.IsNullOrEmpty(s.Foreground) && s.GpuTemp > 0)
            .GroupBy(s => s.Foreground)
            .Select(g => (App: g.Key, Peak: g.Max(x => x.GpuTemp), Share: (double)g.Count() / samples.Count))
            .OrderByDescending(g => g.Peak)
            .Take(4)
            .ToList();

        CulpritsEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in groups)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = g.App, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 0);

            var val = new TextBlock
            {
                Text = $"{g.Peak:0}° · {g.Share * 100:0}%", FontSize = 11,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("RobotoMonoFamily"),
                Foreground = new SolidColorBrush(TempColor(g.Peak)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(val, 1);

            row.Children.Add(name); row.Children.Add(val);
            CulpritsPanel.Children.Add(row);
        }
    }

    // Cool (user-set "blue") → bright yellow → bright red. See Services/HeatColors.
    private static Color TempColor(double t) => HeatColors.Temp(t);

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        var samples = CurrentSamples();
        if (samples.Count == 0) return;

        var stamp = _viewing?.StartedAt ?? SessionRecorder.StartedAt;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export session",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"bansa-session-{stamp:yyyyMMdd-HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using var sw = new StreamWriter(dlg.FileName);
            sw.WriteLine("Time,CpuTemp,GpuTemp,CpuLoad,GpuLoad,ForegroundApp");
            foreach (var s in samples)
                sw.WriteLine(string.Join(",",
                    s.Time.ToString("o", CultureInfo.InvariantCulture),
                    s.CpuTemp.ToString("0.#", CultureInfo.InvariantCulture),
                    s.GpuTemp.ToString("0.#", CultureInfo.InvariantCulture),
                    s.CpuLoad.ToString("0.#", CultureInfo.InvariantCulture),
                    s.GpuLoad.ToString("0.#", CultureInfo.InvariantCulture),
                    Csv(s.Foreground)));
            ConfirmDialog.Show("Export complete", $"Saved {samples.Count} samples to:\n{dlg.FileName}", "OK", null);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Show("Export failed", ex.Message, "OK", null);
        }
    }

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
