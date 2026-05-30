using OpenRGB.NET;
using System.Diagnostics;
using System.IO;
using WpfColor = System.Windows.Media.Color;
using RgbColor = OpenRGB.NET.Color;

namespace Flow.Services;

public enum OpenRgbConnectionState { Disconnected, Connecting, Connected, Error }

public sealed class OpenRgbService : IDisposable
{
    private OpenRgbClient? _client;
    private Process?       _managedProcess;
    private bool           _disposed;

    public OpenRgbConnectionState State     { get; private set; } = OpenRgbConnectionState.Disconnected;
    public string                 StateText { get; private set; } = "Not connected";
    public IReadOnlyList<Device>  Devices   { get; private set; } = [];
    public bool IsConnected => State == OpenRgbConnectionState.Connected;

    public event Action? StateChanged;

    // ── Path resolution ───────────────────────────────────────────────────────

    public static string? FindOpenRgbExe()
    {
        var toolsPath = Path.Combine(App.DataFolder, "Tools", "OpenRGB.exe");
        if (File.Exists(toolsPath)) return toolsPath;

        foreach (var candidate in CommonInstallPaths())
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    private static IEnumerable<string> CommonInstallPaths()
    {
        var pf  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(pf,  "OpenRGB", "OpenRGB.exe");
        yield return Path.Combine(pfx, "OpenRGB", "OpenRGB.exe");
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task ConnectAsync(string host = "localhost", int port = 6742)
    {
        if (IsConnected) return;

        SetState(OpenRgbConnectionState.Connecting, "Connecting…");

        if (await TryConnectClientAsync(host, port)) return;

        var exe = FindOpenRgbExe();
        if (exe is null)
        {
            SetState(OpenRgbConnectionState.Error, "OpenRGB.exe not found");
            return;
        }

        LaunchServer(exe, port);

        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500);
            if (await TryConnectClientAsync(host, port)) return;
        }

        SetState(OpenRgbConnectionState.Error, "Server started but connection timed out");
    }

    public void Disconnect()
    {
        DisposeClient();
        SetState(OpenRgbConnectionState.Disconnected, "Not connected");
        Devices = [];
    }

    public static async Task ImportPortableExeAsync(string sourcePath)
    {
        var dest = Path.Combine(App.DataFolder, "Tools", "OpenRGB.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await Task.Run(() => File.Copy(sourcePath, dest, overwrite: true));
    }

    // ── Device control ────────────────────────────────────────────────────────

    public async Task SetAllColorsAsync(WpfColor color)
    {
        if (_client is null || !IsConnected) return;
        var c = ToRgbColor(color);
        await Task.Run(() =>
        {
            for (int i = 0; i < Devices.Count; i++)
            {
                _client.SetCustomMode(i);
                var leds = new RgbColor[Devices[i].Leds.Length];
                leds.AsSpan().Fill(c);
                _client.UpdateLeds(i, leds);
            }
        });
    }

    public async Task SetDeviceColorAsync(int deviceIndex, WpfColor color)
    {
        if (_client is null || !IsConnected) return;
        if (deviceIndex < 0 || deviceIndex >= Devices.Count) return;
        var c    = ToRgbColor(color);
        var leds = new RgbColor[Devices[deviceIndex].Leds.Length];
        leds.AsSpan().Fill(c);
        await Task.Run(() =>
        {
            _client.SetCustomMode(deviceIndex);
            _client.UpdateLeds(deviceIndex, leds);
        });
    }

    public async Task TurnOffAllAsync() =>
        await SetAllColorsAsync(WpfColor.FromRgb(0, 0, 0));

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task<bool> TryConnectClientAsync(string host, int port)
    {
        try
        {
            DisposeClient();
            _client = new OpenRgbClient(host, port, "Flow", autoConnect: false);
            await Task.Run(() => _client.Connect());
            var devices = await Task.Run(() => _client.GetAllControllerData());
            Devices = devices;
            SetState(OpenRgbConnectionState.Connected,
                     $"Connected — {Devices.Count} device{(Devices.Count != 1 ? "s" : "")}");
            return true;
        }
        catch
        {
            DisposeClient();
            return false;
        }
    }

    private void LaunchServer(string exePath, int port)
    {
        try
        {
            _managedProcess = new Process
            {
                StartInfo = new ProcessStartInfo(exePath, $"--server --server-port {port} --noGui")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    WindowStyle     = ProcessWindowStyle.Hidden,
                }
            };
            _managedProcess.Start();
        }
        catch { /* will surface as timeout */ }
    }

    private void DisposeClient()
    {
        try { _client?.Dispose(); } catch { }
        _client = null;
    }

    private void SetState(OpenRgbConnectionState state, string text)
    {
        State     = state;
        StateText = text;
        StateChanged?.Invoke();
    }

    private static RgbColor ToRgbColor(WpfColor c) => new(c.R, c.G, c.B);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeClient();
        try
        {
            if (_managedProcess is { HasExited: false })
                _managedProcess.Kill();
            _managedProcess?.Dispose();
        }
        catch { }
    }
}
