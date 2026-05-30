using System;
using System.Management;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace Bansa.Services;

/// <summary>
/// Point-in-time snapshot of system hardware readings.
/// All fields default to 0 / empty when the sensor is unavailable.
/// </summary>
public sealed record HardwareSnapshot(
    float CpuLoad,         // %  (0–100)
    float CpuTemp,         // °C (0 = unavailable)
    float CpuFreqMHz,      // Average core clock MHz  (0 = unavailable)
    float CpuBoostMHz,     // Max core clock MHz (boost)
    float GpuLoad,         // %
    float GpuTemp,         // °C
    float GpuVramUsedMb,   // MB (0 = unavailable)
    float GpuVramTotalMb,  // MB
    float RamUsedGb,       // GB
    float RamTotalGb,      // GB
    string GpuName,        // "NVIDIA GeForce RTX …" / "AMD Radeon …" / ""
    float GpuCoreMHz,      // GPU core clock  MHz (0 = unavailable)
    float GpuMemMHz,       // GPU memory clock MHz
    float GpuFanRpm,       // Fan speed RPM    (0 = unavailable / passive)
    float GpuFanPct,       // Fan duty cycle % (0 = unavailable)
    float GpuPowerW,       // GPU power draw watts (0 = unavailable)
    float RamSpeedMHz,     // RAM clock speed MHz (0 = unavailable)
    string CpuName,        // e.g. "AMD Ryzen 9 7950X" / ""
    string MotherboardName,// e.g. "ROG STRIX B550-F GAMING" / ""
    string BiosVersion)    // e.g. "F13  ·  03/14/2023" / ""
{
    public static readonly HardwareSnapshot Empty =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", 0, 0, 0, 0, 0, 0, "", "", "");

    public float RamPct => RamTotalGb > 0 ? RamUsedGb / RamTotalGb * 100f : 0;
}

