using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Services;
using OpenRGB.NET;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using System.Collections.ObjectModel;
using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;

namespace Flow.ViewModels;

public sealed partial class OpenRgbViewModel : ObservableObject, IDisposable
{
    private readonly OpenRgbService _service = new();

    [ObservableProperty] private bool      _isConnected;
    [ObservableProperty] private bool      _isConnecting;
    [ObservableProperty] private string    _statusText = "Not connected";
    [ObservableProperty] private bool      _hasExe;
    [ObservableProperty] private WpfColor  _globalColor = WpfColors.White;

    public ObservableCollection<OpenRgbDeviceItem> Devices { get; } = [];

    public OpenRgbViewModel()
    {
        _service.StateChanged += OnServiceStateChanged;
        RefreshExeStatus();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsConnecting = true;
        await _service.ConnectAsync();
        IsConnecting = false;
    }

    private bool CanConnect() => !IsConnected && !IsConnecting;

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void Disconnect() => _service.Disconnect();

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ApplyGlobalColorAsync() =>
        await _service.SetAllColorsAsync(GlobalColor);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task TurnOffAsync() => await _service.TurnOffAllAsync();

    [RelayCommand]
    private async Task BrowseForExeAsync()
    {
        var dlg = new WpfOpenFileDialog
        {
            Title  = "Locate OpenRGB portable executable",
            Filter = "OpenRGB.exe|OpenRGB.exe|All executables|*.exe",
        };
        if (dlg.ShowDialog() != true) return;

        await OpenRgbService.ImportPortableExeAsync(dlg.FileName);
        RefreshExeStatus();
    }

    // ── Device-level color ────────────────────────────────────────────────────

    public async Task SetDeviceColorAsync(int index, WpfColor color) =>
        await _service.SetDeviceColorAsync(index, color);

    // ── Internals ─────────────────────────────────────────────────────────────

    private void OnServiceStateChanged()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            StatusText   = _service.StateText;
            IsConnected  = _service.IsConnected;
            IsConnecting = _service.State == OpenRgbConnectionState.Connecting;

            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
            ApplyGlobalColorCommand.NotifyCanExecuteChanged();
            TurnOffCommand.NotifyCanExecuteChanged();
            RefreshDevices();
        });
    }

    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var (dev, idx) in _service.Devices.Select((d, i) => (d, i)))
            Devices.Add(new OpenRgbDeviceItem(idx, dev, this));
    }

    private void RefreshExeStatus() =>
        HasExe = OpenRgbService.FindOpenRgbExe() is not null;

    public void Dispose() => _service.Dispose();
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
