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

public partial class MainWindow : Window
{
    private TrayIconManager?    _tray;
    private FloatingGraphWindow? _floatingGraph;
    private ToolsViewModel?      _toolsVm;

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

    // Chart scroll / drag state
    private int    _chartScrollOffset;         // 0 = live; > 0 = scrolled back in time (samples)
    private bool   _chartDragging;
    private double _chartDragStartX;
    private int    _chartDragStartOffset;
    private const int kChartWindow = 60;       // visible samples (30 s at 0.5 s/sample)

    // ── Temperature / usage ring buffers for hardware card sparklines ─────────
    // 60 samples × ~2 s per sample ≈ 2 minutes of history.
    // Ring buffer: _tempBufHead points to the NEXT write slot (oldest data is at head).
    private const int TempHistLen = 60;
    private readonly float[] _cpuTempBuf = new float[TempHistLen];
    private readonly float[] _gpuTempBuf = new float[TempHistLen];
    private readonly float[] _ramPctBuf  = new float[TempHistLen];
    private int _tempBufHead;    // next write index
    private int _tempBufCount;   // how many entries are valid (ramps up to TempHistLen)

    // Y-axis scale of the overlaid CPU/GPU timeline — cached so the crosshair maps Y identically.
    private float _ovSMin, _ovSMax = 1;

    // ── Ping sparkline ring buffer (Dashboard ping card) ─────────────────────
    private const int PingHistLen = 60;
    private readonly int[] _pingBuf = new int[PingHistLen];
    private int _pingBufHead;
    private int _pingBufCount;

