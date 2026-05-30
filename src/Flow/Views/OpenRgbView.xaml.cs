using Flow.ViewModels;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using WpfColor     = System.Windows.Media.Color;
using DrawingColor = System.Drawing.Color;
using WinForms     = System.Windows.Forms;
using WpfKeyArgs   = System.Windows.Input.KeyEventArgs;

namespace Flow.Views;

public partial class OpenRgbView : System.Windows.Controls.UserControl
{
    private OpenRgbViewModel? Vm => DataContext as OpenRgbViewModel;

    private static readonly ColorPreset[] Presets =
    [
        new("White",  "#FFFFFF", WpfColor.FromRgb(255, 255, 255)),
        new("Red",    "#FF0000", WpfColor.FromRgb(255,   0,   0)),
        new("Orange", "#FF6600", WpfColor.FromRgb(255, 102,   0)),
        new("Yellow", "#FFFF00", WpfColor.FromRgb(255, 255,   0)),
        new("Green",  "#00FF00", WpfColor.FromRgb(  0, 255,   0)),
        new("Cyan",   "#00FFFF", WpfColor.FromRgb(  0, 255, 255)),
        new("Blue",   "#0000FF", WpfColor.FromRgb(  0,   0, 255)),
        new("Purple", "#9900FF", WpfColor.FromRgb(153,   0, 255)),
        new("Pink",   "#FF00FF", WpfColor.FromRgb(255,   0, 255)),
    ];

    public OpenRgbView()
    {
        InitializeComponent();
        PresetColors.ItemsSource = Presets;
        DataContextChanged += (_, _) => SyncHex();
    }

    // ── Global color ──────────────────────────────────────────────────────────

    private void OnGlobalSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        var c = PickColor(Vm.GlobalColor);
        if (c is null) return;
        Vm.GlobalColor = c.Value;
        HexInput.Text  = ToHex(c.Value);
    }

    private void OnPresetClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        if (sender is FrameworkElement { Tag: string hex })
        {
            var c = ParseHex(hex);
            if (c is null) return;
            Vm.GlobalColor = c.Value;
            HexInput.Text  = hex;
        }
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e) => ApplyHex();
    private void OnHexKeyDown(object sender, WpfKeyArgs e)
    {
        if (e.Key == Key.Enter) ApplyHex();
    }

    private void ApplyHex()
    {
        if (Vm is null) return;
        var c = ParseHex(HexInput.Text);
        if (c is null) return;
        Vm.GlobalColor = c.Value;
    }

    // ── Per-device color ──────────────────────────────────────────────────────

    private void OnDeviceSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        if (sender is not FrameworkElement { Tag: int idx }) return;
        var item = Vm.Devices.FirstOrDefault(d => d.Index == idx);
        if (item is null) return;
        var c = PickColor(item.Color);
        if (c is null) return;
        item.Color = c.Value;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WpfColor? PickColor(WpfColor initial)
    {
        var dlg = new WinForms.ColorDialog
        {
            Color         = DrawingColor.FromArgb(initial.R, initial.G, initial.B),
            FullOpen      = true,
            AllowFullOpen = true,
        };
        return dlg.ShowDialog() == WinForms.DialogResult.OK
            ? WpfColor.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B)
            : null;
    }

    private static string ToHex(WpfColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static WpfColor? ParseHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.TrimStart('#');
        if (s.Length != 6) return null;
        try
        {
            return WpfColor.FromRgb(
                Convert.ToByte(s[0..2], 16),
                Convert.ToByte(s[2..4], 16),
                Convert.ToByte(s[4..6], 16));
        }
        catch { return null; }
    }

    private void SyncHex()
    {
        if (Vm is null) return;
        HexInput.Text = ToHex(Vm.GlobalColor);
    }
}

public sealed record ColorPreset(string Name, string Hex, WpfColor MediaColor);
