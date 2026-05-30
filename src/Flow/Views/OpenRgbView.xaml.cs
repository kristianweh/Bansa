using Flow.ViewModels;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using WpfColor = System.Windows.Media.Color;
using DrawingColor = System.Drawing.Color;
using WinForms = System.Windows.Forms;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Flow.Views;

public partial class OpenRgbView : System.Windows.Controls.UserControl
{
    private OpenRgbViewModel? Vm => DataContext as OpenRgbViewModel;

    private static readonly ColorPreset[] Presets =
    [
        new("White",   "#FFFFFF", WpfColor.FromRgb(255, 255, 255)),
        new("Red",     "#FF0000", WpfColor.FromRgb(255,   0,   0)),
        new("Orange",  "#FF6600", WpfColor.FromRgb(255, 102,   0)),
        new("Yellow",  "#FFFF00", WpfColor.FromRgb(255, 255,   0)),
        new("Green",   "#00FF00", WpfColor.FromRgb(  0, 255,   0)),
        new("Cyan",    "#00FFFF", WpfColor.FromRgb(  0, 255, 255)),
        new("Blue",    "#0000FF", WpfColor.FromRgb(  0,   0, 255)),
        new("Purple",  "#9900FF", WpfColor.FromRgb(153,   0, 255)),
        new("Pink",    "#FF00FF", WpfColor.FromRgb(255,   0, 255)),
    ];

    public OpenRgbView()
    {
        InitializeComponent();
        PresetSwatches.ItemsSource = Presets;
        ToolsFolderText.Text = Path.Combine(App.DataFolder, "Tools", "OpenRGB.exe");
        DataContextChanged += (_, _) => SyncHexFromVm();
    }

    // ── Global color swatch ───────────────────────────────────────────────────

    private void OnColorSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        var picked = PickColor(Vm.GlobalColor);
        if (picked is null) return;
        Vm.GlobalColor = picked.Value;
        HexInput.Text  = ColorToHex(picked.Value);
    }

    private void OnPresetSwatchClick(object sender, MouseButtonEventArgs e)
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

    // ── Hex input ─────────────────────────────────────────────────────────────

    private void OnHexInputLostFocus(object sender, RoutedEventArgs e) => ApplyHex();

    private void OnHexInputKeyDown(object sender, WpfKeyEventArgs e)
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

    // ── Per-device color swatch ───────────────────────────────────────────────

    private void OnDeviceSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is null) return;
        if (sender is not FrameworkElement { Tag: int idx }) return;
        var item = Vm.Devices.FirstOrDefault(d => d.Index == idx);
        if (item is null) return;

        var picked = PickColor(item.Color);
        if (picked is null) return;
        item.Color = picked.Value;
    }

    // ── Setup helpers ─────────────────────────────────────────────────────────

    private void OnOpenRgbLinkClick(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://openrgb.org") { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnOpenToolsFolder(object sender, RoutedEventArgs e)
    {
        var folder = Path.Combine(App.DataFolder, "Tools");
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
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

    private static string ColorToHex(WpfColor c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static WpfColor? ParseHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.TrimStart('#');
        if (s.Length != 6) return null;
        try
        {
            byte r = Convert.ToByte(s[0..2], 16);
            byte g = Convert.ToByte(s[2..4], 16);
            byte b = Convert.ToByte(s[4..6], 16);
            return WpfColor.FromRgb(r, g, b);
        }
        catch { return null; }
    }

    private void SyncHexFromVm()
    {
        if (Vm is null) return;
        HexInput.Text = ColorToHex(Vm.GlobalColor);
    }
}

public sealed record ColorPreset(string Name, string Hex, WpfColor MediaColor);
