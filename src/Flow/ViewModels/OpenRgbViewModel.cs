using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Services;
using OpenRGB.NET;
using System.Collections.ObjectModel;
using WpfColor  = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;

namespace Flow.ViewModels;

public sealed partial class OpenRgbViewModel : ObservableObject, IDisposable
{
    private readonly OpenRgbService _service = new();
    private CancellationTokenSource? _downloadCts;

    // ── State ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool     _isConnected;
    [ObservableProperty] private bool     _isConnecting;
    [ObservableProperty] private bool     _isDownloading;
    [ObservableProperty] private bool     _needsSetup;    // true until first successful install
    [ObservableProperty] private int      _downloadProgress;
    [ObservableProperty] private string   _downloadStatus = "";
    [ObservableProperty] private string   _setupError     = "";  // persists after a failed attempt
    [ObservableProperty] private string   _statusText     = "Starting…";
    [ObservableProperty] private WpfColor _globalColor    = WpfColors.White;

    public ObservableCollection<OpenRgbDeviceItem> Devices { get; } = [];

    public OpenRgbViewModel()
    {
        _service.StateChanged += OnServiceStateChanged;
        NeedsSetup = !_service.Downloader.IsInstalled;
    }

    // ── App-startup init (called from MainWindow after load) ──────────────────

    /// Silently connects if OpenRGB is already installed; no-ops otherwise.
    public async Task InitAsync() => await _service.StartAsync();

    // ── Commands ──────────────────────────────────────────────────────────────

    /// One-time setup: download + auto-connect. User clicks once, done forever.
    [RelayCommand(CanExecute = nameof(CanSetup))]
    private async Task SetupAsync()
    {
        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        SetupError    = "";

        try
        {
            await _service.Downloader.DownloadAsync(
                p  => Dispatch(() => DownloadProgress = p),
                s  => Dispatch(() => DownloadStatus   = s),
                _downloadCts.Token);

            NeedsSetup = false;
            await _service.ConnectAfterDownloadAsync();
        }
        catch (OperationCanceledException)
        {
            SetupError = "Download cancelled.";
        }
        catch (Exception ex)
        {
            SetupError = $"Setup failed: {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private bool CanSetup() => !IsDownloading && !IsConnected;

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ApplyGlobalColorAsync() =>
        await _service.SetAllColorsAsync(GlobalColor);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task TurnOffAsync() => await _service.TurnOffAllAsync();

    // ── Device-level color ────────────────────────────────────────────────────

    public async Task SetDeviceColorAsync(int index, WpfColor color) =>
        await _service.SetDeviceColorAsync(index, color);

    // ── Internals ─────────────────────────────────────────────────────────────

    private void OnServiceStateChanged() => Dispatch(() =>
    {
        StatusText   = _service.StateText;
        IsConnected  = _service.IsConnected;
        IsConnecting = _service.State == OpenRgbConnectionState.Connecting;

        ApplyGlobalColorCommand.NotifyCanExecuteChanged();
        TurnOffCommand.NotifyCanExecuteChanged();
        SetupCommand.NotifyCanExecuteChanged();
        RefreshDevices();
    });

    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var (dev, idx) in _service.Devices.Select((d, i) => (d, i)))
            Devices.Add(new OpenRgbDeviceItem(idx, dev, this));
    }

    private static void Dispatch(Action a) =>
        System.Windows.Application.Current.Dispatcher.Invoke(a);

    public void Dispose()
    {
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _service.Dispose();
    }
}

// ── Per-device item ───────────────────────────────────────────────────────────

public sealed partial class OpenRgbDeviceItem : ObservableObject
{
    private readonly OpenRgbViewModel _parent;

    public int    Index    { get; }
    public string Name     { get; }
    public string Type     { get; }
    public int    LedCount { get; }

    [ObservableProperty] private WpfColor _color = WpfColors.White;

    public OpenRgbDeviceItem(int index, Device device, OpenRgbViewModel parent)
    {
        Index    = index;
        Name     = device.Name;
        Type     = device.Type.ToString();
        LedCount = device.Leds.Length;
        _parent  = parent;
    }

    [RelayCommand]
    private async Task ApplyColorAsync() =>
        await _parent.SetDeviceColorAsync(Index, Color);
}