/// <summary>
/// Thin wrapper around LibreHardwareMonitor that polls CPU / GPU / RAM sensors
/// every 2 seconds on a background thread.
///
/// Initialization is deferred to a ThreadPool thread so startup is never blocked.
/// All public members are thread-safe.  Subscribe to <see cref="Sampled"/> from
/// any thread; the event fires on the polling thread — marshal to the UI thread
/// yourself (e.g. Dispatcher.InvokeAsync).
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    // ── Singleton ──────────────────────────────────────────────────────────────
    private static HardwareMonitor? _instance;
    public  static HardwareMonitor? Instance => _instance;

    /// <summary>
    /// Start the singleton monitor.  Safe to call multiple times — returns the
    /// existing instance on subsequent calls.
    /// </summary>
    public static HardwareMonitor Start()
    {
        if (_instance is null)
        {
            var m = new HardwareMonitor();
            Interlocked.CompareExchange(ref _instance, m, null);
            if (_instance != m) m.Dispose();   // lost the race, discard
        }
        return _instance!;
    }

    public static void StopInstance()
    {
        var m = Interlocked.Exchange(ref _instance, null);
        m?.Dispose();
    }

    // ── State ──────────────────────────────────────────────────────────────────
    public HardwareSnapshot Latest { get; private set; } = HardwareSnapshot.Empty;

    /// <summary>Fires on the polling thread every ~2 s.</summary>
    public event Action<HardwareSnapshot>? Sampled;

    private Computer?   _computer;
    private System.Threading.Timer? _timer;
    private volatile bool _ready;
    private volatile bool _disposed;

    // Static system info — captured once and carried in every snapshot.
    private string _cpuName         = "";
    private string _motherboardName = "";
    private string _biosVersion     = "";
    private float  _ramSpeedMhz     = 0;   // read once from WMI (SMBIOS/SPD)

    // ── Construction ───────────────────────────────────────────────────────────
    private HardwareMonitor()
    {
        // Open LibreHardwareMonitor on a background thread — Computer.Open()
        // enumerates SMBus and MSR devices which can take ~200 ms on first call.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                // Read BIOS version from registry (static, no driver needed)
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        @"HARDWARE\DESCRIPTION\System\BIOS");
                    if (key != null)
                    {
                        object? ver  = key.GetValue("BIOSVersion");
                        string? date = key.GetValue("BIOSReleaseDate")?.ToString()?.Trim();
                        string verStr = ver is string[] arr
                            ? string.Join(" ", arr).Trim()
                            : (ver?.ToString()?.Trim() ?? "");
                        _biosVersion = string.IsNullOrEmpty(date)
                            ? verStr
                            : $"{verStr}  ·  {date}";
                    }
                }
                catch { /* non-admin or VM — leave empty */ }

                // Read RAM speed from WMI (SMBIOS / SPD data).
                // LHM's Memory hardware does not expose a Clock sensor for DDR speed.
                //
                // WMI exposes two fields:
                //   Speed               — rated module speed (MT/s) as stamped on the DIMM
                //   ConfiguredClockSpeed — actual running speed configured by the BIOS
                //
                // Some BIOSes report ConfiguredClockSpeed as the physical clock (e.g. 3000
                // for DDR5-6000) while others report the transfer rate (6000). We take the
                // MAX of both fields so the correct value wins regardless of BIOS behaviour.
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
                    float maxMhz = 0;
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        float s  = 0, cc = 0;
                        try { s  = Convert.ToSingle(obj["Speed"]               ?? 0); } catch { }
                        try { cc = Convert.ToSingle(obj["ConfiguredClockSpeed"] ?? 0); } catch { }
                        float eff = Math.Max(s, cc);
                        if (eff > maxMhz) maxMhz = eff;
                    }
                    if (maxMhz > 0) _ramSpeedMhz = maxMhz;
                }
                catch { /* WMI unavailable on this machine */ }

                _computer = new Computer
                {
                    IsCpuEnabled         = true,
                    IsGpuEnabled         = true,
                    IsMemoryEnabled      = true,
                    IsMotherboardEnabled = true,   // for motherboard name
                    // Disable sensors we don't need to keep polling fast
                    IsStorageEnabled    = false,
                    IsControllerEnabled = false,
                    IsNetworkEnabled    = false,
                    IsPsuEnabled        = false,
                };
                _computer.Open();
                _ready = true;

                // Poll immediately, then every 2 s
                _timer = new System.Threading.Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Some machines deny access to MSR / SMBus even as admin.
                // Hardware monitoring will be silent (Latest stays Empty).
            }
        });
    }

    // ── Polling ────────────────────────────────────────────────────────────────
    private void Poll(object? _)
    {
        if (!_ready || _disposed || _computer is null) return;

        try
        {
            float cpuLoad = 0, cpuTemp = 0, cpuFreqMhz = 0, cpuBoostMhz = 0;
            float gpuLoad = 0, gpuTemp = 0, vramUsed = 0, vramTotal = 0;
            float gpuCoreMhz = 0, gpuMemMhz = 0, gpuFanRpm = 0, gpuFanPct = 0, gpuPowerW = 0;
            float ramUsed = 0, ramTotal = 0, ramSpeedMhz = 0;
            string gpuName = "";
            bool hasNvidiaGpu   = false;   // NVIDIA dGPU always wins
            bool hasFallbackGpu = false;   // AMD iGPU / Intel iGPU — used only when no NVIDIA

            foreach (var hw in _computer.Hardware)
            {
                if (_disposed) return;
                hw.Update();

                switch (hw.HardwareType)
                {
                    // ── CPU ────────────────────────────────────────────────────
                    case HardwareType.Cpu:
                    {
                        if (_cpuName.Length == 0) _cpuName = hw.Name;   // capture once
                        bool hasPkg = false;
                        var coreClocks = new System.Collections.Generic.List<float>();
                        foreach (var s in hw.Sensors)
                        {
                            float v = s.Value ?? 0;

                            if (s.SensorType == SensorType.Load &&
                                s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                                cpuLoad = v;

                            if (s.SensorType == SensorType.Temperature)
                            {
                                if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                    s.Name.Contains("Tdie",    StringComparison.OrdinalIgnoreCase))
                                { cpuTemp = v; hasPkg = true; }
                                else if (!hasPkg && v > cpuTemp)
                                    cpuTemp = v;   // fall back to hottest individual core
                            }

                            // CPU clocks — collect each core clock for avg + track max for boost
                            if (s.SensorType == SensorType.Clock && v > 100 &&
                                !s.Name.Contains("Bus",    StringComparison.OrdinalIgnoreCase) &&
                                !s.Name.Contains("Ring",   StringComparison.OrdinalIgnoreCase) &&
                                !s.Name.Contains("Uncore", StringComparison.OrdinalIgnoreCase))
                            {
                                coreClocks.Add(v);
                                if (v > cpuBoostMhz) cpuBoostMhz = v;
                            }
                        }
                        if (coreClocks.Count > 0)
                            cpuFreqMhz = coreClocks.Sum() / coreClocks.Count;  // average

                        // Also walk sub-hardware (AMD Ryzen chiplets)
                        foreach (var sub in hw.SubHardware)
                        {
                            sub.Update();
                            foreach (var s in sub.Sensors)
                            {
                                float v = s.Value ?? 0;
                                if (s.SensorType == SensorType.Load &&
                                    s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) &&
                                    cpuLoad == 0)
                                    cpuLoad = v;
                                if (s.SensorType == SensorType.Temperature && !hasPkg && v > cpuTemp)
                                    cpuTemp = v;
                                if (s.SensorType == SensorType.Clock && v > 100 &&
                                    !s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
                                {
                                    coreClocks.Add(v);
                                    if (v > cpuBoostMhz) cpuBoostMhz = v;
                                }
                            }
                        }
                        // Recalculate avg if sub-hardware added more clocks
                        if (coreClocks.Count > 0)
                            cpuFreqMhz = coreClocks.Sum() / coreClocks.Count;
                        break;
                    }

                    // ── NVIDIA dGPU — always wins, overrides any previously captured AMD/Intel ──
                    case HardwareType.GpuNvidia:
                    {
                        hasNvidiaGpu = true;
                        // Reset in case AMD iGPU was captured first
                        gpuName = hw.Name;
                        gpuLoad = gpuTemp = vramUsed = vramTotal = 0;
                        gpuCoreMhz = gpuMemMhz = gpuFanRpm = gpuFanPct = gpuPowerW = 0;
                        ReadGpuSensors(hw,
                            ref gpuLoad, ref gpuTemp,
                            ref vramUsed, ref vramTotal,
                            ref gpuCoreMhz, ref gpuMemMhz,
                            ref gpuFanRpm, ref gpuFanPct,
                            ref gpuPowerW);
                        break;
                    }

                    // ── AMD GPU — could be iGPU or dGPU; capture only when no NVIDIA found ──
                    case HardwareType.GpuAmd:
                    {
                        if (hasNvidiaGpu)   break; // NVIDIA already captured — skip AMD iGPU
                        if (hasFallbackGpu) break; // already have an AMD fallback
                        hasFallbackGpu = true;
                        gpuName = hw.Name;
                        gpuLoad = gpuTemp = vramUsed = vramTotal = 0;
                        gpuCoreMhz = gpuMemMhz = gpuFanRpm = gpuFanPct = gpuPowerW = 0;
                        ReadGpuSensors(hw,
                            ref gpuLoad, ref gpuTemp,
                            ref vramUsed, ref vramTotal,
                            ref gpuCoreMhz, ref gpuMemMhz,
                            ref gpuFanRpm, ref gpuFanPct,
                            ref gpuPowerW);
                        break;
                    }

                    // ── Motherboard — name only, no sensor reads needed ─────────
                    case HardwareType.Motherboard:
                    {
                        if (_motherboardName.Length == 0) _motherboardName = hw.Name;
                        break;
                    }

                    // ── Intel iGPU — lowest priority fallback ───────────────────
                    case HardwareType.GpuIntel:
                    {
                        if (hasNvidiaGpu || hasFallbackGpu) break; // better GPU already found
                        gpuName = hw.Name;
                        ReadGpuSensors(hw,
                            ref gpuLoad, ref gpuTemp,
                            ref vramUsed, ref vramTotal,
                            ref gpuCoreMhz, ref gpuMemMhz,
                            ref gpuFanRpm, ref gpuFanPct,
                            ref gpuPowerW);
                        break;
                    }

                    // ── System memory ──────────────────────────────────────────
                    case HardwareType.Memory:
                    {
                        float avail = 0;
                        foreach (var s in hw.Sensors)
                        {
                            float v = s.Value ?? 0;
                            if (s.SensorType == SensorType.Data &&
                                !s.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                            {
                                if (s.Name.Contains("Used",      StringComparison.OrdinalIgnoreCase)) ramUsed = v;
                                if (s.Name.Contains("Available", StringComparison.OrdinalIgnoreCase)) avail   = v;
                            }
                            // RAM speed — LHM reports memory clock as SensorType.Clock
                            if (s.SensorType == SensorType.Clock && v > 100 && ramSpeedMhz == 0)
                                ramSpeedMhz = v;
                        }
                        ramTotal = ramUsed + avail;
                        break;
                    }
                }
            }

            var snap = new HardwareSnapshot(
                cpuLoad, cpuTemp, cpuFreqMhz, cpuBoostMhz,
                gpuLoad, gpuTemp, vramUsed, vramTotal,
                ramUsed, ramTotal,
                gpuName,
                gpuCoreMhz, gpuMemMhz, gpuFanRpm, gpuFanPct,
                gpuPowerW, ramSpeedMhz > 0 ? ramSpeedMhz : _ramSpeedMhz,
                _cpuName, _motherboardName, _biosVersion);

            Latest = snap;
            Sampled?.Invoke(snap);
        }
        catch { /* best effort — sensor reads can fail transiently */ }
    }

    // ── GPU sensor reader (shared between dedicated and integrated paths) ─────
    private static void ReadGpuSensors(
        IHardware hw,
        ref float load, ref float temp,
        ref float vramUsed, ref float vramTotal,
        ref float coreMhz, ref float memMhz,
        ref float fanRpm, ref float fanPct,
        ref float powerW)
    {
        foreach (var s in hw.Sensors)
        {
            float v = s.Value ?? 0;

            if (s.SensorType == SensorType.Load &&
                s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && load == 0)
                load = v;

            if (s.SensorType == SensorType.Temperature &&
                (s.Name.Contains("Core",    StringComparison.OrdinalIgnoreCase) ||
                 s.Name.Contains("GPU",     StringComparison.OrdinalIgnoreCase) ||
                 s.Name.Contains("Hot Spot",StringComparison.OrdinalIgnoreCase)) &&
                temp == 0)
                temp = v;

            // VRAM — LHM reports in MB under SensorType.SmallData
            if (s.SensorType == SensorType.SmallData &&
                s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            {
                if (s.Name.Contains("Used",  StringComparison.OrdinalIgnoreCase) && vramUsed  == 0) vramUsed  = v;
                if (s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) && vramTotal == 0) vramTotal = v;
            }

            // GPU clocks
            if (s.SensorType == SensorType.Clock)
            {
                if (s.Name.Contains("Core",   StringComparison.OrdinalIgnoreCase) && coreMhz == 0) coreMhz = v;
                if (s.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase) && memMhz  == 0) memMhz  = v;
            }

            // Fan
            if (s.SensorType == SensorType.Fan     && fanRpm == 0) fanRpm = v;
            if (s.SensorType == SensorType.Control && fanPct == 0) fanPct = v;

            // Power draw — LHM reports GPU package/board power under SensorType.Power
            if (s.SensorType == SensorType.Power && powerW == 0) powerW = v;
        }
    }

    // ── Disposal ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        try { _computer?.Close(); } catch { }
    }
}
