using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Bansa.Services;

// Pin Clipboard to the WPF variant (System.Windows.Forms also exposes a Clipboard
// because UseWindowsForms is on for the tray icon).
using Clipboard = System.Windows.Clipboard;

namespace Bansa.Views;

public partial class SpeedTestView : UserControl
{
    private readonly SpeedTester _tester = new();
    private CancellationTokenSource? _cts;
    private double _peakMbps;

    public SpeedTestView()
    {
        InitializeComponent();
        _tester.ProgressMbps += OnProgress;
    }

    private void OnProgress(double mbps)
    {
        Dispatcher.InvokeAsync(() =>
        {
            CurrentMbps.Text = mbps.ToString("0.##");
            if (mbps > _peakMbps) _peakMbps = mbps;
            var w = Math.Min(1, mbps / Math.Max(1, _peakMbps));
            if (ProgressBar.Parent is FrameworkElement parent)
            {
                ProgressBar.Width = parent.ActualWidth * w;
            }
        });
    }

    private async void OnStartSelfTest(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) { _cts.Cancel(); _cts = null; StartBtn.Content = "Start"; return; }

        _cts = new CancellationTokenSource();
        StartBtn.Content = "Stop";
        _peakMbps = 0;
        CurrentMbps.Text = "0";
        ProgressBar.Width = 0;

        try
        {
            var avg = await _tester.RunDownloadTestAsync(_cts.Token);
            CurrentMbps.Text = $"{avg:0.##} (avg)";
        }
        catch (Exception ex)
        {
            ConfirmDialog.Show("Speed test failed", ex.Message, confirmText: "OK", cancelText: null);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            StartBtn.Content = "Start";
        }
    }

    private void OnOpenExternal(object sender, RoutedEventArgs e) => SpeedTester.OpenExternalSpeedTest();

    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText("https://fast.com");
            ConfirmDialog.Show("Copied", "Copied https://fast.com to the clipboard.", confirmText: "OK", cancelText: null);
        }
        catch { }
    }
}
