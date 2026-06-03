using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Bansa.ViewModels;
using Bansa.Views;
using WinForms = System.Windows.Forms;

namespace Bansa.Services;

/// <summary>
/// Live tray icon rendered as two lines of text (↓ / ↑ rates).
/// A dark rounded background + drop-shadow keeps the numbers readable
/// on any taskbar color. Hover shows a rich popup; cursor-poll closes it cleanly.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly WinForms.NotifyIcon _tray;
    private readonly Window _ownerWindow;
    private readonly TrayPopupWindow _popup;
    private readonly DispatcherTimer _cursorPoll;
    private readonly DispatcherTimer _hoverTimer;         // delay before showing popup on hover
    private volatile int _iconX = int.MinValue;           // cursor X (physical px) captured on last MouseMove
    private volatile int _iconY = int.MinValue;           // cursor Y (physical px) captured on last MouseMove
    private DateTime _popupShowTime = DateTime.MinValue;  // when popup was most recently shown
    private IntPtr _currentIconHandle = IntPtr.Zero;
    private int _iconSize;

    private long _lastDown, _lastUp;
    private int _lastPing;
    private IReadOnlyList<(long Down, long Up)> _lastHistory = Array.Empty<(long, long)>();
    private IReadOnlyList<AppRowViewModel> _lastApps = Array.Empty<AppRowViewModel>();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSMICON = 49;   // system tray icon width in physical pixels

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }


    public TrayIconManager(Window ownerWindow, Action? onQuit = null)
    {
        _ownerWindow = ownerWindow;
        _iconSize = ClampSize(App.Settings?.TrayIconSize ?? 96);

        _tray = new WinForms.NotifyIcon { Visible = App.Settings?.ShowTrayIcon != false };
        _tray.MouseClick       += OnTrayClick;
        _tray.MouseDoubleClick += OnTrayDoubleClick;
        _tray.MouseMove        += OnTrayMouseMove;

        _popup = new TrayPopupWindow();

        // 200 ms hover-to-show timer — fires once, verifies cursor is still over the
        // tray icon (not just somewhere on the taskbar), then shows the popup.
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_popup.IsVisible) return;
            // Check live cursor position — if mouse passed quickly the cursor will no
            // longer be inside the icon hitbox and the popup should not appear.
            if (GetCursorPos(out var pt) && IsCursorOverTrayIcon(pt))
                ShowPopup();
        };

        var pinItem = new WinForms.ToolStripMenuItem("Always on top") { CheckOnClick = true, Checked = true };
        var ctItem  = new WinForms.ToolStripMenuItem("Click through")  { CheckOnClick = true, Checked = false };
        pinItem.Click += (_, _) =>
            Application.Current.Dispatcher.Invoke(() => _popup.IsAlwaysOnTop = pinItem.Checked);
        ctItem.Click += (_, _) =>
            Application.Current.Dispatcher.Invoke(() => _popup.SetClickThrough(ctItem.Checked));

        var popupMenu = new WinForms.ToolStripMenuItem("Popup Window");
        popupMenu.DropDownItems.Add(pinItem);
        popupMenu.DropDownItems.Add(ctItem);

        var menu = new WinForms.ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            pinItem.Checked = _popup.IsAlwaysOnTop;
            ctItem.Checked  = _popup.IsClickThrough;
        };
        menu.Items.Add("Show Bansa", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(popupMenu);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) =>
        {
            if (onQuit != null)
                Application.Current.Dispatcher.Invoke(onQuit);
            else
                Application.Current.Shutdown();
        });
        _tray.ContextMenuStrip = menu;

        // Cursor poll every 120ms: if cursor is no longer in the taskbar area
        // or over the popup, fade the popup out.
        _cursorPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _cursorPoll.Tick += OnCursorPoll;

        SettingsManager.Changed += OnSettingsChanged;

        UpdateIcon(0, 0);
    }

    private void OnSettingsChanged()
    {
        _iconSize = ClampSize(App.Settings.TrayIconSize);
        _tray.Visible = App.Settings.ShowTrayIcon;
        UpdateIcon(_lastDown, _lastUp);
    }

    private static int ClampSize(int s) => Math.Max(48, Math.Min(128, s));

    private void OnTrayClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button != WinForms.MouseButtons.Left) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            // Dismiss popup (if open) then open the main window.
            if (_popup.IsVisible) _popup.BeginFadeOut(immediate: true);
            ShowMainWindow();
        });
    }

    private void OnTrayDoubleClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left)
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_popup.IsVisible) _popup.BeginFadeOut(immediate: true);
                ShowMainWindow();
            });
    }

    private void OnTrayMouseMove(object? sender, WinForms.MouseEventArgs e)
    {
        // Capture the exact cursor position now — this point is guaranteed to be inside
        // the icon.  Written before InvokeAsync so it's ready for the timer tick.
        if (GetCursorPos(out var snap)) { _iconX = snap.X; _iconY = snap.Y; }
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_popup.IsVisible)
            {
                // Cursor is back over the tray icon while popup is open — keep it alive.
                _popup.CancelHide();
                return;
            }
            // Start the 200 ms hover timer (idempotent — ignore if already running).
            if (!_hoverTimer.IsEnabled)
                _hoverTimer.Start();
        });
    }

    private void OnCursorPoll(object? sender, EventArgs e)
    {
        // Stop once the popup has fully hidden.
        if (!_popup.IsVisible) { _cursorPoll.Stop(); return; }

        // Grace window: give the user time to move the cursor from the tray icon to the
        // popup after it appears.  During this window we never auto-dismiss.
        if ((DateTime.UtcNow - _popupShowTime).TotalMilliseconds < 500) return;

        if (!GetCursorPos(out var p)) return;

        if (IsCursorOverPopup(p) || IsCursorOverTrayIcon(p))
        {
            // Cursor is over the popup or exactly over the tray icon — keep the popup alive.
            _popup.CancelHide();
        }
        else
        {
            // Cursor left both areas — dismiss without delay.
            _popup.BeginFadeOut(immediate: true);
            // Keep polling; the poll stops itself once IsVisible becomes false.
        }
    }

    /// <summary>Returns true when the cursor (physical px) is inside the popup window.</summary>
    private bool IsCursorOverPopup(POINT p)
    {
        var src = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(_popup).Handle);
        if (src?.CompositionTarget is { } ct)
        {
            double sx = ct.TransformToDevice.M11;
            double sy = ct.TransformToDevice.M22;
            return p.X >= (int)(_popup.Left  * sx) && p.X <= (int)((_popup.Left  + _popup.Width)  * sx)
                && p.Y >= (int)(_popup.Top   * sy) && p.Y <= (int)((_popup.Top   + _popup.Height) * sy);
        }
        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="p"/> is inside a tight hitbox centred on the
    /// last known icon position (captured via <see cref="GetCursorPos"/> at the moment
    /// <see cref="WinForms.NotifyIcon.MouseMove"/> fired — the only time the cursor is
    /// guaranteed to be over the icon).
    ///
    /// HitPad of ±8 physical px keeps the hitbox tight to the icon body (16 px at
    /// 100 % DPI) and well clear of adjacent elements like the overflow chevron.
    /// </summary>
    private bool IsCursorOverTrayIcon(POINT p)
    {
        int ix = _iconX, iy = _iconY;   // snapshot — avoids torn reads of volatile fields
        if (ix == int.MinValue) return false;   // no MouseMove captured yet

        const int HitPad = 5;   // physical pixels in each direction — tight to the icon
        return p.X >= ix - HitPad && p.X <= ix + HitPad
            && p.Y >= iy - HitPad && p.Y <= iy + HitPad;
    }

    private void ShowPopup()
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            // Use saved position if available, else default to bottom-right corner
            double px = App.Settings?.TrayPopupX >= 0 ? App.Settings.TrayPopupX : wa.Right  - 8;
            double py = App.Settings?.TrayPopupY >= 0 ? App.Settings.TrayPopupY : wa.Bottom - 8;
            _popupShowTime = DateTime.UtcNow;   // stamp before Show so grace period starts now
            _popup.ShowAt(px, py);
            _popup.Update(_lastDown, _lastUp, _lastPing, _lastHistory, _lastApps);
            _cursorPoll.Start();
        }
        catch { }
    }

    private void ShowMainWindow()
    {
        if (_ownerWindow is null) return;
        _ownerWindow.Show();
        if (_ownerWindow.WindowState == WindowState.Minimized)
            _ownerWindow.WindowState = WindowState.Normal;
        _ownerWindow.Activate();
        _ownerWindow.Topmost = true; _ownerWindow.Topmost = false;
    }

    public void Update(long totalDownBps, long totalUpBps, int pingMs,
                       IReadOnlyList<(long Down, long Up)> history,
                       IEnumerable<AppRowViewModel> apps)
    {
        _lastDown = totalDownBps; _lastUp = totalUpBps; _lastPing = pingMs;
        _lastHistory = history; _lastApps = apps.ToList();

        UpdateIcon(totalDownBps, totalUpBps);

        if (_popup.IsVisible)
        {
            try { _popup.Update(totalDownBps, totalUpBps, pingMs, history, _lastApps); } catch { }
        }
    }

    // ── Icon rendering — two vertical bars (down=left, up=right) with log-scale heights ──
    // Rendered at 4× slot size for supersampling, then bicubic-downscaled to the tray slot.

    private void UpdateIcon(long downBps, long upBps)
    {
        int slot   = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        int render = slot * 4;

        var downColor = ParseHex(App.Settings?.DownColorHex, Color.FromArgb(255, 93, 173, 226));
        var upColor   = ParseHex(App.Settings?.UpColorHex,   Color.FromArgb(255, 243, 156, 18));

        float dNorm = LogNorm(downBps);
        float uNorm = LogNorm(upBps);

        using var large = new Bitmap(render, render, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(large))
        {
            g.SmoothingMode  = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float pad  = render * 0.07f;
            float bgW  = render - pad * 2;
            float bgH  = render - pad * 2;
            float bgX  = pad;
            float bgY  = pad;
            float rr   = bgH * 0.20f;

            // Dark background
            using var bgBrush = new SolidBrush(Color.FromArgb(210, 18, 18, 18));
            FillRoundedRect(g, bgBrush, bgX, bgY, bgW, bgH, rr);

            // Bar layout: two bars side by side, each ~44% of bgW, 4% gap
            float gap   = bgW * 0.08f;
            float barW  = (bgW - gap * 3f) / 2f;
            float maxBH = bgH - pad * 2f;          // max bar height
            float floor = bgY + bgH - pad;          // baseline (bottom of bars)
            float minBH = maxBH * 0.06f;            // always show a sliver even at 0

            float dH = Math.Max(minBH, dNorm * maxBH);
            float uH = Math.Max(minBH, uNorm * maxBH);

            float dX = bgX + gap;
            float uX = bgX + gap * 2f + barW;

            // Track colours at 80 % opacity for the "trough" (unlit portion)
            using var dTrough = new SolidBrush(Color.FromArgb(50, downColor.R, downColor.G, downColor.B));
            using var uTrough = new SolidBrush(Color.FromArgb(50, upColor.R,   upColor.G,   upColor.B));
            using var dFill   = new SolidBrush(downColor);
            using var uFill   = new SolidBrush(upColor);

            float troughR = barW * 0.25f;

            // Trough (full height, dim)
            FillRoundedRect(g, dTrough, dX, bgY + pad, barW, maxBH, troughR);
            FillRoundedRect(g, uTrough, uX, bgY + pad, barW, maxBH, troughR);

            // Active fill (from bottom)
            FillRoundedRect(g, dFill, dX, floor - dH, barW, dH, troughR);
            FillRoundedRect(g, uFill, uX, floor - uH, barW, uH, troughR);
        }

        using var final = new Bitmap(slot, slot, PixelFormat.Format32bppArgb);
        using (var g2 = Graphics.FromImage(final))
        {
            g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g2.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g2.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g2.DrawImage(large, 0, 0, slot, slot);
        }

        IntPtr hIcon = final.GetHicon();
        try
        {
            _tray.Icon = Icon.FromHandle(hIcon);
            if (_currentIconHandle != IntPtr.Zero) DestroyIcon(_currentIconHandle);
            _currentIconHandle = hIcon;
        }
        catch { }
    }

    // Log10 scale: 0 bps → 0.0, 10 MB/s → 1.0
    private static float LogNorm(long bps)
    {
        const double maxKbps = 10.0 * 1024.0;
        double kbps = bps / 1024.0;
        double v = Math.Log10(kbps + 1.0) / Math.Log10(maxKbps + 1.0);
        return (float)Math.Max(0.0, Math.Min(1.0, v));
    }

    private static void FillRoundedRect(Graphics g, Brush brush, float x, float y, float w, float h, float r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2f);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x,             y,             r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y,             r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2,   0, 90);
        path.AddArc(x,             y + h - r * 2, r * 2, r * 2,  90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static Color ParseHex(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var s = hex.TrimStart('#');
            if (s.Length == 6) return Color.FromArgb(255,
                Convert.ToByte(s.Substring(0, 2), 16),
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch { }
        return fallback;
    }


    public void Dispose()
    {
        try { SettingsManager.Changed -= OnSettingsChanged; } catch { }
        try { _cursorPoll.Stop(); } catch { }
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
        try { _popup.Close(); } catch { }
        if (_currentIconHandle != IntPtr.Zero)
        {
            try { DestroyIcon(_currentIconHandle); } catch { }
            _currentIconHandle = IntPtr.Zero;
        }
    }
}
