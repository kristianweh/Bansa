using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

// Pin Color to WPF's Media variant (System.Drawing.Color leaks in via UseWindowsForms).
using Color = System.Windows.Media.Color;

namespace Flow.Services;

/// <summary>
/// Reads the current Windows accent color (the one used by the taskbar / Start menu /
/// title bars) so Flow's accent matches the rest of the OS.
/// </summary>
public static class WindowsAccent
{
    /// <summary>Returns the system accent, or a sensible fallback.</summary>
    public static Color Get()
    {
        try
        {
            // The most reliable source on Win10/11 is HKCU\SOFTWARE\Microsoft\Windows\DWM\AccentColor (DWORD ABGR).
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                // The value is 0xAABBGGRR (alpha-blue-green-red)
                byte a = (byte)((abgr >> 24) & 0xFF);
                byte b = (byte)((abgr >> 16) & 0xFF);
                byte g = (byte)((abgr >> 8) & 0xFF);
                byte r = (byte)(abgr & 0xFF);
                if (a == 0) a = 255;
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch { }

        // WPF fallback (less reliable, but works on most setups)
        try { return SystemParameters.WindowGlassColor; } catch { }

        return Color.FromRgb(0x58, 0x65, 0xF2);
    }

    /// <summary>Returns true on Windows 11 or newer (build 22000+).</summary>
    public static bool IsWindows11OrLater()
    {
        var v = Environment.OSVersion.Version;
        return v.Major >= 10 && v.Build >= 22000;
    }

    /// <summary>
    /// Apply Win11 Mica backdrop to a window. No-op on Win10.
    /// </summary>
    public static void TryApplyMica(IntPtr hwnd, bool dark)
    {
        if (!IsWindows11OrLater()) return;

        try
        {
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 — dark title bar / system chrome
            int useDark = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int));

            // DWMWA_SYSTEMBACKDROP_TYPE = 38, value 2 = Mica
            // NOTE: Mica backdrop changes how DWM composites the window and is
            // known to break PrintScreen (OS-level screenshots) when the window
            // is focused.  Skip it — the dark background from the theme already
            // looks correct without the wallpaper-bleed effect.
            // int backdrop = 2;
            // DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));

            // DWMWA_WINDOW_CORNER_PREFERENCE = 33, value 2 = Round corners
            int corner = 2;
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        }
        catch { /* older Win11 builds may not support all flags */ }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
