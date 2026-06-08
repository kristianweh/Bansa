using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Bansa.Services;

/// <summary>
/// Records a hardware-monitoring session: CPU/GPU temps + loads sampled at the
/// HardwareMonitor cadence (~2 s), each tagged with the foreground app so a thermal
/// spike can be correlated to the game/app that caused it. In-memory for now;
/// exportable to CSV. One active session at a time.
/// </summary>
public static class SessionRecorder
{
    public sealed record Sample(
        DateTime Time, float CpuTemp, float GpuTemp, float CpuLoad, float GpuLoad, string Foreground);

    private static readonly List<Sample> _samples = new();
    private static readonly object _lock = new();

    public static bool IsRecording { get; private set; }
    public static DateTime StartedAt { get; private set; }
    public static DateTime StoppedAt { get; private set; }

    /// <summary>Fires whenever a sample is added (on the polling thread — marshal in the handler).</summary>
    public static event Action? Updated;

    public static int Count { get { lock (_lock) return _samples.Count; } }

    public static void Start()
    {
        lock (_lock) _samples.Clear();
        StartedAt = DateTime.Now;
        IsRecording = true;
        var hm = HardwareMonitor.Instance;
        if (hm is not null) hm.Sampled += OnSampled;
        Updated?.Invoke();
    }

    public static void Stop()
    {
        IsRecording = false;
        StoppedAt = DateTime.Now;
        var hm = HardwareMonitor.Instance;
        if (hm is not null) hm.Sampled -= OnSampled;

        // Auto-persist the finished session so it survives restarts (no-op for < 2 samples).
        List<Sample> snap;
        lock (_lock) snap = new List<Sample>(_samples);
        SessionStore.Save(StartedAt, StoppedAt, snap);

        Updated?.Invoke();
    }

    public static List<Sample> Snapshot()
    {
        lock (_lock) return new List<Sample>(_samples);
    }

    private static void OnSampled(HardwareSnapshot s)
    {
        if (!IsRecording) return;
        var sample = new Sample(DateTime.Now, s.CpuTemp, s.GpuTemp, s.CpuLoad, s.GpuLoad, ForegroundApp());
        lock (_lock) _samples.Add(sample);
        Updated?.Invoke();
    }

    // ── Foreground app (the likely "culprit" for a thermal spike) ───────────────
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private static string ForegroundApp()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return "";
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }
}
