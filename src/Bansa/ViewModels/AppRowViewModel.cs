using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Bansa.Services;

using Point = System.Windows.Point;

namespace Bansa.ViewModels;

/// <summary>
/// Aggregated view for all processes that share the same image name.
/// e.g. all chrome.exe PIDs collapse into one "chrome" row.
/// </summary>
public partial class AppRowViewModel : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string imagePath = "";       // canonical path used for rules
    [ObservableProperty] private long bytesInPerSec;
    [ObservableProperty] private long bytesOutPerSec;
    [ObservableProperty] private long totalBytesIn;
    [ObservableProperty] private long totalBytesOut;
    [ObservableProperty] private int processCount;
    [ObservableProperty] private int connectionCount;
    [ObservableProperty] private bool isBlocked;
    [ObservableProperty] private int uploadLimitKbps;
    [ObservableProperty] private int downloadLimitKbps;
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool isPinned;
    [ObservableProperty] private string hourlyDownText = "";
    [ObservableProperty] private string hourlyUpText   = "";
    [ObservableProperty] private bool isLocalOnly;     // all connections to loopback / RFC-1918
    [ObservableProperty] private bool isThrottlingDown;     // download firewall block currently active
    [ObservableProperty] private bool isUploadCapExceeded;  // upload limit set but measured rate exceeds it (QoS not yet effective)
    [ObservableProperty] private bool hasUdpConnections;    // any active external UDP socket — warn on upload limit

    public string HourlyTooltip =>
        string.IsNullOrEmpty(HourlyDownText)
            ? "Last hour usage not yet loaded…"
            : $"Last hour  ↓ {HourlyDownText}   ↑ {HourlyUpText}";

    partial void OnHourlyDownTextChanged(string value) => OnPropertyChanged(nameof(HourlyTooltip));
    partial void OnHourlyUpTextChanged(string value)   => OnPropertyChanged(nameof(HourlyTooltip));

    /// <summary>
    /// Primary sort key: 0 = pinned (always first), 1 = modified (has limit/block/priority),
    /// 2 = normal. Secondary sort is applied by the user's chosen column.
    /// </summary>
    public int SortPriority
    {
        get
        {
            if (IsPinned) return 0;
            if (IsBlocked || UploadLimitKbps > 0 || DownloadLimitKbps > 0) return 1;
            return 2;
        }
    }

    public ObservableCollection<ProcessRowViewModel> Processes { get; } = new();

    public ImageSource? Icon => AppIconCache.Get(ImagePath);

    // ── Per-app sparklines (last 60 samples = 30 seconds at 500 ms/tick) ────────
    private const int SparkCap = 60;
    private readonly long[] _sparkDown = new long[SparkCap];
    private readonly long[] _sparkUp   = new long[SparkCap];
    private int _sparkHead;
    private int _sparkCount;

    // Tracks how many non-zero entries exist in the ring buffer.
    // Used to skip PropertyChanged — and thus StreamGeometry allocation — for apps
    // that have been idle long enough for their entire 30-second history to be zero.
    // On a 50-app list that's common: avoids ~120 wasted geometry builds per second.
    private int _sparkNonZeroCount;

    public void PushSparkSample(long downBps, long upBps)
    {
        // Capture the entry being overwritten before we replace it.
        long oldDown = _sparkDown[_sparkHead];
        long oldUp   = _sparkUp[_sparkHead];

        _sparkDown[_sparkHead] = downBps;
        _sparkUp[_sparkHead]   = upBps;
        _sparkHead = (_sparkHead + 1) % SparkCap;
        if (_sparkCount < SparkCap) _sparkCount++;

        // Maintain non-zero count: remove old entry's contribution, add new.
        int prevCount = _sparkNonZeroCount;
        if (oldDown > 0 || oldUp > 0) _sparkNonZeroCount--;
        if (downBps > 0 || upBps > 0) _sparkNonZeroCount++;

        // Fire only while the buffer has active data OR on the one tick that clears it.
        // This eliminates 2×/sec PropertyChanged spam for consistently idle apps.
        if (_sparkNonZeroCount > 0 || prevCount > 0)
        {
            OnPropertyChanged(nameof(DownSparkGeometry));
            OnPropertyChanged(nameof(UpSparkGeometry));
        }
    }

    private Geometry BuildSparkGeometry(long[] buffer)
    {
        if (_sparkCount < 2) return Geometry.Empty;
        int start = _sparkCount < SparkCap ? 0 : _sparkHead;
        long peak = 0;
        for (int i = 0; i < _sparkCount; i++)
            peak = Math.Max(peak, buffer[(start + i) % SparkCap]);
        if (peak == 0) return Geometry.Empty;

        const double W = 80, H = 18;
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            for (int i = 0; i < _sparkCount; i++)
            {
                double x = i * (W / (_sparkCount - 1));
                double y = H - (buffer[(start + i) % SparkCap] / (double)peak) * H;
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else        ctx.LineTo(new Point(x, y), true, false);
            }
        }
        sg.Freeze();
        return sg;
    }

    public Geometry DownSparkGeometry => BuildSparkGeometry(_sparkDown);
    public Geometry UpSparkGeometry   => BuildSparkGeometry(_sparkUp);

    public string DownRate => Format.Rate(BytesInPerSec);
    public string UpRate   => Format.Rate(BytesOutPerSec);
    public string TotalDown => Format.Bytes(TotalBytesIn);
    public string TotalUp   => Format.Bytes(TotalBytesOut);
    public string UploadLimitText => Format.KBps(UploadLimitKbps);
    public string DownloadLimitText => Format.KBps(DownloadLimitKbps);

    // True when connection count is suspiciously high (P2P, possible malware, crawler)
    public bool HighConnectionWarning => ConnectionCount > 40;

    partial void OnBytesInPerSecChanged(long value)    { OnPropertyChanged(nameof(DownRate)); }
    partial void OnBytesOutPerSecChanged(long value)   { OnPropertyChanged(nameof(UpRate)); }
    partial void OnTotalBytesInChanged(long value)     { OnPropertyChanged(nameof(TotalDown)); }
    partial void OnTotalBytesOutChanged(long value)    { OnPropertyChanged(nameof(TotalUp)); }
    partial void OnConnectionCountChanged(int value)   { OnPropertyChanged(nameof(HighConnectionWarning)); }
    partial void OnUploadLimitKbpsChanged(int value)   { OnPropertyChanged(nameof(UploadLimitText)); OnPropertyChanged(nameof(SortPriority)); }
    partial void OnDownloadLimitKbpsChanged(int value) { OnPropertyChanged(nameof(DownloadLimitText)); OnPropertyChanged(nameof(SortPriority)); }
    partial void OnIsBlockedChanged(bool value)        { OnPropertyChanged(nameof(SortPriority)); }
    partial void OnIsPinnedChanged(bool value)         { OnPropertyChanged(nameof(SortPriority)); }

    /// <summary>Refresh all unit-dependent display properties.</summary>
    public void RefreshUnits()
    {
        OnPropertyChanged(nameof(DownRate));
        OnPropertyChanged(nameof(UpRate));
        OnPropertyChanged(nameof(TotalDown));
        OnPropertyChanged(nameof(TotalUp));
        OnPropertyChanged(nameof(UploadLimitText));
        OnPropertyChanged(nameof(DownloadLimitText));
        foreach (var p in Processes) p.RefreshUnits();
    }

    // ── Upload-cap (QoS) effectiveness ──────────────────────────────────────────
    // QoS smooth shaping only caps NEW sockets, so a connection open before the limit was set
    // keeps running uncapped until it reconnects — and the kernel gives no feedback. We infer
    // effectiveness from the measured upload rate: a sustained overage means the cap isn't biting
    // yet (the badge goes amber → "applies on reconnect"). Several consecutive over-limit samples
    // (~2 s at 500 ms/tick) are required so a brief burst doesn't flicker the badge.
    private int _upOverCapSamples;
    public void UpdateUploadCapEffectiveness()
    {
        if (UploadLimitKbps <= 0)
        {
            _upOverCapSamples = 0;
            if (IsUploadCapExceeded) IsUploadCapExceeded = false;
            return;
        }
        long limitBytesPerSec = (long)UploadLimitKbps * 1024;
        // 1.25× tolerance absorbs QoS pacing + measurement jitter so a working cap reads as fine.
        bool over = BytesOutPerSec > limitBytesPerSec * 5 / 4;
        if (over) { if (_upOverCapSamples < 6) _upOverCapSamples++; }
        else      _upOverCapSamples = 0;
        bool notEffective = _upOverCapSamples >= 4;
        if (IsUploadCapExceeded != notEffective) IsUploadCapExceeded = notEffective;
    }

    public void UpdateFromChildren(IReadOnlyList<ProcessRowViewModel> children)
    {
        var byPid = Processes.ToDictionary(p => p.Pid);
        var seen = new HashSet<int>();

        foreach (var c in children)
        {
            seen.Add(c.Pid);
            if (byPid.TryGetValue(c.Pid, out var existing))
            {
                existing.Name = c.Name;
                existing.ImagePath = c.ImagePath;
                existing.BytesInPerSec = c.BytesInPerSec;
                existing.BytesOutPerSec = c.BytesOutPerSec;
                existing.TotalBytesIn = c.TotalBytesIn;
                existing.TotalBytesOut = c.TotalBytesOut;
                existing.ConnectionCount = c.ConnectionCount;
            }
            else
            {
                Processes.Add(c);
            }
        }

        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Processes[i].Pid))
                Processes.RemoveAt(i);
        }

        BytesInPerSec  = children.Sum(c => c.BytesInPerSec);
        BytesOutPerSec = children.Sum(c => c.BytesOutPerSec);
        TotalBytesIn   = children.Sum(c => c.TotalBytesIn);
        TotalBytesOut  = children.Sum(c => c.TotalBytesOut);
        ConnectionCount = children.Sum(c => c.ConnectionCount);
        ProcessCount   = children.Count;

        var path = children.Where(c => !string.IsNullOrEmpty(c.ImagePath))
                           .OrderByDescending(c => c.ImagePath.Length)
                           .Select(c => c.ImagePath)
                           .FirstOrDefault();
        if (!string.IsNullOrEmpty(path) && !string.Equals(path, ImagePath))
        {
            ImagePath = path;
            OnPropertyChanged(nameof(Icon));
        }

        PushSparkSample(BytesInPerSec, BytesOutPerSec);
    }
}
