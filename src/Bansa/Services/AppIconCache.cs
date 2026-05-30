using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bansa.Services;

/// <summary>
/// Caches WPF ImageSources for each executable's icon so the popup
/// list doesn't repeatedly extract icons from disk.
/// Capped at MaxEntries to keep memory bounded on machines with many processes.
/// </summary>
public static class AppIconCache
{
    private const int MaxEntries = 512;

    private static readonly ConcurrentDictionary<string, ImageSource?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        if (_cache.TryGetValue(exePath, out var cached)) return cached;

        // Evict when full — drop the whole cache (simple, avoids LRU overhead)
        if (_cache.Count >= MaxEntries) _cache.Clear();

        ImageSource? result = null;
        try
        {
            if (File.Exists(exePath))
            {
                using var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon != null)
                {
                    result = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    if (result.CanFreeze) result.Freeze();
                }
            }
        }
        catch { /* fall through to null */ }

        _cache[exePath] = result;
        return result;
    }

    /// <summary>Force-clear the cache (e.g. when theme changes).</summary>
    public static void Clear() => _cache.Clear();
}
