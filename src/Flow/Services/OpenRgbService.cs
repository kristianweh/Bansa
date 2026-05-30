using OpenRGB.NET;
using System.Diagnostics;
using WpfColor = System.Windows.Media.Color;
using RgbColor = OpenRGB.NET.Color;

namespace Flow.Services;

public enum OpenRgbConnectionState { Disconnected, Connecting, Connected, Error }

public sealed class OpenRgbService : IDisposable
{
    public readonly OpenRgbDownloader Downloader = new();

    private OpenRgbClient? _client;
    private Process?       _serverProcess;
    private bool           _disposed;

    public OpenRgbConnectionState State     { get; private set; } = OpenRgbConnectionState.Disconnected;
    public string                 StateText { get; private set; } = "Starting…";
    public IReadOnlyList<Device>  Devices   { get; private set; } = [];
    public bool IsConnected => State == OpenRgbConnectionState.Connected;

    public event Action? StateChanged;

    // ── Called once at app startup ────────────────────────────────────────────

    /// Silently starts the OpenRGB server and connects. No-ops if exe is missing.
    public async Task StartAsync(int port = 6742)
    {
        if (!Downloader.IsInstalled) return;
        await ConnectInternalAsync(port);
    }

    // ── Called when user completes the one-time download ─────────────────────

    public async Task ConnectAfterDownloadAsync(int port = 6742) =>
        await ConnectInternalAsync(port);

    // ── Device control ────────────────────────────────────────────────────────

    public async Task SetAllColorsAsync(WpfColor color)
    {
        if (_client is null || !IsConnected) return;
        var c = ToRgb(color);
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
        var c    = ToRgb(color);
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

    private async Task ConnectInternalAsync(int port)
    {
        SetState(OpenRgbConnectionState.Connecting, "Connecting…");

        // Try attaching to an already-running instance first.
        if (await TryConnectAsync(port)) return;

        // Launch our bundled copy.
        if (!LaunchServer(port))
        {
            SetState(OpenRgbConnectionState.Error, "Failed to launch OpenRGB server");
            return;
        }

        // Give it up to 20 s to accept connections.
        // OpenRGB 1.0+ loads drivers on startup which takes a few seconds.
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500);

            // Fail fast if the process already exited (crash / missing dependency)
            if (_serverProcess is { HasExited: true })
            {
                SetState(OpenRgbConnectionState.Error,
                    $"OpenRGB exited unexpectedly (code {_serverProcess.ExitCode})");
                return;
            }

            if (await TryConnectAsync(port)) return;
        }

        SetState(OpenRgbConnectionState.Error, "OpenRGB server started but did not respond");
    }

    private async Task<bool> TryConnectAsync(int port)
    {
        try
        {
            DisposeClient();
            _client = new OpenRgbClient("localhost", port, "Flow", autoConnect: false);
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

    private bool LaunchServer(int port)
    {
        try
        {
            _serverProcess = new Process
            {
                StartInfo = new ProcessStartInfo(
                    Downloader.ExePath,
                    $"--server --server-port {port} --noGui")
                {
                    // UseShellExecute = true mimics the user double-clicking the exe:
                    // - avoids inheriting Flow's elevated admin token (which causes
                    //   OpenRGB to attempt driver installs and crash)
                    // - avoids CREATE_NO_WINDOW which breaks Qt's message-loop init
                    // - --noGui prevents any visible window from appearing
                    UseShellExecute  = true,
                    WindowStyle      = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Downloader.InstallDir,
                }
            };
            return _serverProcess.Start();
        }
        catch { return false; }
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

    private static RgbColor ToRgb(WpfColor c) => new(c.R, c.G, c.B);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeClient();
        try
        {
            if (_serverProcess is { HasExited: false })
                _serverProcess.Kill();
            _serverProcess?.Dispose();
        }
        catch { }
    }
}
