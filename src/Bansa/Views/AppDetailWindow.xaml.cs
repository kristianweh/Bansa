using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Bansa.Services;
using Bansa.ViewModels;

using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Bansa.Views;

public partial class AppDetailWindow : Window
{
    private readonly string _appName;
    private readonly AppRowViewModel _app;
    private readonly DispatcherTimer _liveTimer;
    private List<(long HourTs, long BytesIn, long BytesOut)> _hourly = new();
    private bool _show7Day;

    public AppDetailWindow(AppRowViewModel app)
    {
        InitializeComponent();
        _app     = app;
        _appName = app.Name;
        Title    = app.Name;

        AppNameText.Text = app.Name;
        UpdateLiveHeader();

        // Refresh sparklines and live rate every 500 ms while the window is open.
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _liveTimer.Tick += (_, _) => UpdateLiveHeader();
        _liveTimer.Start();

        Loaded      += (_, _) => LoadData();
        SizeChanged += (_, _) =>
            Dispatcher.BeginInvoke(
                () => DrawChart(_hourly),
                DispatcherPriority.Render);
        Closed += (_, _) => _liveTimer.Stop();
    }

    // ── Live header ───────────────────────────────────────────────────────────

    private void UpdateLiveHeader()
    {
        LiveDownRate.Text = _app.DownRate;
        LiveUpRate.Text   = _app.UpRate;
        LiveDownSpark.Data = _app.DownSparkGeometry;
        LiveUpSpark.Data   = _app.UpSparkGeometry;
    }

    // ── Historical data ───────────────────────────────────────────────────────

    private void LoadData()
    {
        using var store = new HistoryStore();
        try
        {
            var nowUtc  = DateTime.UtcNow;
            var today   = DateTime.UtcNow.Date;
            var week    = today.AddDays(-7);
            var ancient = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            (long tDown, long tUp) = Sum(store.GetTotals(today,   nowUtc), _appName);
            (long wDown, long wUp) = Sum(store.GetTotals(week,    nowUtc), _appName);
            (long aDown, long aUp) = Sum(store.GetTotals(ancient, nowUtc), _appName);

            TodayDown.Text = Format.Bytes(tDown);
            TodayUp.Text   = Format.Bytes(tUp);
            WeekDown.Text  = Format.Bytes(wDown);
            WeekUp.Text    = Format.Bytes(wUp);
            AllDown.Text   = Format.Bytes(aDown);
            AllUp.Text     = Format.Bytes(aUp);

            LoadChart(store);
        }
        catch
        {
            TodayDown.Text = WeekDown.Text = AllDown.Text = "—";
            TodayUp.Text   = WeekUp.Text   = AllUp.Text   = "—";
        }
    }

    private void LoadChart(HistoryStore store)
    {
        var nowUtc = DateTime.UtcNow;
        _hourly = _show7Day
            ? store.GetAppHourly(_appName, nowUtc.AddDays(-7), nowUtc)
            : store.GetAppHourly(_appName, nowUtc.AddHours(-24), nowUtc);
        DrawChart(_hourly);
    }

    // ── Range toggle ──────────────────────────────────────────────────────────

    private void OnRange24h(object sender, RoutedEventArgs e)
    {
        _show7Day         = false;
        Range24hBtn.IsChecked = true;
        Range7dBtn.IsChecked  = false;
        try { using var store = new HistoryStore(); LoadChart(store); } catch { }
    }

    private void OnRange7d(object sender, RoutedEventArgs e)
    {
        _show7Day             = true;
        Range24hBtn.IsChecked = false;
        Range7dBtn.IsChecked  = true;
        try { using var store = new HistoryStore(); LoadChart(store); } catch { }
    }

    // ── Chart drawing ─────────────────────────────────────────────────────────

    private static (long Down, long Up) Sum(
        List<(string Name, long BytesIn, long BytesOut)> rows, string name)
    {
        foreach (var r in rows)
            if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                return (r.BytesIn, r.BytesOut);
        return (0, 0);
    }

