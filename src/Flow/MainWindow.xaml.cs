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
using Flow.Services;
using Flow.ViewModels;
using Flow.Views;

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

namespace Flow;

public partial class MainWindow : Window
{
    private TrayIconManager? _tray;
    private FloatingGraphWindow? _floatingGraph;

    // Chart state
    private IReadOnlyList<(long Down, long Up)> _chartHistory = Array.Empty<(long, long)>();
    private IReadOnlyList<(string Name, string ImagePath, long DownBps, long UpBps)[]?> _appHistory
        = Array.Empty<(string Name, string ImagePath, long DownBps, long UpBps)[]?>();
    private long _chartPeakDown = 1, _chartPeakUp = 1;
    private bool _dualScale;

    // Crosshair — vertical line + popup panel on ChartOverlay
    private System.Windows.Shapes.Line? _crosshairLine;
    private Border? _crosshairPanel;
    private int _lastCrosshairIdx = -1;   // skip content rebuild when idx unchanged

    // Chart pause state
    private bool _chartPaused;

    // ── Temperature / usage ring buffers for hardware card sparklines ─────────
    // 60 samples × ~2 s per sample ≈ 2 minutes of history.
    // Ring buffer: _tempBufHead points to the NEXT write slot (oldest data is at head).
    private const int TempHistLen = 60;
    private readonly float[] _cpuTempBuf = new float[TempHistLen];
    private readonly float[] _gpuTempBuf = new float[TempHistLen];
    private readonly float[] _ramPctBuf  = new float[TempHistLen];
    private int _tempBufHead;    // next write index
    private int _tempBufCount;   // how many entries are valid (ramps up to TempHistLen)

    // ── Ping sparkline ring buffer (Dashboard ping card) ─────────────────────
    private const int PingHistLen = 60;
    private readonly int[] _pingBuf = new int[PingHistLen];
    private int _pingBufHead;
    private int _pingBufCount;

    // Cached static chart brushes — frozen once, never reallocated per-frame
    private static readonly SolidColorBrush _chartGridBrush  = FrozenBrush(Color.FromArgb(25, 255, 255, 255));
    private static readonly SolidColorBrush _chartLabelBrush = FrozenBrush(Color.FromArgb(110, 200, 210, 220));
    private static readonly SolidColorBrush _chartTickBrush  = FrozenBrush(Color.FromArgb(40, 255, 255, 255));
    private static readonly SolidColorBrush _chartCrosshairDimBrush = FrozenBrush(Color.FromArgb(130, 200, 210, 230));

    private static SolidColorBrush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // Color-dependent chart brushes — rebuilt only when user changes color in settings
    private SolidColorBrush? _chartDownStroke, _chartDownFill, _chartUpStroke, _chartUpFill;
    private Color _cachedChartDown, _cachedChartUp;

    // Shutdown guard — prevents the FloatingGraphWindow.Closed inline handler from saving
    // ShowFloatingGraph=false while OnClosing is already handling the final correct save.
    private bool _isClosing;

    // Global hotkey (Ctrl+Shift+<key>)
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private const int    _hotKeyId    = 9001;
    private const uint   MOD_CONTROL  = 0x0002;
    private const uint   MOD_SHIFT    = 0x0004;
    private const int    WM_HOTKEY    = 0x0312;
    private uint         _hotkeyVk    = 0x46;   // 'F' by default; overridden from settings

    // Hotkey capture state
    private bool _capturingHotkey;

    // Curated palette for swatch pickers
    private static readonly string[] _palette =
    {
        "#5DADE2", "#3B82F6", "#5865F2", "#8B5CF6", "#EC4899",
        "#EF4444", "#F59E0B", "#F39C12", "#10B981", "#06B6D4",
        "#94A3B8",
    };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        SourceInitialized += OnSourceInitialized;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowsAccent.TryApplyMica(hwnd, dark: ThemeManager.Current == AppTheme.Dark);
        ThemeManager.ThemeChanged += t => WindowsAccent.TryApplyMica(hwnd, t == AppTheme.Dark);

        // HotkeyVirtualKey == 0 means the user explicitly cleared the hotkey.
        // Default in FlowSettings is 0x46 ('F'), so new users get Ctrl+Shift+F automatically.
        _hotkeyVk = (uint)App.Settings.HotkeyVirtualKey;
        if (_hotkeyVk > 0)
            RegisterHotKey(hwnd, _hotKeyId, MOD_CONTROL | MOD_SHIFT, _hotkeyVk);

