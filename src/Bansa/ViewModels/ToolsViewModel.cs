using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using WpfBrush = System.Windows.Media.Brush;
using WpfSolid = System.Windows.Media.SolidColorBrush;
using WpfColor = System.Windows.Media.Color;

namespace Bansa.ViewModels;

// ════════════════════════════════════════════════════════════════════════════
//  ToolsViewModel
// ════════════════════════════════════════════════════════════════════════════

public sealed class ToolsViewModel
{
    public ObservableCollection<ToolItem> Tools { get; } = [];

    public ToolsViewModel()
    {
        // ── OpenRGB ───────────────────────────────────────────────────────
        Tools.Add(new ToolItem(
            name:       "OpenRGB",
            description:"Control RGB lighting on keyboards, mice, GPUs and more.",
            iconGlyph:  "",
            tileBrush:  Solid("#7B2FBE"),
            installDir: Path.Combine(App.DataFolder, "Tools", "OpenRGB"),
            exeName:    "OpenRGB.exe",
            websiteUrl: "https://openrgb.org"));

        // ── HWiNFO ────────────────────────────────────────────────────────
        Tools.Add(new ToolItem(
            name:       "HWiNFO",
            description:"Comprehensive hardware information and real-time system monitoring.",
            iconGlyph:  "",
            tileBrush:  Solid("#1A8754"),
            installDir: Path.Combine(App.DataFolder, "Tools", "HWiNFO"),
            exeName:    "HWiNFO64.exe",
            websiteUrl: "https://www.hwinfo.com/download/"));

        // ── ShareX ────────────────────────────────────────────────────────
        Tools.Add(new ToolItem(
            name:       "ShareX",
            description:"Powerful screen capture, recording and file sharing.",
            iconGlyph:  "",
            tileBrush:  Solid("#1565C0"),
            installDir: Path.Combine(App.DataFolder, "Tools", "ShareX"),
            exeName:    "ShareX.exe",
            websiteUrl: "https://getsharex.com"));

        // ── Chris Titus WinUtil ───────────────────────────────────────────
        Tools.Add(new ToolItem(
            name:          "Chris Titus WinUtil",
            description:   "Windows tweaks, debloating, privacy fixes and optimizations.",
            iconGlyph:     "",
            tileBrush:     Solid("#B94C00"),
            scriptCommand: "irm christitus.com/win | iex"));
    }

    private static WpfBrush Solid(string hex)
    {
        var c = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var b = new WpfSolid(c);
        b.Freeze();
        return b;
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  ToolItem  —  one card; either a portable exe or a PowerShell script
// ════════════════════════════════════════════════════════════════════════════

public sealed partial class ToolItem : ObservableObject
{
    // ── Identity ──────────────────────────────────────────────────────────
    public string   Name        { get; }
    public string   Description { get; }
    public string   IconGlyph   { get; }
    public WpfBrush TileBrush   { get; }
    public string?  WebsiteUrl  { get; }

    // ── Kind ──────────────────────────────────────────────────────────────
    private readonly string? _installDir;
    private readonly string? _exeName;
    private readonly string? _scriptCommand;

    public bool IsScript => _scriptCommand is not null;

    // ── State ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusText = "";

    // ── Derived ───────────────────────────────────────────────────────────
    public bool IsInstalled => _installDir is not null && _exeName is not null
                            && File.Exists(Path.Combine(_installDir, _exeName));

    public string SubLabel
    {
        get
        {
            if (IsScript) return "Runs via PowerShell · no install needed";
            if (IsInstalled)
            {
                try
                {
                    var rel = Path.GetRelativePath(AppContext.BaseDirectory, _installDir!);
                    return $"Installed · {rel}";
                }
                catch { return "Installed"; }
            }
            return "Not installed";
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────
    public RelayCommand  OpenCommand        { get; }
    public RelayCommand? OpenWebsiteCommand { get; }

    // ── Constructor: portable exe ─────────────────────────────────────────
    public ToolItem(string name, string description, string iconGlyph, WpfBrush tileBrush,
                    string installDir, string exeName, string? websiteUrl = null)
    {
        Name = name; Description = description; IconGlyph = iconGlyph; TileBrush = tileBrush;
        _installDir = installDir; _exeName = exeName; WebsiteUrl = websiteUrl;

        OpenCommand = new RelayCommand(OpenExe);

        if (websiteUrl is not null)
            OpenWebsiteCommand = new RelayCommand(() =>
                Process.Start(new ProcessStartInfo(websiteUrl) { UseShellExecute = true }));
    }

    // ── Constructor: script ───────────────────────────────────────────────
    public ToolItem(string name, string description, string iconGlyph, WpfBrush tileBrush,
                    string scriptCommand)
    {
        Name = name; Description = description; IconGlyph = iconGlyph; TileBrush = tileBrush;
        _scriptCommand = scriptCommand;
        OpenCommand = new RelayCommand(RunScript);
    }

    // ── Actions ───────────────────────────────────────────────────────────

    private void OpenExe()
    {
        StatusText = "";
        if (!IsInstalled)
        {
            try
            {
                var rel = Path.GetRelativePath(AppContext.BaseDirectory, _installDir!);
                StatusText = $"Not found in {rel}";
            }
            catch { StatusText = "Not installed — download from the website."; }
            return;
        }
        try
        {
            var exe = Path.Combine(_installDir!, _exeName!);
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute  = true,
                WorkingDirectory = _installDir!,
            });
        }
        catch (Exception ex) { StatusText = $"Launch failed: {ex.Message}"; }
    }

    private void RunScript()
    {
        StatusText = "";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -ExecutionPolicy Bypass -Command \"{_scriptCommand}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { StatusText = $"Failed: {ex.Message}"; }
    }
}
