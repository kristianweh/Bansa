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
    // ────────── Main window graph ──────────

    // Indices of the currently-visible window into _chartHistory (for crosshair mapping).
    private int _chartWinStart, _chartWinEnd;

    private void DrawMainChart(IReadOnlyList<(long Down, long Up)> history)
    {
        MainChartCanvas.Children.Clear();
        _chartHistory = history;
        if (history.Count < 2) { MainChartPeakLabel.Text = ""; return; }

        double w = MainChartCanvas.ActualWidth;
        double h = MainChartCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // ── Compute visible scroll window ─────────────────────────────────────────
        int fullCount = history.Count;
        int scrollOff = Math.Clamp(_chartScrollOffset, 0, Math.Max(0, fullCount - kChartWindow));
        _chartScrollOffset = scrollOff; // clamp in-place
        _chartWinEnd   = fullCount - scrollOff;
        _chartWinStart = Math.Max(0, _chartWinEnd - kChartWindow);

        // Extract the window as a list so the rest of the method is unchanged
        var visibleHistory = new List<(long Down, long Up)>(_chartWinEnd - _chartWinStart);
        for (int i = _chartWinStart; i < _chartWinEnd; i++)
            visibleHistory.Add(history[i]);

        // Update window label
        if (scrollOff == 0)
            ChartWindowLabel.Text = "30 s";
        else
        {
            double secAgo = scrollOff * 0.5;
            ChartWindowLabel.Text = secAgo < 60
                ? $"−{secAgo:0}s"
                : $"−{(int)(secAgo / 60)}m{(int)(secAgo % 60):00}s";
        }

        // Use windowed history from here on
        history = visibleHistory;

        // Smooth the plotted series so steady transfers draw as a flat line. The crosshair
        // tooltip still reads _chartHistory (raw) for exact per-instant values.
        var draw = ChartFx.Smooth(history);

        // Shared or independent peaks depending on dual-scale toggle
        _chartPeakDown = 1; _chartPeakUp = 1;
        foreach (var (d, u) in draw)
        {
            if (d > _chartPeakDown) _chartPeakDown = d;
            if (u > _chartPeakUp)   _chartPeakUp   = u;
        }
        long sharedPeak = Math.Max(_chartPeakDown, _chartPeakUp);
        long peakD = _dualScale ? _chartPeakDown : sharedPeak;
        long peakU = _dualScale ? _chartPeakUp   : sharedPeak;

        MainChartPeakLabel.Text = _dualScale
            ? $"↓{Format.Rate(_chartPeakDown)}  ↑{Format.Rate(_chartPeakUp)}"
            : Format.Rate(sharedPeak);

        // Rebuild color-dependent brushes only when the user's color choice changes
        Color downColor = ParseColor(App.Settings?.DownColorHex, Color.FromRgb(0x5D, 0xAD, 0xE2));
        Color upColor   = ParseColor(App.Settings?.UpColorHex,   Color.FromRgb(0xF3, 0x9C, 0x12));
        if (downColor != _cachedChartDown || upColor != _cachedChartUp || _chartDownStroke is null)
        {
            _cachedChartDown = downColor;
            _cachedChartUp   = upColor;
            _chartDownStroke = FrozenBrush(downColor);
            _chartUpStroke   = FrozenBrush(upColor);
            _chartDownFill   = FrozenBrush(Color.FromArgb(80, downColor.R, downColor.G, downColor.B));
            _chartUpFill     = FrozenBrush(Color.FromArgb(48, upColor.R,   upColor.G,   upColor.B));
        }
        var downStroke = _chartDownStroke!;
        var upStroke   = _chartUpStroke!;
        var downFill   = _chartDownFill!;
        var upFill     = _chartUpFill!;

        // Theme-aware chart chrome (resolved once per redraw).
        var gridBrush  = ChartChrome("BorderBrush",     Color.FromArgb(25, 255, 255, 255));
        var labelBrush = ChartChrome("SubtleTextBrush", Color.FromArgb(110, 200, 210, 220));
        var tickBrush  = ChartChrome("BorderBrush",     Color.FromArgb(40, 255, 255, 255));

        // ── Horizontal grid lines + Y-axis labels ────────────────────────────────
        for (int li = 1; li <= 3; li++)
        {
            double y = h * li / 4.0;
            MainChartCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1,
                StrokeDashArray = _dashFour
            });
            long labelVal = (long)(sharedPeak * (4 - li) / 4.0);
            var lbl = new TextBlock
            {
                Text = Format.Rate(labelVal), FontSize = 9.5,
                Foreground = labelBrush, IsHitTestVisible = false
            };
            Canvas.SetLeft(lbl, 4); Canvas.SetTop(lbl, y - 12);
            MainChartCanvas.Children.Add(lbl);
        }

        // ── Time markers (every 10 s = every 20 samples at 500 ms/sample) ────────
        int n = history.Count;
        for (int sAgo = 10; sAgo < 30; sAgo += 10)
        {
            int sIdx = n - 1 - sAgo * 2;
            if (sIdx <= 0) continue;
            double tx = sIdx * (w / (n - 1));
            MainChartCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = tx, Y1 = 0, X2 = tx, Y2 = h,
                Stroke = tickBrush, StrokeThickness = 1
            });
            var tLbl = new TextBlock
            {
                Text = $"-{sAgo}s", FontSize = 9,
                Foreground = labelBrush, IsHitTestVisible = false
            };
            Canvas.SetLeft(tLbl, tx + 2); Canvas.SetTop(tLbl, h - 16);
            MainChartCanvas.Children.Add(tLbl);
        }

        // ── Upload cap indicator ──────────────────────────────────────────────────
        long capBps = (App.Settings?.GlobalUploadCapKBs ?? 0) * 1024L;
        if (capBps > 0 && peakU > 0)
        {
            double capY = h - ((double)capBps / peakU) * h * 0.90;
            if (capY >= 0 && capY <= h)
            {
                MainChartCanvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 0, Y1 = capY, X2 = w, Y2 = capY,
                    Stroke = upStroke, StrokeThickness = 1.5, Opacity = 0.7,
                    StrokeDashArray = _dashSix
                });
                var capLbl = new TextBlock
                {
                    Text = $"↑ cap {Format.Rate(capBps)}", FontSize = 9,
                    Foreground = upStroke, IsHitTestVisible = false
                };
                Canvas.SetLeft(capLbl, w - 100); Canvas.SetTop(capLbl, capY - 13);
                MainChartCanvas.Children.Add(capLbl);
            }
        }

        // ── Sparklines (smooth Catmull-Rom curves) ───────────────────────────────
        var downPts = new List<Point>();
        var upPts   = new List<Point>();
        for (int i = 0; i < n; i++)
        {
            double x = n == 1 ? 0 : i * (w / (n - 1));
            downPts.Add(new Point(x, h - ((double)draw[i].Down / peakD) * h * 0.90));
            upPts.Add(  new Point(x, h - ((double)draw[i].Up   / peakU) * h * 0.90));
        }

        MainChartCanvas.Children.Add(new System.Windows.Shapes.Path { Data = BuildSmoothPath(downPts, true,  h), Fill = downFill });
        MainChartCanvas.Children.Add(new System.Windows.Shapes.Path { Data = BuildSmoothPath(upPts,   true,  h), Fill = upFill });
        MainChartCanvas.Children.Add(new System.Windows.Shapes.Path { Data = BuildSmoothPath(downPts, false, h), Stroke = downStroke, StrokeThickness = 1.5 });
        MainChartCanvas.Children.Add(new System.Windows.Shapes.Path { Data = BuildSmoothPath(upPts,   false, h), Stroke = upStroke,   StrokeThickness = 1.5 });

        // ── Live-rate dots at the rightmost point ─────────────────────────────────
        var dDot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = downStroke };
        Canvas.SetLeft(dDot, downPts[^1].X - 4); Canvas.SetTop(dDot, downPts[^1].Y - 4);
        MainChartCanvas.Children.Add(dDot);

        var uDot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = upStroke };
        Canvas.SetLeft(uDot, upPts[^1].X - 4);   Canvas.SetTop(uDot, upPts[^1].Y - 4);
        MainChartCanvas.Children.Add(uDot);
    }

    // ── Crosshair (ChartOverlay mouse events) ────────────────────────────────────

    private void OnChartMouseMove(object sender, MouseEventArgs e)
    {
        double w = ChartOverlay.ActualWidth;
        double h = ChartOverlay.ActualHeight;
        if (w <= 0 || h <= 0 || _chartHistory.Count < 2) return;

        var pos = e.GetPosition(ChartOverlay);

        // Handle drag-scroll
        if (e.LeftButton == MouseButtonState.Pressed && !_chartDragging
            && Math.Abs(pos.X - _chartDragStartX) > 4)
        {
            _chartDragging = true; // latch drag once we see meaningful movement
        }
        if (_chartDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            ChartOverlay.Cursor = System.Windows.Input.Cursors.Hand;
            double dx = pos.X - _chartDragStartX;   // drag right = scroll back in time
            int winSize = Math.Max(1, _chartWinEnd - _chartWinStart);
            double samplesPerPx = Math.Max(1.0, (double)Math.Max(0, _chartHistory.Count - kChartWindow) / w);
            _chartScrollOffset = Math.Clamp(
                _chartDragStartOffset + (int)(dx * samplesPerPx),
                0, Math.Max(0, _chartHistory.Count - kChartWindow));
            DrawMainChart(_chartHistory);
            return;
        }

        // Crosshair: map mouse X → index within the visible window → full history index
        int winLen = _chartWinEnd - _chartWinStart;
        if (winLen < 2) return;
        int winIdx = (int)Math.Round(pos.X / w * (winLen - 1));
        winIdx = Math.Max(0, Math.Min(winLen - 1, winIdx));
        int idx    = _chartWinStart + winIdx;   // absolute index in _chartHistory
        int n      = _chartHistory.Count;

        // ── Ensure crosshair line exists ─────────────────────────────────────
        if (_crosshairLine is null)
        {
            _crosshairLine = new System.Windows.Shapes.Line
            {
                Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                StrokeThickness = 1, IsHitTestVisible = false,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            };
            _crosshairPanel = new Border
            {
                IsHitTestVisible = false,
                Background       = ChartChrome("PanelBrush",  Color.FromArgb(230, 14, 16, 26)),
                BorderBrush      = ChartChrome("BorderBrush", Color.FromArgb(60, 255, 255, 255)),
                BorderThickness  = new Thickness(1),
                CornerRadius     = new CornerRadius(8),
            };
            _crosshairPanel.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16, ShadowDepth = 4, Opacity = 0.5,
                Color = Colors.Black, Direction = 270
            };
            ChartOverlay.Children.Add(_crosshairLine);
            ChartOverlay.Children.Add(_crosshairPanel);
        }

        // ── Always update line position ──────────────────────────────────────
        _crosshairLine.X1 = pos.X; _crosshairLine.Y1 = 0;
        _crosshairLine.X2 = pos.X; _crosshairLine.Y2 = h;

        // ── Rebuild panel content only when the time-index changes ───────────
        if (idx != _lastCrosshairIdx)
        {
            _lastCrosshairIdx = idx;
            var (down, up) = _chartHistory[idx];
            double secsAgo = (n - 1 - idx) * 0.5;
            string timeStr = secsAgo < 0.25 ? "now" : secsAgo < 60
                ? $"-{secsAgo:0.#}s"
                : $"-{(int)(secsAgo/60)}m{(int)(secsAgo%60):00}s";

            var panel = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };

            // ── Header: total rates + time ───────────────────────────────────
            var downBrush = (Brush)(Application.Current.Resources.Contains("ChartDownBrush")
                ? Application.Current.Resources["ChartDownBrush"]
                : new SolidColorBrush(Color.FromRgb(0x5D, 0xAD, 0xE2)));
            var upBrush   = (Brush)(Application.Current.Resources.Contains("ChartUpBrush")
                ? Application.Current.Resources["ChartUpBrush"]
                : new SolidColorBrush(Color.FromRgb(0xF3, 0x9C, 0x12)));
            var dimBrush  = ChartChrome("SubtleTextBrush", Color.FromArgb(130, 200, 210, 230));
            var valueBrush = ChartChrome("TextBrush", Colors.White);

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = "↓ ", Foreground = downBrush, FontWeight = FontWeights.SemiBold, FontSize = 11 });
            header.Children.Add(new TextBlock { Text = Format.Bytes(down / 2), Foreground = valueBrush, FontWeight = FontWeights.SemiBold, FontSize = 11, MinWidth = 72 });
            header.Children.Add(new TextBlock { Text = "  ↑ ", Foreground = upBrush, FontWeight = FontWeights.SemiBold, FontSize = 11 });
            header.Children.Add(new TextBlock { Text = Format.Bytes(up / 2), Foreground = valueBrush, FontWeight = FontWeights.SemiBold, FontSize = 11, MinWidth = 72 });
            header.Children.Add(new TextBlock { Text = $"  {timeStr}", Foreground = dimBrush, FontSize = 10 });
            panel.Children.Add(header);

            // ── Per-app rows (idx is the absolute _appHistory index) ─────────
            if (idx < _appHistory.Count && _appHistory[idx] is { Length: > 0 } apps)
            {
                panel.Children.Add(new Border
                {
                    Height = 1, Margin = new Thickness(-10, 6, -10, 5),
                    Background = ChartChrome("BorderBrush", Color.FromArgb(40, 255, 255, 255))
                });

                foreach (var (name, imgPath, appDown, appUp) in apps)
                {
                    var row = new Grid { Margin = new Thickness(0, 3, 0, 0) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // icon
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // down
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // up

                    // App icon
                    var icon = AppIconCache.Get(imgPath);
                    if (icon != null)
                    {
                        var img = new System.Windows.Controls.Image
                        {
                            Source = icon, Width = 14, Height = 14,
                            Margin = new Thickness(0, 0, 6, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                        Grid.SetColumn(img, 0);
                        row.Children.Add(img);
                    }

                    // App name
                    string displayName = name.Length > 20 ? name[..19] + "…" : name;
                    var nameTb = new TextBlock
                    {
                        Text = displayName,
                        Foreground = valueBrush,
                        FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0)
                    };
                    Grid.SetColumn(nameTb, 1);
                    row.Children.Add(nameTb);

                    // Download rate
                    if (appDown > 500)
                    {
                        var tb = new TextBlock
                        {
                            Text = $"↓ {Format.Bytes(appDown / 2)}",
                            Foreground = downBrush, FontSize = 10,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 6, 0)
                        };
                        Grid.SetColumn(tb, 2);
                        row.Children.Add(tb);
                    }

                    // Upload rate
                    if (appUp > 500)
                    {
                        var tb = new TextBlock
                        {
                            Text = $"↑ {Format.Bytes(appUp / 2)}",
                            Foreground = upBrush, FontSize = 10,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        Grid.SetColumn(tb, 3);
                        row.Children.Add(tb);
                    }

                    panel.Children.Add(row);
                }
            }

            // ── Hardware snapshot row ────────────────────────────────────────
            var hw = HardwareMonitor.Instance?.Latest;
            if (hw != null && hw != HardwareSnapshot.Empty &&
                (hw.CpuLoad > 0 || hw.GpuLoad > 0 || hw.RamTotalGb > 0))
            {
                panel.Children.Add(new Border
                {
                    Height = 1, Margin = new Thickness(-10, 6, -10, 4),
                    Background = ChartChrome("BorderBrush", Color.FromArgb(40, 255, 255, 255))
                });

                // CPU / GPU / RAM labels follow their assigned hardware colors (consistent with
                // the Hardware tab, tray popup and float HUD) — not the accent / upload / success brushes.
                var cpuBrush = (Brush)(Application.Current.Resources.Contains("ChartCpuBrush")
                    ? Application.Current.Resources["ChartCpuBrush"]
                    : new SolidColorBrush(Color.FromRgb(0x5D, 0xAD, 0xE2)));
                var gpuBrush = (Brush)(Application.Current.Resources.Contains("ChartGpuBrush")
                    ? Application.Current.Resources["ChartGpuBrush"]
                    : new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x32)));
                var ramBrush = (Brush)(Application.Current.Resources.Contains("ChartRamBrush")
                    ? Application.Current.Resources["ChartRamBrush"]
                    : new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)));

                var hwRow = new StackPanel { Orientation = Orientation.Horizontal };
                if (hw.CpuLoad > 0)
                    hwRow.Children.Add(new TextBlock
                    {
                        Text = $"CPU {hw.CpuLoad:0}%", Foreground = cpuBrush,
                        FontSize = 10, FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 0, 10, 0)
                    });
                if (hw.GpuLoad > 0)
                    hwRow.Children.Add(new TextBlock
                    {
                        Text = $"GPU {hw.GpuLoad:0}%", Foreground = gpuBrush,
                        FontSize = 10, FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 0, 10, 0)
                    });
                if (hw.RamTotalGb > 0)
                    hwRow.Children.Add(new TextBlock
                    {
                        Text = $"RAM {hw.RamPct:0}%", Foreground = ramBrush,
                        FontSize = 10, FontWeight = FontWeights.SemiBold
                    });
                panel.Children.Add(hwRow);
            }

            _crosshairPanel!.Child = panel;
        }

        // ── Position panel (pin to top, flip side near right edge) ──────────
        double lx = pos.X + 14;
        _crosshairPanel!.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double panelW = _crosshairPanel.DesiredSize.Width + 4;
        if (lx + panelW > w) lx = pos.X - panelW - 14;
        Canvas.SetLeft(_crosshairPanel, Math.Max(0, lx));
        Canvas.SetTop (_crosshairPanel, 6);
    }

    private void OnChartMouseLeave(object sender, MouseEventArgs e)
    {
        ChartOverlay.Children.Clear();
        ChartOverlay.Cursor = null;
        _crosshairLine    = null;
        _crosshairPanel   = null;
        _lastCrosshairIdx = -1;
        _chartDragging    = false;
    }

    // ── Preset / dual-scale / clear filter handlers ────────────────────────────

    private void OnPresetAll(object sender, RoutedEventArgs e)  => Vm.HideBelowKBps = 0;
    private void OnPreset1(object sender, RoutedEventArgs e)    => Vm.HideBelowKBps = 1;
    private void OnPreset10(object sender, RoutedEventArgs e)   => Vm.HideBelowKBps = 10;
    private void OnPreset50(object sender, RoutedEventArgs e)   => Vm.HideBelowKBps = 50;
    private void OnClearFilter(object sender, RoutedEventArgs e) => Vm.FilterText = "";

    private void OnAppContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu) return;
        // Find the Quick profile item by Tag — avoids ContextMenu namescope issues
        var quickProfileItem = menu.Items.OfType<System.Windows.Controls.MenuItem>()
            .FirstOrDefault(mi => mi.Tag?.ToString() == "QuickProfile");
        BuildProfileSubmenu(quickProfileItem);
    }

    private void BuildProfileSubmenu(System.Windows.Controls.MenuItem? item)
    {
        if (item is null) return;

        // Always clear (removes both real items and the XAML placeholder)
        item.Items.Clear();

        var profiles = App.Settings.LimitProfiles;

        if (profiles.Count == 0)
        {
            item.Visibility = Visibility.Collapsed;
            // Restore a collapsed placeholder so the submenu popup template stays alive
            item.Items.Add(new System.Windows.Controls.MenuItem { Visibility = Visibility.Collapsed });
            return;
        }

        item.Visibility = Visibility.Visible;
        foreach (var p in profiles)
        {
            var mi = new System.Windows.Controls.MenuItem
            {
                Header = $"{p.Name}  ↑{p.UploadKbps} ↓{p.DownloadKbps} KB/s"
            };
            var profile = p; // capture loop variable
            mi.Click += (_, _) =>
            {
                if (Selected is not null)
                    _ = Vm.ApplyLimitsCommand.ExecuteAsync((Selected, profile.UploadKbps, profile.DownloadKbps));
            };
            item.Items.Add(mi);
        }
    }

    // ── Chart mouse down / up — drag-scroll + click-to-pause ────────────────────

    private void OnChartMouseDown(object sender, MouseButtonEventArgs e)
    {
        _chartDragging      = false;
        _chartDragStartX    = e.GetPosition(ChartOverlay).X;
        _chartDragStartOffset = _chartScrollOffset;
        ChartOverlay.CaptureMouse();
    }

    private void OnChartMouseUp(object sender, MouseButtonEventArgs e)
    {
        ChartOverlay.ReleaseMouseCapture();
        ChartOverlay.Cursor = null; // restore default
        bool wasDragging = _chartDragging;
        _chartDragging = false;

        if (!wasDragging)
        {
            // Treat as a click: toggle pause / snap to live
            if (_chartScrollOffset > 0)
            {
                // Snap back to live
                _chartScrollOffset = 0;
                _chartPaused = false;
                PauseBanner.Visibility = Visibility.Collapsed;
                DrawMainChart(_chartHistory);
            }
            else
            {
                _chartPaused = !_chartPaused;
                PauseBanner.Visibility = _chartPaused ? Visibility.Visible : Visibility.Collapsed;
                if (!_chartPaused) DrawMainChart(_chartHistory);
            }
        }
        else if (_chartScrollOffset == 0)
        {
            // Dragged back to live — resume
            _chartPaused = false;
            PauseBanner.Visibility = Visibility.Collapsed;
        }
    }


    private static PathGeometry BuildSmoothPath(List<Point> pts, bool fillToBottom, double h)
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


    // ── Dashboard throughput timeline (mirrored: down up / up down) ────────────

    private IReadOnlyList<(long Down, long Up)>? _lastDashThroughput;

    private void OnDashThroughputSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lastDashThroughput is not null) RedrawDashThroughput(_lastDashThroughput);
    }

    /// <summary>
    /// UniFi-style mirrored area chart: download fills upward from a centre axis,
    /// upload fills downward, each with a soft gradient that fades away from the axis.
    /// </summary>
    private void RedrawDashThroughput(IReadOnlyList<(long Down, long Up)> history)
    {
        _lastDashThroughput = history;
        var canvas = DashThroughputChart;
        canvas.Children.Clear();
        if (history is null) return;

        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        int total = history.Count;
        if (w <= 0 || h <= 0 || total < 2) return;

        // Last 120 samples (~60 s) window.
        int win   = Math.Min(total, 120);
        int start = total - win;
        double mid = h / 2.0;

        long peak = 1;
        for (int i = start; i < total; i++)
            peak = Math.Max(peak, Math.Max(history[i].Down, history[i].Up));

        // Centre axis + faint guide lines
        var axis = ChartChrome("BorderBrush", Color.FromArgb(60, 255, 255, 255));
        canvas.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = w, Y1 = mid, Y2 = mid, Stroke = axis, StrokeThickness = 1 });
        var faint = ChartChrome("BorderBrush", Color.FromArgb(26, 255, 255, 255));
        canvas.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = w, Y1 = mid / 2,        Y2 = mid / 2,        Stroke = faint, StrokeThickness = 1, StrokeDashArray = _dashTwo });
        canvas.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = w, Y1 = mid + mid / 2,  Y2 = mid + mid / 2,  Stroke = faint, StrokeThickness = 1, StrokeDashArray = _dashTwo });

        var downPts = new List<Point>(win);
        var upPts   = new List<Point>(win);
        for (int i = 0; i < win; i++)
        {
            var (d, u) = history[start + i];
            double x  = win == 1 ? 0 : i * (w / (win - 1));
            downPts.Add(new Point(x, mid - (double)d / peak * (mid * 0.92)));
            upPts.Add(  new Point(x, mid + (double)u / peak * (mid * 0.92)));
        }

        var downC = (TryFindResource("ChartDownBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x5D, 0xAD, 0xE2);
        var upC   = (TryFindResource("ChartUpBrush")   as SolidColorBrush)?.Color ?? Color.FromRgb(0xF3, 0x9C, 0x12);

        // Download fill: opaque at the axis (bottom of its bbox), fading up toward the peak.
        var downGrad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        downGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, downC.R, downC.G, downC.B), 0));
        downGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, downC.R, downC.G, downC.B), 0.4));
        downGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, downC.R, downC.G, downC.B), 1));

        // Upload fill: opaque at the axis (top of its bbox), fading down.
        var upGrad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        upGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, upC.R, upC.G, upC.B), 0));
        upGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, upC.R, upC.G, upC.B), 0.6));
        upGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, upC.R, upC.G, upC.B), 1));

        canvas.Children.Add(new Path { Data = SmoothTempPath(downPts, true,  mid), Fill = downGrad });
        canvas.Children.Add(new Path { Data = SmoothTempPath(upPts,   true,  mid), Fill = upGrad });
        canvas.Children.Add(new Path { Data = SmoothTempPath(downPts, false, mid), Stroke = new SolidColorBrush(downC), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round });
        canvas.Children.Add(new Path { Data = SmoothTempPath(upPts,   false, mid), Stroke = new SolidColorBrush(upC),   StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round });

        // Live dots at the latest sample
        void Dot(Point p, Color c)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(c) };
            Canvas.SetLeft(dot, p.X - 3); Canvas.SetTop(dot, p.Y - 3);
            canvas.Children.Add(dot);
        }
        Dot(downPts[^1], downC);
        Dot(upPts[^1],   upC);
    }


    // ── Network dashboard: per-app bandwidth-share donut ───────────────────────

    private List<AppRowViewModel>? _lastDonutApps;

    private static readonly Color[] _donutColors =
    {
        Color.FromRgb(0x2D, 0x9C, 0xFF), Color.FromRgb(0x6F, 0xD0, 0xFF),
        Color.FromRgb(0x9B, 0x8C, 0xFF), Color.FromRgb(0x3E, 0xCF, 0x8E),
    };
    private static readonly Color _donutOther = Color.FromRgb(0x3A, 0x46, 0x54);

    private void OnBandwidthDonutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lastDonutApps is not null) RedrawBandwidthDonut(_lastDonutApps);
    }

    private void RedrawBandwidthDonut(List<AppRowViewModel> apps)
    {
        _lastDonutApps = apps;
        var canvas = BandwidthDonut;
        canvas.Children.Clear();
        DonutLegend.Children.Clear();

        long total = 0;
        foreach (var a in apps) total += a.BytesInPerSec;

        // Center readout — split "84.2 MB/s" into number + unit, honoring the unit setting
        if (total > 0)
        {
            string rate = Format.Rate(total);
            int sp = rate.IndexOf(' ');
            DonutTotalVal.Text  = sp > 0 ? rate[..sp] : rate;
            DonutTotalUnit.Text = sp > 0 ? rate[(sp + 1)..] : "total";
        }
        else { DonutTotalVal.Text = "—"; DonutTotalUnit.Text = "idle"; }

        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        DrawDonutTrack(canvas, w, h);
        if (total <= 0) return;

        var top = apps.Where(a => a.BytesInPerSec > 0)
                      .OrderByDescending(a => a.BytesInPerSec)
                      .ToList();
        if (top.Count == 0) return;

        const int MaxSegs = 4;
        var segs = new List<(string name, long bytes, Color col)>();
        for (int i = 0; i < top.Count && i < MaxSegs; i++)
            segs.Add((top[i].Name, top[i].BytesInPerSec, _donutColors[i % _donutColors.Length]));
        long other = total - segs.Sum(s => s.bytes);
        if (other > 0) segs.Add(($"Other ({top.Count - segs.Count})", other, _donutOther));

        double cx = w / 2, cy = h / 2, r = Math.Min(w, h) / 2 - 8;
        double startDeg = 90;   // 12 o'clock, sweeping clockwise
        foreach (var s in segs)
        {
            double sweep = Math.Min((double)s.bytes / total * 360.0, 359.5);
            if (sweep < 0.5) continue;
            AddArc(canvas, cx, cy, r, startDeg, sweep, new SolidColorBrush(s.col), 14);
            startDeg -= sweep;
        }

        foreach (var s in segs)
            DonutLegend.Children.Add(BuildLegendRow(s.name, s.col, Format.Rate(s.bytes)));
    }

    private void DrawDonutTrack(Canvas c, double w, double h)
    {
        double r = Math.Min(w, h) / 2 - 8;
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = 2 * r, Height = 2 * r,
            Stroke = ChartChrome("PanelAltBrush", Color.FromRgb(0x1C, 0x25, 0x30)),
            StrokeThickness = 14
        };
        Canvas.SetLeft(ring, w / 2 - r); Canvas.SetTop(ring, h / 2 - r);
        c.Children.Add(ring);
    }

    private FrameworkElement BuildLegendRow(string name, Color col, string valueText)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var sw = new Border
        {
            Width = 9, Height = 9, CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(col), Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(sw, 0);
        var nm = new TextBlock
        {
            Text = name, FontSize = 12, Foreground = (Brush)FindResource("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(nm, 1);
        var vl = new TextBlock
        {
            Text = valueText, FontSize = 11,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("RobotoMonoFamily"),
            Foreground = (Brush)FindResource("SubtleTextBrush"),
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(vl, 2);
        g.Children.Add(sw); g.Children.Add(nm); g.Children.Add(vl);
        return g;
    }


}