        // Hook WndProc so we can return the right NCHITTEST codes.
        // WindowChrome handles sizing in its own hook but the caption drag can be
        // unreliable when controls in the title bar row consume the mouse.
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private const int WM_NCHITTEST   = 0x0084;
    private const int HTCLIENT       = 1;
    private const int HTCAPTION      = 2;
    private const int HTLEFT         = 10;
    private const int HTRIGHT        = 11;
    private const int HTTOP          = 12;
    private const int HTTOPLEFT      = 13;
    private const int HTTOPRIGHT     = 14;
    private const int HTBOTTOM       = 15;
    private const int HTBOTTOMLEFT   = 16;
    private const int HTBOTTOMRIGHT  = 17;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _hotKeyId)
        {
            if (IsVisible && WindowState != WindowState.Minimized)
                Hide();
            else { Show(); WindowState = WindowState.Normal; Activate(); }
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        // lParam is packed screen-pixel coords (signed 16-bit each half)
        int raw   = lParam.ToInt32();
        int scrX  = (short)(raw & 0xFFFF);
        int scrY  = (short)((raw >> 16) & 0xFFFF);

        // Convert screen pixels → WPF device-independent pixels
        var pt = PointFromScreen(new System.Windows.Point(scrX, scrY));
        double x = pt.X, y = pt.Y;
        double w = ActualWidth, h = ActualHeight;

        // Skip resize handling when maximized — edges are hidden under taskbar/screen edges
        bool canResize = ResizeMode == ResizeMode.CanResize ||
                         ResizeMode == ResizeMode.CanResizeWithGrip;
        double border = canResize && WindowState != WindowState.Maximized ? 6.0 : 0.0;

        // Caption buttons column is ~132 px wide on the right
        const double captionBtnsWidth = 132;

        // Corners (checked first so they take priority over edges)
        if (border > 0)
        {
            if (x <      border && y <      border) { handled = true; return (IntPtr)HTTOPLEFT;     }
            if (x > w - border  && y <      border) { handled = true; return (IntPtr)HTTOPRIGHT;    }
            if (x <      border && y > h - border)  { handled = true; return (IntPtr)HTBOTTOMLEFT;  }
            if (x > w - border  && y > h - border)  { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
            if (x <      border)                    { handled = true; return (IntPtr)HTLEFT;         }
            if (x > w - border)                     { handled = true; return (IntPtr)HTRIGHT;        }
            if (y <      border)                    { handled = true; return (IntPtr)HTTOP;           }
            if (y > h - border)                     { handled = true; return (IntPtr)HTBOTTOM;       }
        }

        // Title bar drag zone — top 40 px, everything left of caption buttons
        if (y < 40 && x < w - captionBtnsWidth)
        {
            handled = true;
            return (IntPtr)HTCAPTION;
        }

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Icon = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Flow;component/Resources/Flow.ico"));
        }
        catch { }

        UnitBitsRadio.IsChecked  = Vm.UseBitsUnit;
        UnitBytesRadio.IsChecked = !Vm.UseBitsUnit;

        // Restore dual-scale toggle state from previous session
        _dualScale = App.Settings.DualScale;
        DualScaleBtn.IsChecked = _dualScale;

        if (App.Settings.UseWindowsAccent) ApplyOsAccentToResources();

        // Apply saved colors to override theme defaults before swatches are built
        Vm.SetDownColor(App.Settings.DownColorHex);
        Vm.SetUpColor(App.Settings.UpColorHex);

        PopulateSwatches(DownGraphSwatches, App.Settings.DownColorHex,     hex => Vm.SetDownColor(hex));
        PopulateSwatches(UpGraphSwatches,   App.Settings.UpColorHex,       hex => Vm.SetUpColor(hex));
        PopulateSwatches(TrayDownSwatches,  App.Settings.TrayDownColorHex, hex => Vm.SetTrayDownColor(hex));
        PopulateSwatches(TrayUpSwatches,    App.Settings.TrayUpColorHex,   hex => Vm.SetTrayUpColor(hex));

        try
        {
            _tray = new TrayIconManager(this);
            Vm.TraySnapshot += (down, up, ping, history, apps) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    // `apps` is already the filtered+sorted AppsView snapshot from the VM,
                    // so it respects KB/s threshold, text filter, Hide-local, etc.
                    var appList = apps.ToList();
                    try { _tray?.Update(down, up, ping, history, appList); } catch { }
                    if (!_chartPaused)
                    {
                        _appHistory = Vm.AppTickSnapshot();
                        DrawMainChart(history);
                    }
                    // Keep ping color consistent across every window
                    UpdatePingColor(ping);
                    _floatingGraph?.UpdateChart(
                        history,
                        ParseColor(App.Settings?.DownColorHex, Color.FromRgb(0x5D, 0xAD, 0xE2)),
                        ParseColor(App.Settings?.UpColorHex,   Color.FromRgb(0xF3, 0x9C, 0x12)),
                        ping,
                        appList);
                });
            };
        }
        catch (Exception ex)
        {
            Vm.StatusText = "Tray icon failed: " + ex.Message;
        }

        // Force a layout pass when the first ETW data arrives so the DataGrid renders rows immediately.
        // CollectionChanged fires INSIDE DeferRefresh so we must dispatch at Background priority,
        // which runs after DeferRefresh ends and the view is fully sorted/filtered.
        System.Collections.Specialized.NotifyCollectionChangedEventHandler? firstData = null;
        firstData = (_, e) =>
        {
            if (e.NewItems?.Count > 0)
            {
                Vm.Apps.CollectionChanged -= firstData;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    AppGrid.Items.Refresh();
                    DashAppGrid.Items.Refresh();
                    _ = Vm.TriggerHourlyRefreshAsync();
                }));
            }
        };
        Vm.Apps.CollectionChanged += firstData;

        // Show saved hotkey binding label (0 = cleared, > 0 = active key)
        if (App.Settings.HotkeyVirtualKey > 0)
        {
            try
            {
                var k = KeyInterop.KeyFromVirtualKey(App.Settings.HotkeyVirtualKey);
                HotkeyLabel.Text = $"Ctrl+Shift+{k}";
            }
            catch { HotkeyLabel.Text = $"Ctrl+Shift+VK{App.Settings.HotkeyVirtualKey:X2}"; }
        }
        else
        {
            HotkeyLabel.Text = "None";
        }

        // Ensure the active ping target is in the list (handles legacy settings)
        if (!App.Settings.PingTargets.Contains(App.Settings.PingTarget, StringComparer.OrdinalIgnoreCase))
        {
            App.Settings.PingTargets.Insert(0, App.Settings.PingTarget);
            SettingsManager.Save(App.Settings);
        }
        PopulatePingTargetCombo();

        // Restore sort column + direction from settings (default: BytesInPerSec DESC)
        RestoreAppGridSort();

        // Open floating graph if it was visible in the last session
        if (App.Settings.ShowFloatingGraph)
            OpenFloatingGraph();

        // Init auto-start toggle (reads Task Scheduler — fast synchronous query)
        InitAutoStartToggle();

        // Wire hardware monitor panel live updates
        InitHardwarePanel();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
        UpdateMaxButtonGlyph();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Signal that we're shutting down — the FloatingGraphWindow.Closed inline handler
        // checks this flag and skips its ShowFloatingGraph=false save so we can write the
        // correct value at the very end of this method.
        _isClosing = true;

        // Capture BEFORE anything closes.
        bool floatWasOpen = _floatingGraph?.IsVisible == true;

        try { UnregisterHotKey(new WindowInteropHelper(this).Handle, _hotKeyId); } catch { }
        try { _floatingGraph?.Close(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { Vm.Dispose(); } catch { }

        // Save ShowFloatingGraph LAST so it wins over any value written by FloatingGraphWindow.Closed
        App.Settings.ShowFloatingGraph = floatWasOpen;
        SettingsManager.Save(App.Settings);
    }

    // ────────── Title-bar drag ──────────

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    // ────────── Main window graph ──────────

    private void DrawMainChart(IReadOnlyList<(long Down, long Up)> history)
    {
        MainChartCanvas.Children.Clear();
        _chartHistory = history;
        if (history.Count < 2) { MainChartPeakLabel.Text = ""; return; }

        double w = MainChartCanvas.ActualWidth;
        double h = MainChartCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Shared or independent peaks depending on dual-scale toggle
        _chartPeakDown = 1; _chartPeakUp = 1;
        foreach (var (d, u) in history)
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

        // ── Horizontal grid lines + Y-axis labels ────────────────────────────────
        for (int li = 1; li <= 3; li++)
        {
            double y = h * li / 4.0;
            MainChartCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = _chartGridBrush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            });
            long labelVal = (long)(sharedPeak * (4 - li) / 4.0);
            var lbl = new TextBlock
            {
                Text = Format.Rate(labelVal), FontSize = 9.5,
                Foreground = _chartLabelBrush, IsHitTestVisible = false
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
                Stroke = _chartTickBrush, StrokeThickness = 1
            });
            var tLbl = new TextBlock
            {
                Text = $"-{sAgo}s", FontSize = 9,
                Foreground = _chartLabelBrush, IsHitTestVisible = false
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
                    StrokeDashArray = new DoubleCollection { 6, 3 }
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
            downPts.Add(new Point(x, h - ((double)history[i].Down / peakD) * h * 0.90));
            upPts.Add(  new Point(x, h - ((double)history[i].Up   / peakU) * h * 0.90));
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
        int n   = _chartHistory.Count;
        int idx = (int)Math.Round(pos.X / w * (n - 1));
        idx = Math.Max(0, Math.Min(n - 1, idx));

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
                Background       = new SolidColorBrush(Color.FromArgb(230, 14, 16, 26)),
                BorderBrush      = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
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
            string timeStr = secsAgo < 0.25 ? "now" : $"-{secsAgo:0.#}s";

            var panel = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };

            // ── Header: total rates + time ───────────────────────────────────
            var downBrush = (Brush)(Application.Current.Resources.Contains("ChartDownBrush")
                ? Application.Current.Resources["ChartDownBrush"]
                : new SolidColorBrush(Color.FromRgb(0x5D, 0xAD, 0xE2)));
            var upBrush   = (Brush)(Application.Current.Resources.Contains("ChartUpBrush")
                ? Application.Current.Resources["ChartUpBrush"]
                : new SolidColorBrush(Color.FromRgb(0xF3, 0x9C, 0x12)));
            var dimBrush  = _chartCrosshairDimBrush;

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = "↓ ", Foreground = downBrush, FontWeight = FontWeights.SemiBold, FontSize = 11 });
            header.Children.Add(new TextBlock { Text = Format.Bytes(down / 2), Foreground = new SolidColorBrush(Colors.White), FontWeight = FontWeights.SemiBold, FontSize = 11, MinWidth = 72 });
            header.Children.Add(new TextBlock { Text = "  ↑ ", Foreground = upBrush, FontWeight = FontWeights.SemiBold, FontSize = 11 });
            header.Children.Add(new TextBlock { Text = Format.Bytes(up / 2), Foreground = new SolidColorBrush(Colors.White), FontWeight = FontWeights.SemiBold, FontSize = 11, MinWidth = 72 });
            header.Children.Add(new TextBlock { Text = $"  {timeStr}", Foreground = dimBrush, FontSize = 10 });
            panel.Children.Add(header);

            // ── Per-app rows ─────────────────────────────────────────────────
            if (idx < _appHistory.Count && _appHistory[idx] is { Length: > 0 } apps)
            {
                panel.Children.Add(new Border
                {
                    Height = 1, Margin = new Thickness(-10, 6, -10, 5),
                    Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
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
                        Foreground = new SolidColorBrush(Color.FromArgb(210, 220, 225, 240)),
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
                    Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
                });

                var accentBrush = (Brush)(Application.Current.Resources.Contains("AccentBrush")
                    ? Application.Current.Resources["AccentBrush"]
                    : new SolidColorBrush(Color.FromRgb(0x5D, 0xAD, 0xE2)));
                var gpuBrush = (Brush)(Application.Current.Resources.Contains("ChartUpBrush")
                    ? Application.Current.Resources["ChartUpBrush"]
                    : new SolidColorBrush(Color.FromRgb(0xF3, 0x9C, 0x12)));
                var ramBrush = (Brush)(Application.Current.Resources.Contains("SuccessBrush")
                    ? Application.Current.Resources["SuccessBrush"]
                    : new SolidColorBrush(Color.FromRgb(0x3D, 0xBA, 0x6F)));

                var hwRow = new StackPanel { Orientation = Orientation.Horizontal };
                if (hw.CpuLoad > 0)
                    hwRow.Children.Add(new TextBlock
                    {
                        Text = $"CPU {hw.CpuLoad:0}%", Foreground = accentBrush,
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
        _crosshairLine    = null;
        _crosshairPanel   = null;
        _lastCrosshairIdx = -1;
    }

    // ── Preset / dual-scale / clear filter handlers ────────────────────────────

    private void OnPresetAll(object sender, RoutedEventArgs e)  => Vm.HideBelowKBps = 0;
    private void OnPreset10(object sender, RoutedEventArgs e)   => Vm.HideBelowKBps = 10;
    private void OnPreset50(object sender, RoutedEventArgs e)   => Vm.HideBelowKBps = 50;
    private void OnClearFilter(object sender, RoutedEventArgs e) => Vm.FilterText = "";

    // ── Chart pause on click ────────────────────────────────────────────────────

    private void OnChartClick(object sender, MouseButtonEventArgs e)
    {
        _chartPaused = !_chartPaused;
        PauseBanner.Visibility = _chartPaused ? Visibility.Visible : Visibility.Collapsed;
        if (!_chartPaused) DrawMainChart(_chartHistory); // immediate redraw when resuming
    }

    // ── Clear hotkey ──────────────────────────────────────────────────────────

    private void OnClearHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey) CancelHotkeyCapture();
        try { UnregisterHotKey(new WindowInteropHelper(this).Handle, _hotKeyId); } catch { }
        _hotkeyVk = 0;
        App.Settings.HotkeyVirtualKey = 0;
        SettingsManager.Save(App.Settings);
        HotkeyLabel.Text = "None";
        Vm.StatusText = "Global hotkey cleared.";
    }

    // ── Ping target management ────────────────────────────────────────────────

    private void PopulatePingTargetCombo()
    {
        PingTargetCombo.SelectionChanged -= OnPingTargetSelectionChanged;  // prevent re-entry
        PingTargetCombo.Items.Clear();
        foreach (var t in App.Settings.PingTargets)
            PingTargetCombo.Items.Add(t);
        var idx = App.Settings.PingTargets.IndexOf(App.Settings.PingTarget);
        PingTargetCombo.SelectedIndex = idx >= 0 ? idx : (App.Settings.PingTargets.Count > 0 ? 0 : -1);
        PingTargetCombo.SelectionChanged += OnPingTargetSelectionChanged;
        // Sync label textbox to whichever target is now selected
        SyncPingLabelBox();
    }

    private void SyncPingLabelBox()
    {
        if (PingTargetCombo.SelectedItem is string target &&
            App.Settings.PingTargetLabels.TryGetValue(target, out var lbl))
            PingLabelBox.Text = lbl;
        else
            PingLabelBox.Text = "";
    }

    private void OnPingTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Update label textbox to show the saved label for the newly selected target
        SyncPingLabelBox();
    }

    private void OnSetPingTarget(object sender, RoutedEventArgs e)
    {
        if (PingTargetCombo.SelectedItem is not string target) return;
        Vm.ChangePingTarget(target);
        Vm.StatusText = $"Ping target changed to {target}.";
    }

    private void OnAddPingTarget(object sender, RoutedEventArgs e)
    {
        var target = NewPingTargetBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target)) return;
        if (!App.Settings.PingTargets.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            App.Settings.PingTargets.Add(target);
            SettingsManager.Save(App.Settings);
        }
        // Also switch to the new target immediately
        Vm.ChangePingTarget(target);
        NewPingTargetBox.Text = "";
        PopulatePingTargetCombo();
        Vm.StatusText = $"Added and switched to {target}.";
    }

    private void OnRemovePingTarget(object sender, RoutedEventArgs e)
    {
        if (PingTargetCombo.SelectedItem is not string target) return;
        App.Settings.PingTargets.Remove(target);
        App.Settings.PingTargetLabels.Remove(target);
        // If we removed the active target, fall back to the first available
        if (string.Equals(App.Settings.PingTarget, target, StringComparison.OrdinalIgnoreCase)
            && App.Settings.PingTargets.Count > 0)
        {
            Vm.ChangePingTarget(App.Settings.PingTargets[0]);
        }
        SettingsManager.Save(App.Settings);
        PopulatePingTargetCombo();
        Vm.StatusText = $"Removed ping target {target}.";
    }

    private void OnSavePingLabel(object sender, RoutedEventArgs e)
    {
        if (PingTargetCombo.SelectedItem is not string target) return;
        var label = PingLabelBox.Text.Trim();
        if (string.IsNullOrEmpty(label))
            App.Settings.PingTargetLabels.Remove(target);
        else
            App.Settings.PingTargetLabels[target] = label;
        SettingsManager.Save(App.Settings);
        // Push updated label to sidebar
        Vm.NotifyPingDisplayLabel();
        Vm.StatusText = string.IsNullOrEmpty(label)
            ? $"Label cleared for {target}."
            : $"Label \"{label}\" saved for {target}.";
    }

    private void OnDualScaleClick(object sender, RoutedEventArgs e)
    {
        _dualScale = DualScaleBtn.IsChecked == true;
        App.Settings.DualScale = _dualScale;
        SettingsManager.Save(App.Settings);
        DrawMainChart(_chartHistory);
    }

    // ── Global hotkey rebinding ────────────────────────────────────────────────

    private void OnChangeHotkeyClick(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyCapturing.Visibility = Visibility.Visible;
        ChangeHotkeyBtn.IsEnabled = false;
        Focus(); // window-level focus lets OnPreviewKeyDown intercept the next key
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturingHotkey)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { CancelHotkeyCapture(); return; }
            // Skip bare modifier keys
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                     or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;

            var hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, _hotKeyId);
            if (RegisterHotKey(hwnd, _hotKeyId, MOD_CONTROL | MOD_SHIFT, vk))
            {
                _hotkeyVk = vk;
                App.Settings.HotkeyVirtualKey = (int)vk;
                SettingsManager.Save(App.Settings);
                HotkeyLabel.Text = $"Ctrl+Shift+{key}";
                Vm.StatusText = $"Hotkey changed to Ctrl+Shift+{key}.";
            }
            else
            {
                RegisterHotKey(hwnd, _hotKeyId, MOD_CONTROL | MOD_SHIFT, _hotkeyVk);
                Vm.StatusText = "Couldn't bind that key — try a different one.";
            }
            CancelHotkeyCapture();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void CancelHotkeyCapture()
    {
        _capturingHotkey = false;
        HotkeyCapturing.Visibility = Visibility.Collapsed;
        ChangeHotkeyBtn.IsEnabled = true;
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

    // ────────── Caption buttons ──────────

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaxRestore(object sender, RoutedEventArgs e)
        => WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCloseEnter(object sender, MouseEventArgs e)
        => CloseBtn.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private void OnCloseLeave(object sender, MouseEventArgs e) => CloseBtn.ClearValue(BackgroundProperty);

    private void UpdateMaxButtonGlyph()
    {
        if (MaxBtn is null) return;
        //  = Chrome Restore,  = Chrome Maximize (Segoe Fluent / MDL2)
        MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    // ────────── Settings tabs ──────────

    private void OnSettingsTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        SettingsTabGeneral.Visibility      = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabNetworkGroup.Visibility = tag == "network" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabTestGroup.Visibility    = tag == "test"    ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "network")
        {
            ConnectionUploadBox.Text   = App.Settings.ConnectionUploadMbps.ToString();
            ConnectionDownloadBox.Text = App.Settings.ConnectionDownloadMbps.ToString();
            ProfilesList.ItemsSource   = App.Settings.LimitProfiles;
        }
    }

    // ── Connection speed save ─────────────────────────────────────────────────

    private void OnSaveConnectionSpeed(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(ConnectionUploadBox.Text.Trim(),   out int up)   && up   >= 0)
            App.Settings.ConnectionUploadMbps   = up;
        if (int.TryParse(ConnectionDownloadBox.Text.Trim(), out int down) && down >= 0)
            App.Settings.ConnectionDownloadMbps = down;
        SettingsManager.Save(App.Settings);
    }

    // ── Limit profiles ────────────────────────────────────────────────────────

    private void OnAddProfile(object sender, RoutedEventArgs e)
    {
        string name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!int.TryParse(ProfileUpBox.Text,   out int up)   || up   < 0) up   = 0;
        if (!int.TryParse(ProfileDownBox.Text, out int down) || down < 0) down = 0;
        App.Settings.LimitProfiles.Add(new Services.LimitProfile { Name = name, UploadKbps = up, DownloadKbps = down });
        SettingsManager.Save(App.Settings);
        ProfileNameBox.Text  = "";
        ProfileUpBox.Text    = "0";
        ProfileDownBox.Text  = "0";
        RefreshProfilesList();
    }

    private void OnRemoveProfile(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is string name)
        {
            App.Settings.LimitProfiles.RemoveAll(p => p.Name == name);
            SettingsManager.Save(App.Settings);
            RefreshProfilesList();
        }
    }

    private void RefreshProfilesList()
    {
        ProfilesList.ItemsSource = null;
        ProfilesList.ItemsSource = App.Settings.LimitProfiles;
    }

    // ────────── Sidebar nav ──────────
    // Tags: 0=Dashboard, 1=Network, 2=Hardware, 3=History, 4=Settings

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!int.TryParse(tag, out int idx)) return;
        NavigateToPanel(idx);
    }

    // "View all →" on dashboard routes to Network tab
    private void OnDashViewAllClick(object sender, RoutedEventArgs e) => NavigateToPanel(1);

    // ── Navigation helpers ────────────────────────────────────────────────────

    private static readonly Duration _fadeInDuration  = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration _fadeOutDuration = new(TimeSpan.FromMilliseconds(110));

    /// <summary>
    /// Fade + slide-up panel entrance.  The panel rises 14 px while fading in,
    /// giving the UI a spatial "new content arriving from below" feel.
    /// Respects the Windows "Show animations" accessibility setting.
    /// </summary>
    private static void FadeIn(UIElement el)
    {
        el.Visibility = Visibility.Visible;

        if (!SystemParameters.ClientAreaAnimation)
        {
            el.Opacity = 1;
            el.RenderTransform = new TranslateTransform(0, 0);
            return;
        }

        el.Opacity = 0;

        // Per-instance TranslateTransform so panels don't share a reference.
        var translate = new TranslateTransform(0, 14);
        el.RenderTransform = translate;
        el.RenderTransformOrigin = new Point(0.5, 0.5);

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation(0, 1, _fadeInDuration) { EasingFunction = easing };
        el.BeginAnimation(UIElement.OpacityProperty, fade);

        var slide = new DoubleAnimation(14, 0, _fadeInDuration) { EasingFunction = easing };
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>
    /// Quick fade-out (110 ms) with a subtle downward drift — the reverse
    /// of FadeIn so outgoing content appears to "sink" as it leaves.
    /// </summary>
    private static void FadeOut(UIElement el)
    {
        if (!SystemParameters.ClientAreaAnimation) { el.Visibility = Visibility.Collapsed; return; }

        var translate = el.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 0);
        el.RenderTransform = translate;

        var fade = new DoubleAnimation(el.Opacity, 0, _fadeOutDuration);
        fade.Completed += (_, _) => el.Visibility = Visibility.Collapsed;
        el.BeginAnimation(UIElement.OpacityProperty, fade);

        var slide = new DoubleAnimation(0, 6, _fadeOutDuration);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>Switch to a top-level panel by index (0=Dashboard,1=Network,2=Hardware,3=History,4=Settings).</summary>
    private void NavigateToPanel(int idx)
    {
        NavDashboard.IsChecked = idx == 0;
        NavProcesses.IsChecked = idx == 1;
        NavHardware.IsChecked  = idx == 2;
        NavHistory.IsChecked   = idx == 3;
        NavSettings.IsChecked  = idx == 4;

        UIElement[] panels = [DashboardPanel, ProcPanel, HardwareMonitorPanel, HistoryPanel, SettingsPanel];
        for (int i = 0; i < panels.Length; i++)
        {
            if (i == idx)
            {
                if (panels[i].Visibility != Visibility.Visible) FadeIn(panels[i]);
            }
            else
            {
                if (panels[i].Visibility == Visibility.Visible) FadeOut(panels[i]);
            }
        }
    }

    /// <summary>Switch to a Settings sub-tab by tag name, mapping old fine-grained tags to the new 3-tab structure.</summary>
    private void NavigateToSettingsTab(string tabTag)
    {
        string parent = tabTag switch
        {
            "appearance" or "shortcuts" or "system"               => "general",
            "monitoring" or "connection" or "profiles" or "network" => "network",
            "ping"       or "speedtest"                           => "test",
            _                                                     => tabTag
        };
        SettingsTabBtnGeneral.IsChecked     = parent == "general";
        SettingsTabBtnNetwork.IsChecked     = parent == "network";
        SettingsTabBtnTest.IsChecked        = parent == "test";
        SettingsTabGeneral.Visibility      = parent == "general" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabNetworkGroup.Visibility = parent == "network" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabTestGroup.Visibility    = parent == "test"    ? Visibility.Visible : Visibility.Collapsed;
        if (parent == "network")
        {
            ConnectionUploadBox.Text   = App.Settings.ConnectionUploadMbps.ToString();
            ConnectionDownloadBox.Text = App.Settings.ConnectionDownloadMbps.ToString();
            ProfilesList.ItemsSource   = App.Settings.LimitProfiles;
        }
    }

    // ── Dashboard card click handlers ────────────────────────────────────────

    private void OnDashBandwidthClick(object sender, MouseButtonEventArgs e) => NavigateToPanel(1);
    private void OnDashHardwareClick(object sender, MouseButtonEventArgs e)  => NavigateToPanel(2);
    private void OnDashPingClick(object sender, MouseButtonEventArgs e)
    {
        NavigateToPanel(4);
        NavigateToSettingsTab("ping");
    }

    // ── Sidebar ping card click → Settings → Ping ────────────────────────────

    private void OnSidebarPingClick(object sender, MouseButtonEventArgs e)
    {
        NavigateToPanel(4);
        NavigateToSettingsTab("ping");
    }

    // ────────── Hardware monitor panel ──────────

    private void InitHardwarePanel()
    {
        if (HardwareMonitor.Instance is not { } hw) return;

        // Feed the latest reading immediately (sensor might already have data)
        if (hw.Latest != HardwareSnapshot.Empty)
        {
            UpdateHardwarePanel(hw.Latest);
            UpdateDashboardHardware(hw.Latest);
        }

        // Subscribe for live updates
        hw.Sampled += snap => Dispatcher.InvokeAsync(() =>
        {
            UpdateHardwarePanel(snap);
            UpdateDashboardHardware(snap);
        });
    }

    // ── Dashboard hardware mini-cards ─────────────────────────────────────────
    private void UpdateDashboardHardware(HardwareSnapshot snap)
    {
        // CPU
        if (!string.IsNullOrEmpty(snap.CpuName))
            DashCpuName.Text = snap.CpuName
                .Replace("Intel(R) Core(TM) ", "")
                .Replace("Intel Core ", "")
                .Replace(" Processor", "");
        DashCpuPct.Text  = snap.CpuTemp > 0 ? $"{snap.CpuTemp:0}" : "—";
        DashCpuBar.Value = snap.CpuLoad;
        DashCpuTemp.Text = snap.CpuLoad > 0 ? $"{snap.CpuLoad:0}%" : "";
        DashCpuFreq.Text = snap.CpuFreqMHz > 0 ? $"{snap.CpuFreqMHz / 1000f:0.0} GHz" : "";

        // GPU
        DashGpuPct.Text  = snap.GpuTemp > 0 ? $"{snap.GpuTemp:0}" : "—";
        DashGpuBar.Value = snap.GpuLoad;
        DashGpuTemp.Text = snap.GpuTemp > 0 ? $"{snap.GpuTemp:0}°C" : "";
        DashGpuClock.Text = snap.GpuCoreMHz > 0 ? $"{snap.GpuCoreMHz:0} MHz" : "";
        if (!string.IsNullOrEmpty(snap.GpuName))
            DashGpuName.Text = snap.GpuName.Replace("NVIDIA GeForce ", "").Replace("AMD Radeon ", "");

        // RAM
        if (snap.RamTotalGb > 0)
        {
            DashRamPct.Text   = $"{snap.RamPct:0}";
            DashRamBar.Maximum = snap.RamTotalGb;
            DashRamBar.Value   = snap.RamUsedGb;
            DashRamUsed.Text   = $"{snap.RamUsedGb:0.0} GB";
            DashRamTotal.Text  = $"of {snap.RamTotalGb:0.0} GB";
        }
    }

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
            Color c = pingMs < 40  ? Color.FromRgb(0x4A, 0xDE, 0x80)
                    : pingMs < 80  ? Color.FromRgb(0xFB, 0xD2, 0x24)
                                   : Color.FromRgb(0xF8, 0x71, 0x71);
            brush = new SolidColorBrush(c);
        }
        SidebarPingText.Foreground = brush;
        SidebarPingUnit.Foreground = brush;
        DashPingText.Foreground    = brush;
        DashPingUnit.Foreground    = brush;

        // Record sample and redraw the ping sparkline on the Dashboard card
        if (pingMs >= 0)
        {
            _pingBuf[_pingBufHead] = pingMs;
            _pingBufHead = (_pingBufHead + 1) % PingHistLen;
            if (_pingBufCount < PingHistLen) _pingBufCount++;
            DrawDashPingSparkline(brush);
        }
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

    private void UpdateHardwarePanel(HardwareSnapshot snap)
    {
        bool hasAnyData = snap.CpuLoad > 0 || snap.GpuLoad > 0 || snap.RamTotalGb > 0;
        HwNoDataNotice.Visibility = hasAnyData ? Visibility.Collapsed : Visibility.Visible;

        // ── CPU ──────────────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(snap.CpuName))
            CpuCardName.Text = snap.CpuName;
        CpuUsageBar.Value = snap.CpuLoad;
        CpuPct.Text       = snap.CpuTemp > 0 ? $"{snap.CpuTemp:0}" : "—";   // big = temperature
        CpuLoadLabel.Text = $"{snap.CpuLoad:0}";
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
        GpuPct.Text       = snap.GpuTemp > 0 ? $"{snap.GpuTemp:0}" : "—";   // big = temperature
        GpuLoadLabel.Text = $"{snap.GpuLoad:0}";
        if (!string.IsNullOrEmpty(snap.GpuName))
            GpuCardName.Text = snap.GpuName;

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

        // ── Temperature / usage sparklines ───────────────────────────────────
        _cpuTempBuf[_tempBufHead] = snap.CpuTemp  > 0 ? (float)snap.CpuTemp : 0f;
        _gpuTempBuf[_tempBufHead] = snap.GpuTemp  > 0 ? (float)snap.GpuTemp : 0f;
        _ramPctBuf[_tempBufHead]  = snap.RamTotalGb > 0 ? (float)snap.RamPct : 0f;
        _tempBufHead = (_tempBufHead + 1) % TempHistLen;
        if (_tempBufCount < TempHistLen) _tempBufCount++;

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
                      Color.FromRgb(0x5D, 0xAD, 0xE2), "°");   // AccentBrush blue
        DrawTempChart(GpuTempChart,  _gpuTempBuf, _tempBufHead, _tempBufCount,
                      Color.FromRgb(0xF3, 0x9C, 0x12), "°");   // ChartUpBrush orange
        DrawTempChart(RamUsageChart, _ramPctBuf,  _tempBufHead, _tempBufCount,
                      Color.FromRgb(0x3D, 0xBA, 0x6F), "%");   // SuccessBrush green

        // Pulse the refresh dot so users can see data is live
        HwRefreshDot.Opacity = 1.0;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.35,
            new Duration(TimeSpan.FromMilliseconds(600)));
        HwRefreshDot.BeginAnimation(OpacityProperty, fade);
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
        var axisColor = Color.FromArgb(110, 200, 210, 220);
        string maxLabel = $"{dataMax:0}{unit}";
        string minLabel = $"{dataMin:0}{unit}";

        var maxTb = new TextBlock
        {
            Text = maxLabel, FontSize = 8,
            Foreground = new SolidColorBrush(axisColor),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(maxTb, 2); Canvas.SetTop(maxTb, 1);
        canvas.Children.Add(maxTb);

        var minTb = new TextBlock
        {
            Text = minLabel, FontSize = 8,
            Foreground = new SolidColorBrush(axisColor),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(minTb, 2); Canvas.SetTop(minTb, h - 11);
        canvas.Children.Add(minTb);
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
            StrokeDashArray = new DoubleCollection { 2, 2 }
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

    // ────────── App grid column sorting (keeps SortPriority as locked primary key) ──────────

    private void OnAppGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;   // suppress default behaviour

        // Toggle direction on the clicked column
        var dir = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        // Clear all column sort glyphs, then set only the clicked one
        foreach (var col in AppGrid.Columns) col.SortDirection = null;
        e.Column.SortDirection = dir;

        // Re-apply: SortPriority (locked) + user column
        var lv = (ListCollectionView)Vm.AppsView;
        using (lv.DeferRefresh())
        {
            Vm.AppsView.SortDescriptions.Clear();
            Vm.AppsView.SortDescriptions.Add(new SortDescription(
                nameof(AppRowViewModel.SortPriority), ListSortDirection.Ascending));
            if (!string.IsNullOrEmpty(e.Column.SortMemberPath))
                Vm.AppsView.SortDescriptions.Add(new SortDescription(e.Column.SortMemberPath, dir));
        }

        App.Settings.AppSortMemberPath = e.Column.SortMemberPath ?? "";
        App.Settings.AppSortDescending = dir == ListSortDirection.Descending;
        SettingsManager.Save(App.Settings);
    }

    private void RestoreAppGridSort()
    {
        var path = App.Settings.AppSortMemberPath;
        var dir  = App.Settings.AppSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;

        foreach (var col in AppGrid.Columns)
            col.SortDirection = col.SortMemberPath == path ? dir : (ListSortDirection?)null;

        if (!string.IsNullOrEmpty(path))
        {
            var lv = (ListCollectionView)Vm.AppsView;
            using (lv.DeferRefresh())
            {
                Vm.AppsView.SortDescriptions.Clear();
                Vm.AppsView.SortDescriptions.Add(new SortDescription(
                    nameof(AppRowViewModel.SortPriority), ListSortDirection.Ascending));
                Vm.AppsView.SortDescriptions.Add(new SortDescription(path, dir));
            }
        }
    }

    // ────────── Context menu / processes ──────────

    private AppRowViewModel? Selected => AppGrid.SelectedItem as AppRowViewModel;

    private void OnPinClick(object sender, RoutedEventArgs e)
    { if (Selected is not null) Vm.PinAppCommand.Execute(Selected); }
    private void OnUnpinClick(object sender, RoutedEventArgs e)
    { if (Selected is not null) Vm.UnpinAppCommand.Execute(Selected); }

    private void OnBlockClick(object sender, RoutedEventArgs e)
    { if (Selected is not null) _ = Vm.BlockCommand.ExecuteAsync(Selected); }
    private void OnUnblockClick(object sender, RoutedEventArgs e)
    { if (Selected is not null) _ = Vm.UnblockCommand.ExecuteAsync(Selected); }
    private void OnSetLimitClick(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        var dlg = new SetLimitWindow(Selected.Name, Selected.UploadLimitKbps, Selected.DownloadLimitKbps,
                                     App.Settings.ConnectionUploadMbps, App.Settings.ConnectionDownloadMbps,
                                     Selected.HasUdpConnections) { Owner = this };
        if (dlg.ShowDialog() == true)
            _ = Vm.ApplyLimitsCommand.ExecuteAsync((Selected, dlg.UploadKbps, dlg.DownloadKbps));
    }

    private void OnClearLimitsClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not null)
            _ = Vm.ApplyLimitsCommand.ExecuteAsync((Selected, 0, 0));
    }
    private void OnMarkGameClick(object sender, RoutedEventArgs e)
    { if (Selected is not null) _ = Vm.MarkAsGameCommand.ExecuteAsync(Selected); }
    private void OnUnmarkGameClick(object sender, RoutedEventArgs e)
    { if (Selected is not null) _ = Vm.UnmarkGameCommand.ExecuteAsync(Selected); }
    private void OnShowPidsClick(object sender, RoutedEventArgs e) => ShowPidsFor(Selected);
    private void OnAppDoubleClick(object sender, MouseButtonEventArgs e) => ShowAppDetail(Selected);

    private void ShowAppDetail(AppRowViewModel? app)
    {
        if (app is null) return;
        var w = new AppDetailWindow(app) { Owner = this };
        w.Show();
    }

    private void ShowPidsFor(AppRowViewModel? app)
    {
        if (app is null) return;
        if (app.Processes.Count == 0)
        {
            MessageBox.Show("No active processes for this app.", "Flow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"{app.Name}  —  {app.Processes.Count} process(es):");
        sb.AppendLine();
        foreach (var p in app.Processes.OrderByDescending(p => p.BytesInPerSec + p.BytesOutPerSec))
        {
            sb.AppendLine($"  PID {p.Pid}");
            sb.AppendLine($"    ↓ {p.DownRate}   ↑ {p.UpRate}");
            sb.AppendLine($"    Connections: {p.ConnectionCount}");
            if (!string.IsNullOrEmpty(p.ImagePath))
                sb.AppendLine($"    {p.ImagePath}");
            sb.AppendLine();
        }
        MessageBox.Show(sb.ToString(), $"Processes for {app.Name}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ────────── Settings ──────────

    private void OnThemeToggleClick(object sender, RoutedEventArgs e) => Vm.ToggleThemeCommand.Execute(null);
    private void OnUnitBytesClick(object sender, RoutedEventArgs e) => Vm.UseBitsUnit = false;
    private void OnUnitBitsClick(object sender, RoutedEventArgs e)  => Vm.UseBitsUnit = true;

    private void PopulateSwatches(ItemsControl host, string currentHex, Action<string> onPick)
    {
        host.Items.Clear();
        foreach (var hex in _palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var border = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
                Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(string.Equals(hex, currentHex, StringComparison.OrdinalIgnoreCase) ? 3 : 1),
                BorderBrush = new SolidColorBrush(Colors.White),
                Cursor = Cursors.Hand,
                Tag = hex,
            };
            border.MouseLeftButtonDown += (_, _) =>
            {
                onPick(hex);
                foreach (var child in host.Items.OfType<Border>())
                    child.BorderThickness = new Thickness(string.Equals((string)child.Tag!, hex, StringComparison.OrdinalIgnoreCase) ? 3 : 1);
            };
            host.Items.Add(border);
        }
    }

    private void ApplyOsAccentToResources()
    {
        var c = WindowsAccent.Get();
        var brush = new SolidColorBrush(c);
        if (brush.CanFreeze) brush.Freeze();
        Application.Current.Resources["AccentBrush"] = brush;
    }

    // ────────── Gaming Mode ──────────

    // The command is already bound; this Click handler runs AFTER the command so
    // the button tooltip / ToolTip is updated without extra VM wiring.
    private void OnGamingModeBtnClick(object sender, RoutedEventArgs e)
        => _ = Vm.ToggleGamingModeCommand.ExecuteAsync(null);

    // ────────── Global cap ──────────

    private void OnClearGlobalCap(object sender, RoutedEventArgs e)
    {
        Vm.GlobalUploadCapKBs = 0;
        _ = Vm.ApplyGlobalUploadCapCommand.ExecuteAsync(null);
    }

    private void OnOpenNetworkConnections(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ncpa.cpl")
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    // ────────── Auto-start ──────────

    private void InitAutoStartToggle()
    {
        bool enabled = AutoStartManager.IsEnabled();
        AutoStartToggle.IsChecked = enabled;
        AutoStartStatusLabel.Text = enabled
            ? "Enabled — Flow will launch at Windows login"
            : "Disabled — Flow won't start automatically";
    }

    private async void OnAutoStartToggleClick(object sender, RoutedEventArgs e)
    {
        bool enable = AutoStartToggle.IsChecked == true;
        AutoStartToggle.IsEnabled = false;
        AutoStartStatusLabel.Text = enable ? "Enabling…" : "Disabling…";

        bool ok = await AutoStartManager.SetAsync(enable);

        AutoStartToggle.IsEnabled = true;
        if (ok)
        {
            AutoStartStatusLabel.Text = enable
                ? "Enabled — Flow will launch at Windows login"
                : "Disabled — Flow won't start automatically";
            Vm.StatusText = enable ? "Auto-start enabled." : "Auto-start disabled.";
        }
        else
        {
            // Revert the toggle — the operation failed (likely needs admin, or schtasks error)
            AutoStartToggle.IsChecked = !enable;
            AutoStartStatusLabel.Text = "Failed — run Flow as administrator to change this setting";
            Vm.StatusText = "Auto-start change failed. Try running Flow as administrator.";
        }
    }

    // ────────── Column auto-size on separator double-click (Excel / Sheets style) ──────────

    /// <summary>
    /// Fires on PreviewMouseDoubleClick for both DataGrids.  When the user double-clicks the
    /// right (or left) gripper of a column header the column is sized to fit its widest visible
    /// cell — the same behaviour as double-clicking a column separator in Excel or Google Sheets.
    ///
    /// Implementation: set Width=Auto so WPF measures the content, force a layout pass to get
    /// the measured ActualWidth, then lock it back to a fixed pixel value so the column doesn't
    /// keep auto-resizing as data changes.
    /// </summary>
    private void OnColumnHeaderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        // Only react when the pointer is inside a DataGridColumnHeader
        var header = FindVisualAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column == null) return;

        // Grippers in the Controls.xaml template are Width="8".
        // Right gripper (pos.X near right edge) → resize this column.
        // Left gripper  (pos.X near left edge)  → resize the preceding column.
        var pos = e.GetPosition(header);
        DataGridColumn? target = null;

        if (pos.X >= header.ActualWidth - 8)
        {
            target = header.Column;
        }
        else if (pos.X <= 8)
        {
            var idx = grid.Columns.IndexOf(header.Column);
            if (idx > 0) target = grid.Columns[idx - 1];
        }

        if (target == null || !target.CanUserResize) return;

        // Two-step: Auto → measure → lock to pixels.
        target.Width = DataGridLength.Auto;
        grid.UpdateLayout();
        target.Width = new DataGridLength(target.ActualWidth);
        e.Handled = true;   // prevent OnAppDoubleClick from also firing
    }

    /// <summary>Walks the visual tree upward looking for an ancestor of type <typeparamref name="T"/>.</summary>
    private static T? FindVisualAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T result) return result;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // ────────── Floating graph ──────────

    private void OnFloatGraphToggle(object sender, RoutedEventArgs e)
    {
        if (_floatingGraph?.IsVisible == true)
        {
            _floatingGraph.Hide();
            Vm.ShowFloatingGraph = false;
        }
        else
        {
            OpenFloatingGraph();
            Vm.ShowFloatingGraph = true;
        }
    }

    private void OpenFloatingGraph()
    {
        if (_floatingGraph is null)
        {
            _floatingGraph = new FloatingGraphWindow();
            _floatingGraph.Closed += (_, _) =>
            {
                _floatingGraph = null;
                // Only save "closed" when the USER closed the float graph while app is running.
                // During app shutdown (_isClosing=true), OnClosing handles the final save.
                if (!_isClosing)
                    Vm.ShowFloatingGraph = false;
            };
        }
        _floatingGraph.Show();
        Vm.ShowFloatingGraph = true;
    }
}
