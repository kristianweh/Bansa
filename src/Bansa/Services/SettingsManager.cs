using System;
using System.IO;
using System.Text.Json;

namespace Bansa.Services;

public enum RateUnit { Bytes, Bits }

/// <summary>A named bandwidth-limit preset that can be applied from the limit window.</summary>
public class LimitProfile
{
    public string Name        { get; set; } = "";
    public int    UploadKbps  { get; set; } = 0;
    public int    DownloadKbps{ get; set; } = 0;
}

public class BansaSettings
{
    public string Theme { get; set; } = "Dark";          // "Dark" or "Light"
    public double HideBelowKBps { get; set; } = 0;
    public bool HideIdleApps { get; set; } = false;
    public bool HideLocalOnlyApps { get; set; } = false;  // hide apps whose all connections are loopback/LAN
    public bool StartMinimizedToTray { get; set; } = false;
    public bool ShowTrayIcon { get; set; } = true;
    public string PingTarget { get; set; } = "8.8.8.8";
    public List<string> PingTargets { get; set; } = new() { "8.8.8.8", "1.1.1.1", "8.8.4.4" };
    public string RateUnit { get; set; } = "Bytes";      // "Bytes" or "Bits"
    public int TrayIconSize { get; set; } = 96;          // px; rendered at this resolution and Windows downscales
    public bool UseWindowsAccent { get; set; } = true;   // chart + accent follow OS accent
    public string DownColorHex { get; set; } = "#5DADE2";
    public string UpColorHex   { get; set; } = "#F39C12";
    public string TrayDownColorHex { get; set; } = "#5DADE2";
    public string TrayUpColorHex   { get; set; } = "#F39C12";

    // ── Global upload cap ────────────────────────────────────────────────────
    // A system-wide QoS ceiling (no app filter) applied by Gaming Mode.
    // 0 = disabled. Keep at ~80% of your line's max upload to prevent bufferbloat.
    public int GlobalUploadCapKBs { get; set; } = 0;
    public bool GamingModeActive  { get; set; } = false;

    // ── Ping target labels ───────────────────────────────────────────────────
    // Optional display name for each ping target.  Key = IP/hostname (case-insensitive).
    public Dictionary<string, string> PingTargetLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Per-app limit persistence ─────────────────────────────────────────────
    // Keyed by image path (lowercased). Restored on startup when the app is detected.
    // All OS rules (QoS, firewall) are torn down on exit and re-applied on startup,
    // so limits are only active while Bansa is running.
    public Dictionary<string, int> AppUploadLimitsKBs   { get; set; } = new();
    public Dictionary<string, int> AppDownloadLimitsKBs { get; set; } = new();
    // Blocked app image paths (lowercased). Firewall rules re-created on startup.
    public HashSet<string> AppBlockedPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Paths of apps pinned to the top of the process list (lowercased).
    public List<string> PinnedAppPaths { get; set; } = new();
    // Apps marked as high-priority (DSCP 46). Re-applied on startup.
    public HashSet<string> AppHighPriorityPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Name-only fallback for protected processes (e.g. Valorant) where ImagePath is empty.
    public HashSet<string> AppHighPriorityNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Global hotkey ─────────────────────────────────────────────────────────
    // Virtual-key code for the Ctrl+Shift+? hotkey. 0x46='F' default. 0 = no hotkey (cleared by user).
    public int HotkeyVirtualKey { get; set; } = 0x46;

    // ── Connection speed (used for limit suggestions) ─────────────────────────
    /// <summary>User's ISP upload speed in Mbps. 0 = not configured.</summary>
    public int ConnectionUploadMbps   { get; set; } = 0;
    /// <summary>User's ISP download speed in Mbps. 0 = not configured.</summary>
    public int ConnectionDownloadMbps { get; set; } = 0;

    // ── Named limit profiles ──────────────────────────────────────────────────
    public List<LimitProfile> LimitProfiles { get; set; } = new();

    // ── App grid sort persistence ─────────────────────────────────────────────
    public string AppSortMemberPath  { get; set; } = "BytesInPerSec";
    public bool   AppSortDescending  { get; set; } = true;

    // ── Floating graph window ─────────────────────────────────────────────────
    public bool   ShowFloatingGraph  { get; set; } = false;
    public bool   ShowHardwarePanel  { get; set; } = true;    // SYS panel in float graph
    public double FloatGraphX        { get; set; } = -1;      // -1 = auto-position
    public double FloatGraphY        { get; set; } = -1;
    public double FloatGraphW        { get; set; } = 340;
    public double FloatGraphH        { get; set; } = 240;      // slightly taller default for apps
    public bool   FloatGraphTopmost  { get; set; } = true;

    // ── Network chart ─────────────────────────────────────────────────────────
    /// <summary>When true, download and upload use independent Y-axis scales.</summary>
    public bool DualScale { get; set; } = false;

    // ── Tray hover popup ──────────────────────────────────────────────────────
    /// <summary>When true, the tray hover popup passes mouse events to windows underneath.</summary>
    public bool TrayClickThrough { get; set; } = false;
}

public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(App.DataFolder, "settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static event Action? Changed;

    public static BansaSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<BansaSettings>(json) ?? new BansaSettings();
            }
        }
        catch { /* fall through to defaults */ }
        return new BansaSettings();
    }

    public static void Save(BansaSettings settings)
    {
        try
        {
            Directory.CreateDirectory(App.DataFolder);
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(SettingsPath, json);
            try { Changed?.Invoke(); } catch { }
        }
        catch { /* best effort */ }
    }

    public static RateUnit GetRateUnit()
    {
        if (App.Settings is null) return RateUnit.Bytes;
        return App.Settings.RateUnit.Equals("Bits", StringComparison.OrdinalIgnoreCase)
            ? RateUnit.Bits
            : RateUnit.Bytes;
    }
}
