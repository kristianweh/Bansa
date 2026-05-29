using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Flow.Services;

namespace Flow.Views;

public partial class SetLimitWindow : Window
{
    public int UploadKbps   { get; private set; }
    public int DownloadKbps { get; private set; }

    // ── DWM dark title bar ────────────────────────────────────────────────────
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    public SetLimitWindow(string appName, int currentUploadKbps, int currentDownloadKbps,
                          int connectionUploadMbps = 0, int connectionDownloadMbps = 0,
                          bool hasUdpConnections = false)
    {
        InitializeComponent();
        AppNameText.Text = $"Limits for {appName}";

        UploadEnabled.IsChecked   = currentUploadKbps   > 0;
        UploadKbpsBox.Text        = currentUploadKbps   > 0 ? currentUploadKbps.ToString()   : "1000";

        DownloadEnabled.IsChecked = currentDownloadKbps > 0;
        DownloadKbpsBox.Text      = currentDownloadKbps > 0 ? currentDownloadKbps.ToString() : "1000";

        // Show UDP warning when the app has active UDP sockets (games, voice, QUIC).
        // Upload limits work by pulsing a firewall block — for UDP this drops excess packets
        // rather than queuing them, so voice/game quality may suffer at the limit boundary.
        if (hasUdpConnections)
            UdpWarningBorder.Visibility = Visibility.Visible;

        PopulateUploadPresets(connectionUploadMbps);
        PopulateDownloadPresets(connectionDownloadMbps);
        PopulateProfiles();
    }

    // ── Preset generation ────────────────────────────────────────────────────

    /// <summary>Mbps → KB/s using real kilobytes (1 Mbps = 125 KB/s).</summary>
    private static int MbpsToKbps(int mbps) => mbps * 1000 / 8;

    private static int RoundNice(double kbps)
    {
        if (kbps <= 0) return 10;
        int v = (int)kbps;
        if (v < 20)   return Math.Max(5, (v / 5)   * 5);
        if (v < 100)  return (v / 10)  * 10;
        if (v < 500)  return (v / 25)  * 25;
        if (v < 2000) return (v / 50)  * 50;
        return (v / 100) * 100;
    }

    private static string FormatKbps(int kbps)
    {
        if (kbps >= 1024)
        {
            double mb = kbps / 1024.0;
            return mb == Math.Floor(mb) ? $"{(int)mb} MB/s" : $"{mb:F1} MB/s";
        }
        return $"{kbps} KB/s";
    }

    private void PopulateUploadPresets(int connectionMbps)
    {
        UploadPresetsPanel.Children.Clear();
        List<(int kbps, string label)> presets;

        if (connectionMbps > 0)
        {
            int maxKbps = MbpsToKbps(connectionMbps);
            // 8 % / 16 % / 24 % / 40 % / 60 % of the line's upload capacity
            // (always strictly below the configured connection speed)
            double[] pcts = { 0.08, 0.16, 0.24, 0.40, 0.60 };
            presets = pcts
                .Select(p => RoundNice(maxKbps * p))
                .Where(k => k > 0)
                .Distinct()
                .Select(k => (k, FormatKbps(k)))
                .ToList();
        }
        else
        {
            presets = new() { (100, "100 KB/s"), (500, "500 KB/s"), (1024, "1 MB/s"), (5120, "5 MB/s"), (10240, "10 MB/s") };
        }

        foreach (var (kbps, label) in presets)
            UploadPresetsPanel.Children.Add(MakePresetButton(label, kbps.ToString(), OnPresetUpload));
    }

    private void PopulateDownloadPresets(int connectionMbps)
    {
        DownloadPresetsPanel.Children.Clear();
        List<(int kbps, string label)> presets;

        if (connectionMbps > 0)
        {
            int maxKbps = MbpsToKbps(connectionMbps);
            // Spread across 5 % / 10 % / 20 % / 40 % / 80 % of line capacity
            double[] pcts = { 0.05, 0.10, 0.20, 0.40, 0.80 };
            presets = pcts
                .Select(p => RoundNice(maxKbps * p))
                .Where(k => k > 0)
                .Distinct()
                .Select(k => (k, FormatKbps(k)))
                .ToList();
        }
        else
        {
            presets = new() { (100, "100 KB/s"), (500, "500 KB/s"), (1024, "1 MB/s"), (5120, "5 MB/s"), (10240, "10 MB/s") };
        }

        foreach (var (kbps, label) in presets)
            DownloadPresetsPanel.Children.Add(MakePresetButton(label, kbps.ToString(), OnPresetDownload));
    }

    private void PopulateProfiles()
    {
        var profiles = App.Settings?.LimitProfiles;
        if (profiles is null || profiles.Count == 0) return;

        ProfilesCard.Visibility = Visibility.Visible;
        foreach (var p in profiles)
        {
            var btn = MakePresetButton(p.Name, null, null);
            var captured = p;
            btn.Click += (_, _) =>
            {
                if (captured.UploadKbps > 0)
                {
                    UploadKbpsBox.Text = captured.UploadKbps.ToString();
                    UploadEnabled.IsChecked = true;
                }
                if (captured.DownloadKbps > 0)
                {
                    DownloadKbpsBox.Text = captured.DownloadKbps.ToString();
                    DownloadEnabled.IsChecked = true;
                }
            };
            ProfilesPanel.Children.Add(btn);
        }
    }

    private static Button MakePresetButton(string label, string? tag, RoutedEventHandler? handler)
    {
        var btn = new Button
        {
            Content = label,
            Tag     = tag,
            Margin  = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(10, 4, 10, 4),
        };
        if (handler is not null) btn.Click += handler;
        return btn;
    }

    // ── Preset click handlers ─────────────────────────────────────────────────

    private void OnPresetUpload(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
        {
            UploadKbpsBox.Text = tag;
            UploadEnabled.IsChecked = true;
        }
    }

    private void OnPresetDownload(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
        {
            DownloadKbpsBox.Text = tag;
            DownloadEnabled.IsChecked = true;
        }
    }

    // ── Dialog buttons ────────────────────────────────────────────────────────

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        UploadKbps   = (UploadEnabled.IsChecked   == true && int.TryParse(UploadKbpsBox.Text,   out var u) && u > 0) ? u : 0;
        DownloadKbps = (DownloadEnabled.IsChecked == true && int.TryParse(DownloadKbpsBox.Text, out var d) && d > 0) ? d : 0;
        DialogResult = true;
        Close();
    }
}
