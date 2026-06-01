using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using WpfBrush = System.Windows.Media.Brush;
using WpfSolid = System.Windows.Media.SolidColorBrush;
using WpfColor = System.Windows.Media.Color;
using ImageSource = System.Windows.Media.ImageSource;

namespace Bansa.ViewModels;

// ════════════════════════════════════════════════════════════════════════════
//  ToolsViewModel
// ════════════════════════════════════════════════════════════════════════════

public sealed class ToolsViewModel
{
    public ObservableCollection<ToolItem> Tools { get; } = [];

    public ToolsViewModel()
    {
        var toolsDir = App.ToolsFolder;

        // ── OpenRGB ───────────────────────────────────────────────────────
        Tools.Add(new ToolItem(
            name:          "OpenRGB",
            description:   "Control RGB lighting on keyboards, mice, GPUs and more.",
            tileBrush:     Solid("#7B2FBE"),
            iconImagePath: "pack://application:,,,/Resources/logo_openrgb.png",
            toolsDir:      toolsDir,
            exeName:       "OpenRGB.exe",
            websiteUrl:    "https://openrgb.org"));

        // ── HWiNFO ────────────────────────────────────────────────────────
        Tools.Add(new ToolItem(
            name:          "HWiNFO",
            description:   "Comprehensive hardware information and real-time system monitoring.",
            tileBrush:     Solid("#1A8754"),
            iconImagePath: "pack://application:,,,/Resources/logo_hwinfo.png",
            toolsDir:      toolsDir,
            exeName:       "HWiNFO64.exe",
            websiteUrl:    "https://www.hwinfo.com/download/"));

        // ── ShareX ────────────────────────────────────────────────────────
        Tools.Add(new ToolItem(
            name:          "ShareX",
            description:   "Powerful screen capture, recording and file sharing.",
            tileBrush:     Solid("#1565C0"),
            iconImagePath: "pack://application:,,,/Resources/logo_sharex.png",
            toolsDir:      toolsDir,
            exeName:       "ShareX.exe",
            websiteUrl:    "https://getsharex.com"));

        // ── Chris Titus WinUtil ───────────────────────────────────────────
        Tools.Add(new ToolItem(
            name:          "Chris Titus WinUtil",
            description:   "Windows tweaks, debloating, privacy fixes and optimizations.",
            tileBrush:     Solid("#B94C00"),
            iconImagePath: "pack://application:,,,/Resources/logo_ctt.png",
            scriptCommand: "irm christitus.com/win | iex"));

        // ── DDU ───────────────────────────────────────────────────────────
        Tools.Add(new ToolItem(
            name:          "DDU",
            description:   "Display Driver Uninstaller — cleanly removes GPU drivers before reinstalling or switching.",
            tileBrush:     Solid("#7A1010"),
            iconImagePath: "pack://application:,,,/Resources/logo_ddu.png",
            url:           "https://www.guru3d.com/download/display-driver-uninstaller-download/",
            websiteOnly:   true));
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
    public string      Name        { get; }
    public string      Description { get; }
    public WpfBrush    TileBrush   { get; }
    public string?     WebsiteUrl  { get; }
    public ImageSource? IconImage  { get; }

    // ── Kind ──────────────────────────────────────────────────────────────
    private readonly string? _toolsDir;
    private readonly string? _exeName;
    private readonly string? _scriptCommand;
    private readonly bool    _isWebsiteOnly;

    public bool IsScript      => _scriptCommand is not null;
    public bool IsWebsiteOnly => _isWebsiteOnly;

    // ── State ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusText = "";

    // ── Derived ───────────────────────────────────────────────────────────

    private string? _cachedExe = "";

    private string? FindExe()
    {
        if (_cachedExe != "")
            return _cachedExe;
        _cachedExe = _toolsDir is null || _exeName is null || !Directory.Exists(_toolsDir)
            ? null
            : Directory.EnumerateFiles(_toolsDir, _exeName, SearchOption.AllDirectories)
                       .FirstOrDefault();
        return _cachedExe;
    }

    public bool IsInstalled => FindExe() is not null;

    public string SubLabel
    {
        get
        {
            if (_isWebsiteOnly) return "Download from the website";
            if (IsScript) return "Runs via PowerShell · no install needed";
            var exe = FindExe();
            if (exe is not null)
            {
                try
                {
                    var rel = Path.GetRelativePath(AppContext.BaseDirectory,
                                  Path.GetDirectoryName(exe)!);
                    return $"Installed · {rel}";
                }
                catch { return "Installed"; }
            }
            return "Not installed";
        }
    }

    private void RefreshState()
    {
        _cachedExe = "";
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(SubLabel));
    }

    private static ImageSource? LoadImage(string? packUri)
    {
        if (packUri is null) return null;
        try
        {
            var img = new BitmapImage(new Uri(packUri, UriKind.Absolute));
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    // ── Commands ──────────────────────────────────────────────────────────
    public RelayCommand  OpenCommand        { get; }
    public RelayCommand? OpenWebsiteCommand { get; }

    // ── Constructor: portable exe ─────────────────────────────────────────
    public ToolItem(string name, string description, WpfBrush tileBrush,
                    string? iconImagePath,
                    string toolsDir, string exeName, string? websiteUrl = null)
    {
        Name = name; Description = description; TileBrush = tileBrush;
        IconImage = LoadImage(iconImagePath);
        _toolsDir = toolsDir; _exeName = exeName; WebsiteUrl = websiteUrl;

        OpenCommand = new RelayCommand(OpenExe);

        if (websiteUrl is not null)
            OpenWebsiteCommand = new RelayCommand(() =>
                Process.Start(new ProcessStartInfo(websiteUrl) { UseShellExecute = true }));
    }

    // ── Constructor: website-only link ────────────────────────────────────
    public ToolItem(string name, string description, WpfBrush tileBrush,
                    string? iconImagePath, string url, bool websiteOnly)
    {
        Name = name; Description = description; TileBrush = tileBrush;
        IconImage = LoadImage(iconImagePath);
        WebsiteUrl = url;
        _isWebsiteOnly = true;
        OpenCommand = new RelayCommand(() => { });
        OpenWebsiteCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
    }

    // ── Constructor: script ───────────────────────────────────────────────
    public ToolItem(string name, string description, WpfBrush tileBrush,
                    string? iconImagePath, string scriptCommand)
    {
        Name = name; Description = description; TileBrush = tileBrush;
        IconImage = LoadImage(iconImagePath);
        _scriptCommand = scriptCommand;
        OpenCommand = new RelayCommand(RunScript);
    }

    // ── Actions ───────────────────────────────────────────────────────────

    private void OpenExe()
    {
        StatusText = "";
        RefreshState();

        var exe = FindExe();
        if (exe is null)
        {
            StatusText = $"Not found in Data\\Tools\\ — download from the website.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute  = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
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
