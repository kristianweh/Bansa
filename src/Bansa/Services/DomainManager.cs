using System;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;

namespace Bansa.Services;

public enum AppDomainMode { Network, Hardware }

/// <summary>
/// Drives the per-domain accent (Network = cyan/teal, Hardware = thermal amber→red).
/// Writes the accent keys directly onto the root Application resource dictionary, which
/// outranks the merged theme dict, so every DynamicResource consumer re-skins instantly.
/// Palette is theme-aware (Dark vs Light) and re-applies whenever the theme changes.
/// </summary>
public static class DomainManager
{
    public static AppDomainMode Current { get; private set; } = AppDomainMode.Network;

    public static event Action<AppDomainMode>? DomainChanged;

    private static bool _hookedTheme;

    public static void Apply(AppDomainMode domain)
    {
        Current = domain;
        ApplyPalette();

        if (!_hookedTheme)
        {
            ThemeManager.ThemeChanged += _ => ApplyPalette();
            _hookedTheme = true;
        }

        DomainChanged?.Invoke(domain);
    }

    public static void Toggle()
        => Apply(Current == AppDomainMode.Network ? AppDomainMode.Hardware : AppDomainMode.Network);

    private static void ApplyPalette()
    {
        var app = Application.Current;
        if (app is null) return;

        bool dark = ThemeManager.Current == AppTheme.Dark;

        // Dominant accent is user-chosen per domain (dark-tuned base). On light theme we
        // darken it modestly so it stays readable on white without going muddy. The
        // gradient terminus / hover / nav tint are all derived from that one color.
        string baseHex = Current == AppDomainMode.Network
            ? Bansa.App.Settings.NetworkColorHex
            : Bansa.App.Settings.HardwareColorHex;
        Color baseColor = Hex(baseHex);

        Color accent = dark ? baseColor : Darken(baseColor, 0.16);
        Color alt    = Darken(accent, 0.18);
        Color hover  = Lighten(accent, 0.12);
        Color muted  = dark ? MixToward(accent, Hex("#0B0C11"), 0.86)
                            : MixToward(accent, Hex("#FFFFFF"), 0.86);

        // "Light" accent for selected-tab text — a lighter tint than the accent pill on
        // dark theme (e.g. light blue on a dark blue highlight). On light theme the base
        // accent already has enough contrast against the pale pill, so keep it as-is.
        Color light = dark ? Lighten(accent, 0.45) : accent;

        var res = app.Resources;
        res["AccentColor"]      = accent;
        res["AccentAltColor"]   = alt;
        res["AccentHoverColor"] = hover;
        res["AccentBrush"]      = new SolidColorBrush(accent);
        res["AccentHoverBrush"] = new SolidColorBrush(hover);
        res["AccentLightBrush"] = new SolidColorBrush(light);
        res["AccentMutedBrush"] = new SolidColorBrush(muted);
        res["AccentGradientBrush"]         = Grad(accent, alt, horizontal: true);
        res["AccentGradientVerticalBrush"] = Grad(accent, alt, horizontal: false);
    }

    private static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    private static Color Darken(Color c, double t)  => Lerp(c, Color.FromRgb(0, 0, 0), t);
    private static Color Lighten(Color c, double t) => Lerp(c, Color.FromRgb(255, 255, 255), t);
    private static Color MixToward(Color c, Color target, double t) => Lerp(c, target, t);

    private static Color Lerp(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    private static LinearGradientBrush Grad(Color a, Color b, bool horizontal)
    {
        var g = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint   = horizontal ? new Point(1, 0) : new Point(0, 1)
        };
        g.GradientStops.Add(new GradientStop(a, 0));
        g.GradientStops.Add(new GradientStop(b, 1));
        return g;
    }
}