    private void DrawChart(List<(long HourTs, long BytesIn, long BytesOut)> hourly)
    {
        ChartCanvas.Children.Clear();
        if (hourly.Count == 0)
        {
            ChartPeakLabel.Text   = "";
            NoDataText.Visibility = Visibility.Visible;
            return;
        }
        NoDataText.Visibility = Visibility.Collapsed;

        double w = ChartCanvas.ActualWidth;
        double h = ChartCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Fill gaps so every hour in the range has a bucket (even if zero)
        int    rangeHours = _show7Day ? 7 * 24 : 24;
        long   nowHour    = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600) * 3600;
        long   fromHour   = nowHour - (rangeHours - 1) * 3600L;
        var    byHour     = hourly.ToDictionary(x => x.HourTs);

        var buckets = new List<(long HourTs, long Down, long Up)>();
        for (long h2 = fromHour; h2 <= nowHour; h2 += 3600)
        {
            if (byHour.TryGetValue(h2, out var v))
                buckets.Add((h2, v.BytesIn, v.BytesOut));
            else
                buckets.Add((h2, 0, 0));
        }

        long peak = buckets.Max(b => Math.Max(b.Down, b.Up));
        if (peak == 0) peak = 1;
        ChartPeakLabel.Text = Format.Bytes(peak) + "/h";

        Color downColor  = ParseColor(App.Settings?.DownColorHex, Color.FromRgb(0x5D, 0xAD, 0xE2));
        Color upColor    = ParseColor(App.Settings?.UpColorHex,   Color.FromRgb(0xF3, 0x9C, 0x12));
        var   downFill   = new SolidColorBrush(Color.FromArgb(160, downColor.R, downColor.G, downColor.B));
        var   upFill     = new SolidColorBrush(Color.FromArgb(120, upColor.R,   upColor.G,   upColor.B));
        var   gridBrush  = new SolidColorBrush(Color.FromArgb(25,  255, 255, 255));
        var   labelBrush = new SolidColorBrush(Color.FromArgb(110, 200, 210, 220));

        int    n       = buckets.Count;
        double barW    = w / n;
        double maxBarH = h * 0.90;

        // Horizontal grid lines
        for (int li = 1; li <= 3; li++)
        {
            double y = h * li / 4.0;
            ChartCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            });
            var lbl = new TextBlock
            {
                Text = Format.Bytes((long)(peak * (4 - li) / 4.0)) + "/h",
                FontSize = 8, Foreground = labelBrush, IsHitTestVisible = false
            };
            Canvas.SetLeft(lbl, 2); Canvas.SetTop(lbl, y - 10);
            ChartCanvas.Children.Add(lbl);
        }

        // Label interval: every 6 h for 24h view, every 24 h (daily) for 7d view
        int labelEvery = _show7Day ? 24 : 6;

        // Bars (download behind, upload in front)
        for (int i = 0; i < n; i++)
        {
            double x  = i * barW;
            double dH = (double)buckets[i].Down / peak * maxBarH;
            double uH = (double)buckets[i].Up   / peak * maxBarH;
            double rW = Math.Max(1, barW - 2);

            if (dH > 0)
            {
                var dRect = new Rectangle { Width = rW, Height = dH, Fill = downFill, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(dRect, x + 1); Canvas.SetTop(dRect, h - dH);
                ChartCanvas.Children.Add(dRect);
            }
            if (uH > 0)
            {
                var uRect = new Rectangle { Width = rW, Height = uH, Fill = upFill, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(uRect, x + 1); Canvas.SetTop(uRect, h - uH);
                ChartCanvas.Children.Add(uRect);
            }

            var dt = DateTimeOffset.FromUnixTimeSeconds(buckets[i].HourTs).LocalDateTime;
            if (dt.Hour % labelEvery == 0)
            {
                string label = _show7Day ? dt.ToString("ddd") : dt.ToString("HH:mm");
                var tLbl = new TextBlock
                {
                    Text = label, FontSize = 8,
                    Foreground = labelBrush, IsHitTestVisible = false
                };
                Canvas.SetLeft(tLbl, x + 1); Canvas.SetTop(tLbl, h - 14);
                ChartCanvas.Children.Add(tLbl);
            }
        }
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }
}