    // Chart chrome (grid / labels / ticks / crosshair) resolves from the active theme's
    // resources on each redraw so it adapts to dark/light. These are cheap dictionary
    // lookups and the chart only redraws ~2×/s. Fallbacks match the original dark values.
    private static Brush ChartChrome(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? FrozenBrush(fallback);

    // Cached dash arrays — allocated once, reused across every chart redraw
    private static readonly DoubleCollection _dashFour  = Frozen(new DoubleCollection { 4, 4 });
    private static readonly DoubleCollection _dashSix   = Frozen(new DoubleCollection { 6, 3 });
    private static readonly DoubleCollection _dashTwo   = Frozen(new DoubleCollection { 2, 2 });

    private static DoubleCollection Frozen(DoubleCollection dc) { dc.Freeze(); return dc; }

    private static SolidColorBrush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // Color-dependent chart brushes — rebuilt only when user changes color in settings
    private SolidColorBrush? _chartDownStroke, _chartDownFill, _chartUpStroke, _chartUpFill;
    private Color _cachedChartDown, _cachedChartUp;

    // Shutdown guard — prevents the FloatingGraphWindow.Closed inline handler from saving
    // ShowFloatingGraph=false while OnClosing is already handling the final correct save.
    private bool _isClosing;
    // Set by the tray "Quit" action so OnClosing knows to skip MinimizeOnClose.
    private bool _forceClose;

    // Global hotkey (Ctrl+Shift+<key>)
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Win32 window-style helpers — needed to keep WS_THICKFRAME|WS_CAPTION
    // intact (some WindowChrome configs strip them) so FancyZones can snap
    [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    private const int GWL_STYLE    = -16;
    private const int WS_CAPTION   = 0x00C00000;
    private const int WS_THICKFRAME= 0x00040000;

    // UIPI message filter — allows lower-integrity processes (ShareX, FancyZones)
    // to send window-management messages to this elevated window
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(
        IntPtr hWnd, uint msg, uint action, IntPtr pChangeFilterStruct);
    private const uint MSGFLT_ALLOW  = 1;
    private const uint WM_LBUTTONDOWN   = 0x0201;
    private const uint WM_LBUTTONUP     = 0x0202;
    private const uint WM_MOUSEMOVE     = 0x0200;
    private const uint WM_MOVING        = 0x0216;
    private const uint WM_ENTERSIZEMOVE = 0x0231;
    private const uint WM_EXITSIZEMOVE  = 0x0232;
    private const uint WM_SIZING        = 0x0214;
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

    // Temperature-band palette: cool blues/cyans → greens → warm yellows/oranges → reds
    // (includes the three band defaults so the active swatch always highlights).
    private static readonly string[] _tempPalette =
    {
        "#36BFFA", "#3B82F6", "#06B6D4", "#22D3EE", "#10B981", "#A3E635",
        "#FFD60A", "#FACC15", "#F59E0B", "#FB923C", "#FF3B30", "#EF4444",
    };

    // Domain-accent palette: full cool→warm spectrum so Network and Hardware can each pick
    // any color (not just cool / warm). Includes both domain defaults (#00C8F0 cyan,
    // #FF8A3D thermal) so whichever is active always highlights its selected swatch.
    private static readonly string[] _domainPalette =
    {
        "#00C8F0", "#2D9CFF", "#3B82F6", "#5865F2", "#8B5CF6", "#EC4899",
        "#EF4444", "#FF5C5C", "#FF8A3D", "#F97316", "#F59E0B", "#FACC15",
        "#10B981", "#06B6D4", "#94A3B8",
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

        // Ensure WS_CAPTION | WS_THICKFRAME are present.
        // WindowChrome can strip these on some WPF versions; FancyZones requires
        // both to detect the window as moveable and show snap zones.
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style | WS_CAPTION | WS_THICKFRAME);

        // UIPI fix: allow lower-integrity processes (ShareX, FancyZones running
        // as a standard user) to deliver window-management messages to this
        // elevated window.  Without this, hooks in non-elevated apps are silently
        // blocked when Bansa is in the foreground.
        foreach (var msg in new uint[]
            { WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MOUSEMOVE,
              WM_MOVING, WM_ENTERSIZEMOVE, WM_EXITSIZEMOVE, WM_SIZING })
        {
            ChangeWindowMessageFilterEx(hwnd, msg, MSGFLT_ALLOW, IntPtr.Zero);
        }

        // HotkeyVirtualKey == 0 means the user explicitly cleared the hotkey.
        // Default in BansaSettings is 0x46 ('F'), so new users get Ctrl+Shift+F automatically.
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

        // Title bar — let WindowChrome handle HTCAPTION so FancyZones'
        // WM_NCHITTEST chain fires correctly. We only override the edges.
        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyBrandImages(ThemeManager.Current);
        ThemeManager.ThemeChanged += ApplyBrandImages;

        ReparentLimitsCards();

        // Reflect the persisted domain (accent already applied at startup in App.OnStartup)
        DomainHardwareBtn.IsChecked = DomainManager.Current == AppDomainMode.Hardware;
        DomainNetworkBtn.IsChecked  = DomainManager.Current == AppDomainMode.Network;
        UpdateSidebarForDomain(DomainManager.Current);
        if (DomainManager.Current == AppDomainMode.Hardware) NavigateToPanel(2);

        UnitBitsRadio.IsChecked  = Vm.UseBitsUnit;
        UnitBytesRadio.IsChecked = !Vm.UseBitsUnit;

        // Restore dual-scale toggle state from previous session
        _dualScale = App.Settings.DualScale;
        DualScaleBtn.IsChecked = _dualScale;

        // Accent is owned by DomainManager (per-domain dominant color, theme-aware) —
        // already applied at startup; nothing to do here.

        // Apply saved colors to override theme defaults before swatches are built
        Vm.SetDownColor(App.Settings.DownColorHex);
        Vm.SetUpColor(App.Settings.UpColorHex);
        Vm.SetCpuColor(App.Settings.CpuColorHex);
        Vm.SetGpuColor(App.Settings.GpuColorHex);
        Vm.SetRamColor(App.Settings.RamColorHex);

        PopulateSwatches(DownGraphSwatches,    App.Settings.DownColorHex,     hex => Vm.SetDownColor(hex));
        PopulateSwatches(UpGraphSwatches,      App.Settings.UpColorHex,       hex => Vm.SetUpColor(hex));
        PopulateSwatches(CpuColorSwatches,     App.Settings.CpuColorHex,      hex => Vm.SetCpuColor(hex));
        PopulateSwatches(GpuColorSwatches,     App.Settings.GpuColorHex,      hex => Vm.SetGpuColor(hex));
        PopulateSwatches(RamColorSwatches,     App.Settings.RamColorHex,      hex => Vm.SetRamColor(hex));
        SetupTempBandSwatches();
        PopulateSwatches(PingGoodSwatches,     App.Settings.PingGoodColorHex, hex => Vm.SetPingGoodColor(hex));
        PopulateSwatches(PingBadSwatches,      App.Settings.PingBadColorHex,  hex => Vm.SetPingBadColor(hex));
        PopulateSwatches(NetworkAccentSwatches,  _domainPalette, App.Settings.NetworkColorHex,  hex => SetDomainColor(AppDomainMode.Network, hex));
        PopulateSwatches(HardwareAccentSwatches, _domainPalette, App.Settings.HardwareColorHex, hex => SetDomainColor(AppDomainMode.Hardware, hex));

        try
        {
            _tray = new TrayIconManager(this, onQuit: () =>
            {
                _forceClose = true;
                Application.Current.Shutdown();
            });
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
                    RedrawBandwidthDonut(appList);
                    RedrawDashThroughput(history);
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

        // Restore main window bounds
        RestoreWindowBounds();

        // Restore Network tab chart height
        RestoreChartHeight();

        // Restore sort column + direction and column widths/visibility
        RestoreAppGridSort();
        RestoreAppGridColumns();

        // Open floating graph if it was visible in the last session
        if (App.Settings.ShowFloatingGraph)
            OpenFloatingGraph();

        // Init window-behaviour and auto-start toggles
        InitBehaviourToggles();
        InitAutoStartToggle();

        // Start minimized if the user set that preference
        if (App.Settings.StartMinimizedToTray)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => WindowState = WindowState.Minimized));

        // Wire hardware monitor panel live updates
        InitHardwarePanel();

        // Wire Tools panel
        _toolsVm = new ToolsViewModel();
        ToolsPanel.DataContext = _toolsVm;

        // First-run reversibility explainer — shown once. Deferred to Background priority so the
        // main window paints first; skipped when starting hidden to the tray.
        if (!App.Settings.WelcomeDismissed && !App.Settings.StartMinimizedToTray)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                try { new Views.WelcomeWindow { Owner = this }.ShowDialog(); }
                catch (Exception ex) { Log.Debug("Welcome dialog failed", ex); }
                App.Settings.WelcomeDismissed = true;
                SettingsManager.Save(App.Settings);
            }));
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
        UpdateMaxButtonGlyph();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // If "minimize on close" is on and this isn't a deliberate quit, send to tray instead.
        if (App.Settings.MinimizeOnClose && !_forceClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Signal that we're shutting down — the FloatingGraphWindow.Closed inline handler
        // checks this flag and skips its ShowFloatingGraph=false save so we can write the
        // correct value at the very end of this method.
        _isClosing = true;

        // Capture BEFORE anything closes.
        bool floatWasOpen = _floatingGraph?.IsVisible == true;

        try { UnregisterHotKey(new WindowInteropHelper(this).Handle, _hotKeyId); } catch { }
        try { _floatingGraph?.Close(); } catch { }
        try { _tray?.Dispose(); } catch { }
        // _toolsVm has no disposable resources
        try { Vm.Dispose(); } catch { }

        // Persist column layout before final settings write
        SaveAppGridColumns();

        // Save main window bounds
        SaveWindowBounds();

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

    private void RestoreWindowBounds()
    {
        var s = App.Settings;
        if (s.MainWindowW > 0 && s.MainWindowH > 0)
        {
            Width  = s.MainWindowW;
            Height = s.MainWindowH;
        }
        if (s.MainWindowX >= 0 && s.MainWindowY >= 0)
        {
            // Clamp to visible screen area
            var screen = System.Windows.SystemParameters.WorkArea;
            Left = Math.Max(screen.Left, Math.Min(s.MainWindowX, screen.Right  - Width));
            Top  = Math.Max(screen.Top,  Math.Min(s.MainWindowY, screen.Bottom - Height));
        }
        if (s.MainWindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        // Only save Normal state bounds (don't overwrite with maximized coords)
        if (WindowState == WindowState.Normal)
        {
            App.Settings.MainWindowX = Left;
            App.Settings.MainWindowY = Top;
            App.Settings.MainWindowW = Width;
            App.Settings.MainWindowH = Height;
        }
        App.Settings.MainWindowMaximized = WindowState == WindowState.Maximized;
    }

    // ── Network tab chart height persistence ─────────────────────────────────

    private void RestoreChartHeight()
    {
        double h = App.Settings.NetworkChartHeight;
        if (h >= 60 && h <= 600)
            ProcPanel.RowDefinitions[2].Height = new GridLength(h);
    }

    private void OnChartSplitterDragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        double h = ProcPanel.RowDefinitions[2].ActualHeight;
        if (h >= 60)
        {
            App.Settings.NetworkChartHeight = h;
            SettingsManager.Save(App.Settings);
        }
    }

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

    // Lightweight display wrapper so the ComboBox shows "Label (IP)" while
    // code can still extract the raw IP/hostname.
    private sealed class PingTargetItem
    {
        public string Target  { get; }
        public string Display { get; }
        public PingTargetItem(string target, string? label)
        {
            Target  = target;
            Display = string.IsNullOrEmpty(label) ? target : $"{label}  ({target})";
        }
        public override string ToString() => Display;
    }

    private PingTargetItem? SelectedPingItem =>
        PingTargetCombo.SelectedItem as PingTargetItem;

    private void PopulatePingTargetCombo()
    {
        PingTargetCombo.SelectionChanged -= OnPingTargetSelectionChanged;
        PingTargetCombo.Items.Clear();
        foreach (var t in App.Settings.PingTargets)
        {
            App.Settings.PingTargetLabels.TryGetValue(t, out var lbl);
            PingTargetCombo.Items.Add(new PingTargetItem(t, lbl));
        }
        var idx = App.Settings.PingTargets
            .FindIndex(t => string.Equals(t, App.Settings.PingTarget, StringComparison.OrdinalIgnoreCase));
        PingTargetCombo.SelectedIndex = idx >= 0 ? idx : (App.Settings.PingTargets.Count > 0 ? 0 : -1);
        PingTargetCombo.SelectionChanged += OnPingTargetSelectionChanged;
        SyncPingLabelBox();
    }

    private void SyncPingLabelBox()
    {
        var item = SelectedPingItem;
        if (item != null && App.Settings.PingTargetLabels.TryGetValue(item.Target, out var lbl))
            PingLabelBox.Text = lbl;
        else
            PingLabelBox.Text = "";
    }

    private void OnPingTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncPingLabelBox();
    }

    private void OnSetPingTarget(object sender, RoutedEventArgs e)
    {
        var item = SelectedPingItem;
        if (item is null) return;
        Vm.ChangePingTarget(item.Target);
        // Repopulate so active-target indicator updates
        PopulatePingTargetCombo();
        Vm.StatusText = $"Ping target set to {item.Display}.";
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
        Vm.ChangePingTarget(target);
        NewPingTargetBox.Text = "";
        PopulatePingTargetCombo();
        Vm.StatusText = $"Added and switched to {target}.";
    }

    private void OnRemovePingTarget(object sender, RoutedEventArgs e)
    {
        var item = SelectedPingItem;
        if (item is null) return;
        App.Settings.PingTargets.Remove(item.Target);
        App.Settings.PingTargetLabels.Remove(item.Target);
        if (string.Equals(App.Settings.PingTarget, item.Target, StringComparison.OrdinalIgnoreCase)
            && App.Settings.PingTargets.Count > 0)
            Vm.ChangePingTarget(App.Settings.PingTargets[0]);
        SettingsManager.Save(App.Settings);
        PopulatePingTargetCombo();
        Vm.StatusText = $"Removed ping target {item.Target}.";
    }

    private void OnSavePingLabel(object sender, RoutedEventArgs e)
    {
        var item = SelectedPingItem;
        if (item is null) return;
        var label = PingLabelBox.Text.Trim();
        if (string.IsNullOrEmpty(label))
            App.Settings.PingTargetLabels.Remove(item.Target);
        else
            App.Settings.PingTargetLabels[item.Target] = label;
        SettingsManager.Save(App.Settings);
        PopulatePingTargetCombo();   // refresh display text
        Vm.NotifyPingDisplayLabel();
        Vm.StatusText = string.IsNullOrEmpty(label)
            ? $"Label cleared for {item.Target}."
            : $"Label \"{label}\" saved for {item.Target}.";
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
        // Never intercept PrintScreen — let the OS / Snipping Tool handle it.
        if (e.Key is Key.PrintScreen or Key.Snapshot)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

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
        SettingsTabGeneral.Visibility    = tag == "general"    ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabNetwork.Visibility    = tag == "network"    ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabAppearance.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        SettingsTabScenario.Visibility = tag == "scenarios" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "network")    RefreshConnectionSpeedUI();
        if (tag == "scenarios") RefreshScenarioUI();
    }

    // ── Connection speed ──────────────────────────────────────────────────────

    private void RefreshConnectionSpeedUI()
    {
        bool gbps = App.Settings.ConnectionSpeedUnitGbps;
        SpeedUnitGbpsRadio.IsChecked = gbps;
        SpeedUnitMbpsRadio.IsChecked = !gbps;
        if (gbps)
        {
            ConnectionUploadBox.Text   = (App.Settings.ConnectionUploadMbps   / 1000.0).ToString("0.###");
            ConnectionDownloadBox.Text = (App.Settings.ConnectionDownloadMbps / 1000.0).ToString("0.###");
            SpeedUnitLabel.Text = "Gbps";
        }
        else
        {
            ConnectionUploadBox.Text   = App.Settings.ConnectionUploadMbps.ToString();
            ConnectionDownloadBox.Text = App.Settings.ConnectionDownloadMbps.ToString();
            SpeedUnitLabel.Text = "Mbps";
        }
        ProfilesList.ItemsSource = App.Settings.LimitProfiles;
    }

    private void OnSpeedUnitChanged(object sender, RoutedEventArgs e)
    {
        if (SpeedUnitGbpsRadio is null) return;
        App.Settings.ConnectionSpeedUnitGbps = SpeedUnitGbpsRadio.IsChecked == true;
        SettingsManager.Save(App.Settings);
        RefreshConnectionSpeedUI();
    }

    private void OnSaveConnectionSpeed(object sender, RoutedEventArgs e)
    {
        bool gbps = App.Settings.ConnectionSpeedUnitGbps;
        if (gbps)
        {
            if (double.TryParse(ConnectionUploadBox.Text.Trim(),   out double u) && u >= 0)
                App.Settings.ConnectionUploadMbps   = (int)(u * 1000);
            if (double.TryParse(ConnectionDownloadBox.Text.Trim(), out double d) && d >= 0)
                App.Settings.ConnectionDownloadMbps = (int)(d * 1000);
        }
        else
        {
            if (int.TryParse(ConnectionUploadBox.Text.Trim(),   out int u) && u >= 0)
                App.Settings.ConnectionUploadMbps   = u;
            if (int.TryParse(ConnectionDownloadBox.Text.Trim(), out int d) && d >= 0)
                App.Settings.ConnectionDownloadMbps = d;
        }
        SettingsManager.Save(App.Settings);
        Vm.StatusText = "Connection speed saved.";
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

    private void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Services.LimitProfile profile) return;
        var dlg = new Views.EditProfileDialog(profile.Name, profile.UploadKbps, profile.DownloadKbps)
        {
            Owner = this
        };
        if (dlg.ShowDialog() == true)
        {
            profile.Name         = dlg.ProfileName;
            profile.UploadKbps   = dlg.UploadKbps;
            profile.DownloadKbps = dlg.DownloadKbps;
            SettingsManager.Save(App.Settings);
            RefreshProfilesList();
        }
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

    // Header domain switch: re-skin accent + jump to that domain's primary panel.
    private void OnDomainClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<AppDomainMode>(tag, out var mode)) return;
        if (DomainManager.Current == mode) return;

        DomainManager.Apply(mode);
        App.Settings.Domain = mode.ToString();
        SettingsManager.Save(App.Settings);

        UpdateSidebarForDomain(mode);
        NavigateToPanel(mode == AppDomainMode.Hardware ? 2 : 0);
    }

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

    /// <summary>Switch to a top-level panel by index (0=Dashboard,1=Network,2=Hardware,3=Tools,4=History,5=Settings).</summary>
    private void NavigateToPanel(int idx)
    {
        NavDashboard.IsChecked = idx == 0;
        NavProcesses.IsChecked = idx == 1;
        NavHardware.IsChecked  = idx == 2;
        NavTools.IsChecked     = idx == 3;
        NavHistory.IsChecked   = idx == 4;
        NavLimits.IsChecked    = idx == 6;

        // History is domain-aware: Network history vs Hardware history.
        bool hw = DomainManager.Current == AppDomainMode.Hardware;
        UIElement historyEl = hw ? HardwareHistoryPanel : HistoryPanel;
        UIElement otherHistory = hw ? HistoryPanel : HardwareHistoryPanel;
        if (otherHistory.Visibility == Visibility.Visible) FadeOut(otherHistory);

        UIElement[] panels = [DashboardPanel, ProcPanel, HardwareMonitorPanel, ToolsPanel, historyEl, SettingsPanel, LimitsScenariosPanel];
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

        // Refresh history data whenever the user navigates to that tab.
        if (idx == 4)
        {
            if (hw) HardwareHistoryPanel.Reload();
            else    HistoryPanel.Reload();
        }

        // Limits & Scenarios: refresh its moved cards + the limited-apps summary.
        if (idx == 6)
        {
            RefreshProfilesList();
            PopulatePingTargetCombo();
            RefreshScenarioUI();
            PopulateLimitedApps();
        }
    }

    private void OnSettingsGearClick(object sender, RoutedEventArgs e) => NavigateToPanel(5);

    /// <summary>Show only the nav items relevant to the active domain (mockup-style per-mode tabs).</summary>
    private void UpdateSidebarForDomain(AppDomainMode mode)
    {
        bool net = mode == AppDomainMode.Network;
        NavDashboard.Visibility = net ? Visibility.Visible : Visibility.Collapsed;
        NavProcesses.Visibility = net ? Visibility.Visible : Visibility.Collapsed;
        NavLimits.Visibility    = net ? Visibility.Visible : Visibility.Collapsed;
        NavHardware.Visibility  = net ? Visibility.Collapsed : Visibility.Visible;
        // Tools: Hardware mode only. Network tabs = Dashboard · Live Traffic · Limits & Scenarios · History.
        NavTools.Visibility     = net ? Visibility.Collapsed : Visibility.Visible;
        // History stays visible in both modes.
    }

    // ── Limits & Scenarios panel ───────────────────────────────────────────────

    private bool _limitsCardsMoved;

    /// <summary>
    /// Relocates the Limit Profiles, Global Cap, Ping Monitor and Scenario editor cards
    /// out of Settings into the Limits &amp; Scenarios panel. Done by re-parenting the live
    /// elements so all their x:Names and event handlers keep working unchanged.
    /// </summary>
    private void ReparentLimitsCards()
    {
        if (_limitsCardsMoved) return;
        _limitsCardsMoved = true;
        MoveCard(CardLimitProfiles);
        MoveCard(CardGlobalCap);
        MoveCard(CardPingMonitor);
        MoveCard(CardScenarios);

        void MoveCard(FrameworkElement card)
        {
            if (card.Parent is System.Windows.Controls.Panel p)
            {
                p.Children.Remove(card);
                LimitsHost.Children.Add(card);
            }
        }
    }

    /// <summary>Read-only summary of apps that currently have an up/down limit or are blocked.</summary>
    private void PopulateLimitedApps()
    {
        LimitedAppsList.Items.Clear();

        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in App.Settings.AppUploadLimitsKBs.Keys)   paths.Add(k);
        foreach (var k in App.Settings.AppDownloadLimitsKBs.Keys) paths.Add(k);

        foreach (var path in paths)
        {
            int up   = App.Settings.AppUploadLimitsKBs.TryGetValue(path, out var u) ? u : 0;
            int down = App.Settings.AppDownloadLimitsKBs.TryGetValue(path, out var d) ? d : 0;
            if (up <= 0 && down <= 0) continue;
            LimitedAppsList.Items.Add(BuildLimitedAppRow(path,
                System.IO.Path.GetFileNameWithoutExtension(path), up, down));
        }

        bool any = LimitedAppsList.Items.Count > 0;
        LimitedAppsEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement BuildLimitedAppRow(string path, string name, int up, int down)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nm = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)FindResource("TextBrush") };
        Grid.SetColumn(nm, 0);
        var upTb = new TextBlock { Text = up > 0 ? up.ToString() : "—", HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("RobotoMonoFamily"),
            Foreground = (Brush)FindResource(up > 0 ? "ChartUpBrush" : "MutedTextBrush") };
        Grid.SetColumn(upTb, 1);
        var dnTb = new TextBlock { Text = down > 0 ? down.ToString() : "—", HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("RobotoMonoFamily"),
            Foreground = (Brush)FindResource(down > 0 ? "ChartDownBrush" : "MutedTextBrush") };
        Grid.SetColumn(dnTb, 2);

        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var editBtn = new System.Windows.Controls.Button { Content = "Edit", Padding = new Thickness(10, 3, 10, 3), FontSize = 11, Cursor = Cursors.Hand };
        editBtn.Click += (_, _) => EditLimitInline(path, name, up, down);
        var clearBtn = new System.Windows.Controls.Button { Content = "Clear", Padding = new Thickness(10, 3, 10, 3), FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand };
        clearBtn.Click += async (_, _) => { await Vm.SetLimitByPathAsync(path, 0, 0); PopulateLimitedApps(); };
        actions.Children.Add(editBtn); actions.Children.Add(clearBtn);
        Grid.SetColumn(actions, 3);

        g.Children.Add(nm); g.Children.Add(upTb); g.Children.Add(dnTb); g.Children.Add(actions);
        return g;
    }

    private async void EditLimitInline(string path, string name, int up, int down)
    {
        var dlg = new SetLimitWindow(name, up, down,
                                     App.Settings.ConnectionUploadMbps, App.Settings.ConnectionDownloadMbps,
                                     false) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            await Vm.SetLimitByPathAsync(path, dlg.UploadKbps, dlg.DownloadKbps);
            PopulateLimitedApps();
        }
    }

    // ── Dashboard card click handlers ────────────────────────────────────────

    private void OnDashBandwidthClick(object sender, MouseButtonEventArgs e) => NavigateToPanel(1);
    private void OnDashPingClick(object sender, MouseButtonEventArgs e)      => NavigateToLimitsCard(CardPingMonitor);

    // ── Sidebar STATUS card clicks → respective tabs ─────────────────────────

    /// <summary>CPU/GPU box → Hardware dashboard (switches domain so accent + nav follow).</summary>
    private void OnSidebarHwClick(object sender, MouseButtonEventArgs e)
    {
        EnsureDomain(AppDomainMode.Hardware);
        NavigateToPanel(2);
    }

    /// <summary>Down/Up totals box → Network dashboard.</summary>
    private void OnSidebarBandwidthClick(object sender, MouseButtonEventArgs e)
    {
        EnsureDomain(AppDomainMode.Network);
        NavigateToPanel(0);
    }

    /// <summary>Ping box → Ping Monitor card in Limits &amp; Scenarios.</summary>
    private void OnSidebarPingClick(object sender, MouseButtonEventArgs e)
        => NavigateToLimitsCard(CardPingMonitor);

    /// <summary>
    /// Applies a domain (accent reskin + per-domain sidebar + header toggle state) WITHOUT
    /// navigating — callers decide the destination panel. No-op when already in that domain.
    /// </summary>
    private void EnsureDomain(AppDomainMode mode)
    {
        if (DomainManager.Current == mode) return;
        DomainManager.Apply(mode);
        App.Settings.Domain = mode.ToString();
        SettingsManager.Save(App.Settings);
        UpdateSidebarForDomain(mode);
        DomainHardwareBtn.IsChecked = mode == AppDomainMode.Hardware;
        DomainNetworkBtn.IsChecked  = mode == AppDomainMode.Network;
    }

    /// <summary>Ensures Network domain, opens Limits &amp; Scenarios (panel 6) and scrolls to a card.</summary>
    private void NavigateToLimitsCard(FrameworkElement card)
    {
        EnsureDomain(AppDomainMode.Network);
        NavigateToPanel(6);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => card.BringIntoView()));
    }

    // Keyboard activation for the click-only dashboard / sidebar cards. Plain Borders aren't
    // focusable or Enter/Space-activatable, so each accessible card carries a Tag naming its
    // action and this raises the same navigation / toggle a mouse click would.
    private void OnCardKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Space) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string action) return;
        e.Handled = true;
        switch (action)
        {
            case "nav:network":   NavigateToPanel(1); break;
            case "nav:dashboard": EnsureDomain(AppDomainMode.Network); NavigateToPanel(0); break;
            case "nav:hardware":  EnsureDomain(AppDomainMode.Hardware); NavigateToPanel(2); break;
            case "ping":          NavigateToLimitsCard(CardPingMonitor); break;
            case "scenario":      _ = Vm.ToggleScenarioCommand.ExecuteAsync(null); break;
            case "globalcap":     ToggleGlobalCap(); break;
        }
    }

    // ────────── Hardware monitor panel ──────────

    private void InitHardwarePanel()
    {
        if (HardwareMonitor.Instance is not { } hw) return;

        // Feed the latest reading immediately (sensor might already have data)
        if (hw.Latest != HardwareSnapshot.Empty)
            UpdateHardwarePanel(hw.Latest);

        // Subscribe for live updates
        hw.Sampled += snap => Dispatcher.InvokeAsync(() => UpdateHardwarePanel(snap));
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
        RedrawSidebarThermals(snap);

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

    // ── Column width + visibility persistence ─────────────────────────────────

    private void SaveAppGridColumns()
    {
        var widths  = App.Settings.AppGridColumnWidths;
        var hidden  = App.Settings.AppGridHiddenColumns;
        widths.Clear();
        hidden.Clear();
        foreach (var col in AppGrid.Columns)
        {
            var key = col.Header as string;
            if (string.IsNullOrEmpty(key)) continue;
            if (col.ActualWidth > 0) widths[key] = col.ActualWidth;
            if (col.Visibility == Visibility.Collapsed) hidden.Add(key);
        }
        SettingsManager.Save(App.Settings);
    }

    private void RestoreAppGridColumns()
    {
        var widths = App.Settings.AppGridColumnWidths;
        var hidden = App.Settings.AppGridHiddenColumns;
        foreach (var col in AppGrid.Columns)
        {
            var key = col.Header as string;
            if (string.IsNullOrEmpty(key)) continue;
            if (widths.TryGetValue(key, out double w) && w > 0)
                col.Width = new DataGridLength(w);
            col.Visibility = hidden.Contains(key) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ── Right-click column header → show/hide columns ────────────────────────

    private void OnAppGridHeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        _contextApp = row?.DataContext as AppRowViewModel;

        var header = FindVisualAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column == null) return;

        e.Handled = true;

        var menu = new ContextMenu();
        foreach (var col in AppGrid.Columns)
        {
            var label = col.Header as string;
            if (string.IsNullOrEmpty(label)) continue;
            var captured = col;
            var item = new MenuItem
            {
                Header      = label,
                IsCheckable = true,
                IsChecked   = col.Visibility == Visibility.Visible,
            };
            item.Click += (_, _) =>
            {
                captured.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                SaveAppGridColumns();
            };
            menu.Items.Add(item);
        }

        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
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

    private AppRowViewModel? _contextApp;

    private AppRowViewModel? Selected => _contextApp ?? AppGrid.SelectedItem as AppRowViewModel;

    private void OnDashAppGridRowRightClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindVisualAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        _contextApp = row?.DataContext as AppRowViewModel;
    }

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
            Views.ConfirmDialog.Show("No active processes", "This app has no active processes right now.", confirmText: "OK", cancelText: null);
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
        Views.ConfirmDialog.Show($"Processes for {app.Name}", sb.ToString(), confirmText: "OK", cancelText: null);
    }

    // ────────── Settings ──────────

    private void OnThemeToggleClick(object sender, RoutedEventArgs e) => Vm.ToggleThemeCommand.Execute(null);

    private void ApplyBrandImages(AppTheme theme)
    {
        var logoUri = theme == AppTheme.Dark
            ? new Uri("pack://application:,,,/Bansa;component/Resources/bansa-dark.png")
            : new Uri("pack://application:,,,/Bansa;component/Resources/bansa-light.png");
        BrandLogo.Source = new System.Windows.Media.Imaging.BitmapImage(logoUri);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
            new Uri("pack://application:,,,/Bansa;component/Resources/Icon_bansa-dark.ico"));
    }
    private void OnUnitBytesClick(object sender, RoutedEventArgs e) => Vm.UseBitsUnit = false;
    private void OnUnitBitsClick(object sender, RoutedEventArgs e)  => Vm.UseBitsUnit = true;

    private void PopulateSwatches(ItemsControl host, string currentHex, Action<string> onPick)
        => PopulateSwatches(host, _palette, currentHex, onPick);

    private void PopulateSwatches(ItemsControl host, IReadOnlyList<string> palette, string currentHex, Action<string> onPick)
    {
        host.Items.Clear();
        foreach (var hex in palette)
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

    // ── Temperature bands (stepped color) ──────────────────────────────────────

    /// <summary>Populates the 3 band swatch rows + threshold boxes + range labels from settings.</summary>
    private void SetupTempBandSwatches()
    {
        PopulateSwatches(TempCoolBandSwatches, _tempPalette, App.Settings.TempBandCoolColorHex, hex => Vm.SetTempCoolBand(hex));
        PopulateSwatches(TempWarmBandSwatches, _tempPalette, App.Settings.TempBandWarmColorHex, hex => Vm.SetTempWarmBand(hex));
        PopulateSwatches(TempHotBandSwatches,  _tempPalette, App.Settings.TempBandHotColorHex,  hex => Vm.SetTempHotBand(hex));
        TempWarmThreshBox.Text = App.Settings.TempWarmThresholdC.ToString();
        TempHotThreshBox.Text  = App.Settings.TempHotThresholdC.ToString();
        UpdateTempBandLabels();
    }

    /// <summary>Validates and persists the warm/hot thresholds, then refreshes the range labels.</summary>
    private void OnTempThresholdChanged(object sender, RoutedEventArgs e)
    {
        int warm = App.Settings.TempWarmThresholdC;
        int hot  = App.Settings.TempHotThresholdC;
        if (int.TryParse(TempWarmThreshBox.Text, out int w)) warm = Math.Clamp(w, 0, 120);
        if (int.TryParse(TempHotThreshBox.Text,  out int h)) hot  = Math.Clamp(h, 0, 121);
        if (hot <= warm) hot = Math.Min(warm + 1, 121);   // hot must sit above warm

        App.Settings.TempWarmThresholdC = warm;
        App.Settings.TempHotThresholdC  = hot;
        SettingsManager.Save(App.Settings);

        // Reflect any clamping back into the boxes + labels.
        TempWarmThreshBox.Text = warm.ToString();
        TempHotThreshBox.Text  = hot.ToString();
        UpdateTempBandLabels();
    }

    private void UpdateTempBandLabels()
    {
        int warm = App.Settings.TempWarmThresholdC;
        int hot  = App.Settings.TempHotThresholdC;
        TempCoolBandLabel.Text = $"Cool — below {warm}°C";
        TempWarmBandLabel.Text = $"Warm — {warm}–{hot - 1}°C";
        TempHotBandLabel.Text  = $"Hot — {hot}°C and above";
    }

    // Persist a domain's dominant color; live-refresh the accent if that domain is active.
    private void SetDomainColor(AppDomainMode mode, string hex)
    {
        if (mode == AppDomainMode.Network) App.Settings.NetworkColorHex = hex;
        else                               App.Settings.HardwareColorHex = hex;
        SettingsManager.Save(App.Settings);
        if (DomainManager.Current == mode) DomainManager.Apply(mode);
    }

    // ────────── Scenarios ──────────

    private void OnScenarioBtnClick(object sender, RoutedEventArgs e)
        => _ = Vm.ToggleScenarioCommand.ExecuteAsync(null);

    private void OnGlobalCapBtnClick(object sender, MouseButtonEventArgs e) => ToggleGlobalCap();

    /// <summary>
    /// Toggles the global upload cap. If the user is enabling it but no value is
    /// configured yet (0 = no cap), there's nothing to apply — so instead of flipping
    /// on a no-op cap, send them to Limits &amp; Scenarios and focus the cap value box.
    /// </summary>
    private void ToggleGlobalCap()
    {
        // With no cap value configured the toggle has nothing to apply, so it's not
        // toggleable — instead it opens Limits & Scenarios → Global upload cap to set one.
        if (Vm.GlobalUploadCapKBs <= 0)
        {
            NavigateToLimitsCard(CardGlobalCap);
            Dispatcher.BeginInvoke(() =>
            {
                GlobalCapValueBox.Focus();
                GlobalCapValueBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
            return;
        }
        Vm.IsGlobalUploadCapEnabled = !Vm.IsGlobalUploadCapEnabled;
    }

    // ── Scenarios profile editor (Settings tab) ──────────────────────────────

    // Exposed for DataTemplate bindings via RelativeSource AncestorType=Window
    public IReadOnlyList<LimitProfile> AvailableLimitProfiles => App.Settings.LimitProfiles;

    private class ScenarioProfileRow
    {
        public string ExePath    { get; set; } = "";
        public string AppName    { get; set; } = "";
        public string UploadKBs  { get; set; } = "0";
        public string DownloadKBs{ get; set; } = "0";
    }

    private class AppSearchEntry
    {
        public string Name      { get; set; } = "";
        public string ImagePath { get; set; } = "";
    }

    private List<AppSearchEntry> _allScenarioApps = new();
    private AppSearchEntry? _selectedScenarioApp;
    private bool _suppressSearchUpdate;

    private async void RefreshScenarioUI()
    {
        // Snapshot UI-thread collections before the background task
        var networkApps = Vm.Apps
            .Where(a => !string.IsNullOrEmpty(a.ImagePath))
            .Select(a => (name: System.IO.Path.GetFileNameWithoutExtension(a.ImagePath), path: a.ImagePath))
            .ToList();
        var profileApps = App.Settings.ScenarioProfiles
            .Select(kv => (
                name: !string.IsNullOrEmpty(kv.Value.AppName)
                    ? kv.Value.AppName
                    : System.IO.Path.GetFileNameWithoutExtension(kv.Key),
                path: kv.Key))
            .ToList();
        var limitApps = App.Settings.AppUploadLimitsKBs.Keys
            .Concat(App.Settings.AppDownloadLimitsKBs.Keys)
            .Select(p => (name: System.IO.Path.GetFileNameWithoutExtension(p), path: p))
            .ToList();

        // Reset search UI immediately
        _suppressSearchUpdate = true;
        ScenarioSearchBox.Text = "";
        _suppressSearchUpdate = false;
        ScenarioClearBtn.Visibility = Visibility.Collapsed;
        _selectedScenarioApp = null;
        ScenarioSearchPopup.IsOpen = false;

        // Enumerate ALL running processes in background (MainModule.FileName can be slow)
        _allScenarioApps = await Task.Run(() =>
        {
            var dict = new Dictionary<string, AppSearchEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var proc in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (string.IsNullOrEmpty(path)) continue;
                    var name = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrEmpty(name)) continue;
                    dict.TryAdd(name, new AppSearchEntry { Name = name, ImagePath = path });
                }
                catch { }
                finally { proc.Dispose(); }
            }

            // Merge apps from network monitor, profiles and per-app limits
            foreach (var (name, path) in networkApps.Concat(profileApps).Concat(limitApps))
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path))
                    dict.TryAdd(name, new AppSearchEntry { Name = name, ImagePath = path });

            return dict.Values
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

        RefreshScenarioProfilesList();
    }

    private void OnScenarioSearchGotFocus(object sender, RoutedEventArgs e)
    {
        ScenarioResultsList.ItemsSource = _allScenarioApps;
        if (_allScenarioApps.Count > 0)
            ScenarioSearchPopup.IsOpen = true;
    }

    private void OnScenarioSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchUpdate) return;
        var text = ScenarioSearchBox.Text ?? "";
        ScenarioClearBtn.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var filtered = string.IsNullOrWhiteSpace(text)
            ? _allScenarioApps
            : _allScenarioApps.Where(a => a.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

        ScenarioResultsList.ItemsSource = filtered;
        ScenarioSearchPopup.IsOpen = filtered.Count > 0;
    }

    private void OnScenarioResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ScenarioResultsList.SelectedItem is not AppSearchEntry app) return;
        _selectedScenarioApp = app;
        _suppressSearchUpdate = true;
        ScenarioSearchBox.Text = app.Name;
        _suppressSearchUpdate = false;
        ScenarioResultsList.SelectedIndex = -1;
        ScenarioSearchPopup.IsOpen = false;
    }

    private void OnScenarioSearchClear(object sender, RoutedEventArgs e)
    {
        _suppressSearchUpdate = true;
        ScenarioSearchBox.Text = "";
        _suppressSearchUpdate = false;
        _selectedScenarioApp = null;
        ScenarioClearBtn.Visibility = Visibility.Collapsed;
        ScenarioResultsList.ItemsSource = _allScenarioApps;
        ScenarioSearchPopup.IsOpen = _allScenarioApps.Count > 0;
        ScenarioSearchBox.Focus();
    }

    private void RefreshScenarioProfilesList()
    {
        var profiles = App.Settings.ScenarioProfiles;
        var rows = profiles.Select(kv => new ScenarioProfileRow
        {
            ExePath     = kv.Key,
            AppName     = string.IsNullOrEmpty(kv.Value.AppName)
                            ? System.IO.Path.GetFileNameWithoutExtension(kv.Key)
                            : kv.Value.AppName,
            UploadKBs   = kv.Value.UploadKBs.ToString(),
            DownloadKBs = kv.Value.DownloadKBs.ToString(),
        }).ToList();
        ScenarioProfilesControl.ItemsSource = rows;
        ScenarioEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnScenarioQuickProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox combo) return;
        if (combo.SelectedItem is not LimitProfile profile) return;
        if (combo.Tag is not string path) return;
        if (!App.Settings.ScenarioProfiles.TryGetValue(path, out var entry)) return;
        entry.UploadKBs   = profile.UploadKbps;
        entry.DownloadKBs = profile.DownloadKbps;
        if (Vm.IsScenarioActive) Vm.ApplyScenarioAppLimits(path, entry);
        SettingsManager.Save(App.Settings);
        RefreshScenarioProfilesList();
    }

    private void OnAddScenarioApp(object sender, RoutedEventArgs e)
    {
        // Use item selected from popup; fall back to exact name match if the user typed one in
        AppSearchEntry? selected = _selectedScenarioApp
            ?? _allScenarioApps.FirstOrDefault(a =>
                   a.Name.Equals(ScenarioSearchBox.Text?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));
        if (selected == null || string.IsNullOrEmpty(selected.ImagePath)) return;

        var key = selected.ImagePath.ToLowerInvariant();
        if (!App.Settings.ScenarioProfiles.ContainsKey(key))
        {
            App.Settings.ScenarioProfiles[key] = new ScenarioEntry
            {
                AppName     = selected.Name,
                UploadKBs   = 0,
                DownloadKBs = 0,
            };
            SettingsManager.Save(App.Settings);
        }

        ScenarioSearchBox.Text = "";
        _selectedScenarioApp = null;
        RefreshScenarioProfilesList();
    }

    private void OnRemoveScenarioProfile(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string path) return;
        App.Settings.ScenarioProfiles.Remove(path);
        if (Vm.IsScenarioActive) Vm.ClearScenarioAppLimits(path);
        SettingsManager.Save(App.Settings);
        RefreshScenarioProfilesList();
    }

    private void OnScenarioUploadLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string path) return;
        if (!int.TryParse(tb.Text.Trim(), out int kbs) || kbs < 0) { kbs = 0; tb.Text = "0"; }
        if (!App.Settings.ScenarioProfiles.TryGetValue(path, out var entry)) return;
        entry.UploadKBs = kbs;
        if (Vm.IsScenarioActive) Vm.ApplyScenarioAppLimits(path, entry);
        SettingsManager.Save(App.Settings);
    }

    private void OnScenarioDownloadLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string path) return;
        if (!int.TryParse(tb.Text.Trim(), out int kbs) || kbs < 0) { kbs = 0; tb.Text = "0"; }
        if (!App.Settings.ScenarioProfiles.TryGetValue(path, out var entry)) return;
        entry.DownloadKBs = kbs;
        if (Vm.IsScenarioActive) Vm.ApplyScenarioAppLimits(path, entry);
        SettingsManager.Save(App.Settings);
    }

    // ────────── Global cap ──────────

    private void OnClearGlobalCap(object sender, RoutedEventArgs e)
    {
        Vm.GlobalUploadCapKBs = 0;
        // Untick the master switch — its change handler removes the cap across both layers.
        Vm.IsGlobalUploadCapEnabled = false;
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

    // ────────── Behaviour toggles ──────────

    private void InitBehaviourToggles()
    {
        MinimizeOnCloseToggle.IsChecked  = App.Settings.MinimizeOnClose;
        StartMinimizedToggle.IsChecked   = App.Settings.StartMinimizedToTray;
        ShowTrayIconToggle.IsChecked     = App.Settings.ShowTrayIcon;
    }

    private void OnMinimizeOnCloseToggleClick(object sender, RoutedEventArgs e)
    {
        App.Settings.MinimizeOnClose = MinimizeOnCloseToggle.IsChecked == true;
        SettingsManager.Save(App.Settings);
    }

    private void OnStartMinimizedToggleClick(object sender, RoutedEventArgs e)
    {
        App.Settings.StartMinimizedToTray = StartMinimizedToggle.IsChecked == true;
        SettingsManager.Save(App.Settings);
    }

    private void OnShowTrayIconToggleClick(object sender, RoutedEventArgs e)
    {
        App.Settings.ShowTrayIcon = ShowTrayIconToggle.IsChecked == true;
        SettingsManager.Save(App.Settings);
    }

    // ────────── Auto-start ──────────

    private void InitAutoStartToggle()
    {
        bool enabled = AutoStartManager.IsEnabled();
        AutoStartToggle.IsChecked = enabled;
        AutoStartStatusLabel.Text = enabled
            ? "Enabled — Bansa will launch at Windows login"
            : "Disabled — Bansa won't start automatically";
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
                ? "Enabled — Bansa will launch at Windows login"
                : "Disabled — Bansa won't start automatically";
            Vm.StatusText = enable ? "Auto-start enabled." : "Auto-start disabled.";
        }
        else
        {
            // Revert the toggle — the operation failed (likely needs admin, or schtasks error)
            AutoStartToggle.IsChecked = !enable;
            AutoStartStatusLabel.Text = "Failed — run Bansa as administrator to change this setting";
            Vm.StatusText = "Auto-start change failed. Try running Bansa as administrator.";
        }
    }

    // ────────── Settings export / import ──────────

    private void OnExportSettings(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.SaveFileDialog
        {
            Title    = "Export Bansa settings",
            Filter   = "JSON files (*.json)|*.json",
            FileName = $"bansa-settings-{DateTime.Now:yyyy-MM-dd}.json",
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                App.Settings,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(dlg.FileName, json);
            Vm.StatusText = $"Settings exported to {System.IO.Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            Views.ConfirmDialog.Show("Export failed", ex.Message, confirmText: "OK", cancelText: null);
        }
    }

    private void OnImportSettings(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title  = "Import Bansa settings",
            Filter = "JSON files (*.json)|*.json",
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        BansaSettings imported;
        try
        {
            var json = System.IO.File.ReadAllText(dlg.FileName);
            imported = System.Text.Json.JsonSerializer.Deserialize<BansaSettings>(json)
                       ?? throw new InvalidOperationException("Empty or null settings file.");
        }
        catch (Exception ex)
        {
            Views.ConfirmDialog.Show("Could not read settings file", ex.Message, confirmText: "OK", cancelText: null);
            return;
        }

        var ok = Views.ConfirmDialog.Show(
            "Import settings?",
            "This will replace all current settings — limits, profiles, colors, and preferences.",
            confirmText: "Replace settings", cancelText: "Cancel", danger: true);
        if (!ok) return;

        App.Settings = imported;
        SettingsManager.Save(App.Settings);
        ApplyImportedSettings();
        Vm.StatusText = "Settings imported successfully.";
    }

    private void ApplyImportedSettings()
    {
        // Units
        bool useBits = App.Settings.RateUnit.Equals("Bits", StringComparison.OrdinalIgnoreCase);
        Vm.UseBitsUnit      = useBits;
        UnitBitsRadio.IsChecked  = useBits;
        UnitBytesRadio.IsChecked = !useBits;

        // Theme
        bool dark = App.Settings.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        if (Vm.IsDarkTheme != dark) Vm.IsDarkTheme = dark;

        // Accent is owned by DomainManager (per-domain dominant color) — re-assert it
        DomainManager.Apply(DomainManager.Current);

        // Colors
        Vm.SetDownColor(App.Settings.DownColorHex);
        Vm.SetUpColor(App.Settings.UpColorHex);
        Vm.SetCpuColor(App.Settings.CpuColorHex);
        Vm.SetGpuColor(App.Settings.GpuColorHex);
        Vm.SetRamColor(App.Settings.RamColorHex);
        PopulateSwatches(DownGraphSwatches, App.Settings.DownColorHex,     hex => Vm.SetDownColor(hex));
        PopulateSwatches(UpGraphSwatches,   App.Settings.UpColorHex,       hex => Vm.SetUpColor(hex));
        PopulateSwatches(CpuColorSwatches,  App.Settings.CpuColorHex,      hex => Vm.SetCpuColor(hex));
        PopulateSwatches(GpuColorSwatches,  App.Settings.GpuColorHex,      hex => Vm.SetGpuColor(hex));
        PopulateSwatches(RamColorSwatches,  App.Settings.RamColorHex,      hex => Vm.SetRamColor(hex));
        SetupTempBandSwatches();
        PopulateSwatches(PingGoodSwatches,  App.Settings.PingGoodColorHex, hex => Vm.SetPingGoodColor(hex));
        PopulateSwatches(PingBadSwatches,   App.Settings.PingBadColorHex,  hex => Vm.SetPingBadColor(hex));
        PopulateSwatches(NetworkAccentSwatches,  _domainPalette, App.Settings.NetworkColorHex,  hex => SetDomainColor(AppDomainMode.Network, hex));
        PopulateSwatches(HardwareAccentSwatches, _domainPalette, App.Settings.HardwareColorHex, hex => SetDomainColor(AppDomainMode.Hardware, hex));

        // Behavior toggles
        InitBehaviourToggles();

        // Dual-scale chart toggle
        _dualScale = App.Settings.DualScale;
        DualScaleBtn.IsChecked = _dualScale;

        // Ping targets
        PopulatePingTargetCombo();

        // Connection speed (refreshed next time user opens Network tab)
        // Columns + sort
        RestoreAppGridSort();
        RestoreAppGridColumns();
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
