using System;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Bansa.Services;

/// <summary>
/// Shared temperature color used by every CPU/GPU temperature readout (Hardware tab,
/// sidebar STATUS, float HUD, tray popup, Hardware history).
///
/// Colors are <b>flat bands</b>, not a full gradient — so there are no muddy in-between
/// tones across the bulk of the range. Two user-defined thresholds split the range into
/// three bands, each with its own configurable flat color (Settings → Appearance):
///   cool: &lt; warm°C   ·   warm: warm…hot°C   ·   hot: ≥ hot°C
/// The only softening is a narrow <see cref="BlendC"/>-wide eased window centered on each
/// threshold, so a value crossing a boundary doesn't visibly snap. Everywhere else the
/// color is a pure flat band.
/// </summary>
public static class HeatColors
{
    /// <summary>Total width (°C) of the eased transition centered on each threshold.</summary>
    private const double BlendC = 3.0;

    /// <summary>Maps a temperature to its band color, with a small eased window at each boundary.</summary>
    public static Color Temp(double tempC)
    {
        var s = App.Settings;
        int warm = s?.TempWarmThresholdC ?? 60;
        int hot  = s?.TempHotThresholdC  ?? 80;
        if (hot <= warm) hot = warm + 1;   // guard against inverted thresholds

        Color cool  = Parse(s?.TempBandCoolColorHex, Color.FromRgb(0x36, 0xBF, 0xFA));
        Color warmC = Parse(s?.TempBandWarmColorHex, Color.FromRgb(0xFF, 0xD6, 0x0A));
        Color hotC  = Parse(s?.TempBandHotColorHex,  Color.FromRgb(0xFF, 0x3B, 0x30));

        // Half-width of each eased window, clamped so adjacent windows never overlap
        // (matters when the warm band is very narrow).
        double hb = Math.Min(BlendC / 2.0, (hot - warm) / 2.0);

        // Cool → warm, eased across [warm-hb, warm+hb]
        if (tempC <= warm - hb) return cool;
        if (tempC <  warm + hb) return Lerp(cool, warmC, (tempC - (warm - hb)) / (2 * hb));
        // Warm → hot, eased across [hot-hb, hot+hb]
        if (tempC <= hot - hb)  return warmC;
        if (tempC <  hot + hb)  return Lerp(warmC, hotC, (tempC - (hot - hb)) / (2 * hb));
        return hotC;
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }
}
