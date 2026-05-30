using OpenRGB.NET;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

    // Win32 — used to hide the OpenRGB window after the server is ready
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_HIDE = 0;

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

        // Give it up to 45 s to accept connections.
        // OpenRGB 1.0+ loads device drivers on startup (PawnIO etc.) which takes time.
        for (int i = 0; i < 90; i++)
        {
            await Task.Delay(500);

            // Fail fast if the process already exited (crash / missing dependency)
            if (_serverProcess is { HasExited: true })
            {
                var log = LastLaunchLog.Trim();
                var detail = string.IsNullOrEmpty(log) ? "" : $"\n{log}";
                SetState(OpenRgbConnectionState.Error,
                    $"OpenRGB exited (code {_serverProcess.ExitCode}){detail}");
                return;
            }

            if (await TryConnectAsync(port))
            {
                // Successfully connected — hide the OpenRGB window so it's invisible to the user.
                // Small delay lets Qt finish painting before we hide it.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(800);
                    HideServerWindow();
                });
                return;
            }
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

    // Hides the OpenRGB window after we connect — user manages lighting through Flow only.
    // We let OpenRGB start normally (hidden at launch breaks Qt's event loop / server init).
    private void HideServerWindow()
    {
        try
        {
            _serverProcess?.Refresh();
            var hwnd = _serverProcess?.MainWindowHandle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
                ShowWindow(hwnd, SW_HIDE);
        }
        catch { }
    }

    // Captured output from the OpenRGB process for diagnostics
    public string LastLaunchLog { get; private set; } = "";

    private bool LaunchServer(int port)
    {
        LastLaunchLog = "";
        try
        {
            _serverProcess = new Process
            {
                StartInfo = new ProcessStartInfo(
                    Downloader.ExePath,
                    $"--server --server-port {port}")
                {
                    UseShellExecute        = false,
                    WorkingDirectory       = Downloader.InstallDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    // Do NOT set CreateNoWindow — that's a console-subsystem flag and
                    // corrupts Qt's message-loop initialization for GUI applications.
                    // Do NOT set WindowStyle.Hidden either — Qt's SDK server only begins
                    // accepting connections once the event loop is running, and SW_HIDE
                    // prevents Qt from fully processing the window-show sequence that
                    // triggers exec().  We hide the window via Win32 after connecting.
                },
                EnableRaisingEvents = true,
            };

            var log = new System.Text.StringBuilder();
            _serverProcess.OutputDataReceived += (_, e) => { if (e.Data != null) log.AppendLine(e.Data); };
            _serverProcess.ErrorDataReceived  += (_, e) => { if (e.Data != null) log.AppendLine(e.Data); };
            _serverProcess.Exited             += (_, _) => LastLaunchLog = log.ToString();

            bool started = _serverProcess.Start();
            if (started)
            {
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();
            }
            return started;
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
