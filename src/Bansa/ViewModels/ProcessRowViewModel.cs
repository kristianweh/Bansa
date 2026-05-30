using CommunityToolkit.Mvvm.ComponentModel;
using Bansa.Services;

namespace Bansa.ViewModels;

/// <summary>
/// One individual process instance (single PID).
/// </summary>
public partial class ProcessRowViewModel : ObservableObject
{
    [ObservableProperty] private int pid;
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string imagePath = "";
    [ObservableProperty] private long bytesInPerSec;
    [ObservableProperty] private long bytesOutPerSec;
    [ObservableProperty] private long totalBytesIn;
    [ObservableProperty] private long totalBytesOut;
    [ObservableProperty] private int connectionCount;

    public string DownRate => Format.Rate(BytesInPerSec);
    public string UpRate   => Format.Rate(BytesOutPerSec);
    public string TotalDown => Format.Bytes(TotalBytesIn);
    public string TotalUp   => Format.Bytes(TotalBytesOut);

    partial void OnBytesInPerSecChanged(long value)   { OnPropertyChanged(nameof(DownRate)); }
    partial void OnBytesOutPerSecChanged(long value)  { OnPropertyChanged(nameof(UpRate)); }
    partial void OnTotalBytesInChanged(long value)    { OnPropertyChanged(nameof(TotalDown)); }
    partial void OnTotalBytesOutChanged(long value)   { OnPropertyChanged(nameof(TotalUp)); }

    /// <summary>Refresh all display properties — call when unit setting changes.</summary>
    public void RefreshUnits()
    {
        OnPropertyChanged(nameof(DownRate));
        OnPropertyChanged(nameof(UpRate));
        OnPropertyChanged(nameof(TotalDown));
        OnPropertyChanged(nameof(TotalUp));
    }
}

/// <summary>
/// Unit-aware byte/rate formatter. Returns text in either
/// SI bytes (B / KB / MB / GB) or bits (b / Kb / Mb / Gb)
/// based on the current BansaSettings.RateUnit.
/// </summary>
public static class Format
{
    public static string Bytes(long bytes)
    {
        // Totals: always bytes-based (showing 100 GB total is more intuitive than 800 Gb)
        // but if user prefers bits, honor that for consistency.
        if (SettingsManager.GetRateUnit() == RateUnit.Bits)
            return FormatScaled(bytes * 8, new[] { "b", "Kb", "Mb", "Gb", "Tb" });
        return FormatScaled(bytes, new[] { "B", "KB", "MB", "GB", "TB" });
    }

    public static string Rate(long bytesPerSec)
    {
        if (SettingsManager.GetRateUnit() == RateUnit.Bits)
            return FormatScaled(bytesPerSec * 8, new[] { "b", "Kb", "Mb", "Gb" }) + "/s";
        return FormatScaled(bytesPerSec, new[] { "B", "KB", "MB", "GB" }) + "/s";
    }

    /// <summary>Compact form used by the tray icon image — fits in tight space.</summary>
    public static string ShortRate(long bytesPerSec)
    {
        bool bits = SettingsManager.GetRateUnit() == RateUnit.Bits;
        double v = bits ? bytesPerSec * 8.0 : bytesPerSec;
        var unit = bits ? "b" : "B";
        if (v < 1000) return $"{v:0}{unit}";
        v /= 1000;
        if (v < 1000) return $"{v:0}K";
        v /= 1000;
        if (v < 100) return $"{v:0.#}M";
        return $"{v:0}M";
    }

    /// <summary>Format a limit in KB/s for display. Always shows bytes (limits are set in KB/s).</summary>
    public static string KBps(int kbs)
    {
        if (kbs <= 0) return "unlimited";
        if (kbs >= 1024) return $"{kbs / 1024.0:0.##} MB/s";
        return $"{kbs} KB/s";
    }

    private static string FormatScaled(double v, string[] units)
    {
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.##} {units[u]}";
    }
}
