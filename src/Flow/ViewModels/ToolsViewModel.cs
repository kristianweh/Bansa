using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using WpfBrush = System.Windows.Media.Brush;
using WpfSolid = System.Windows.Media.SolidColorBrush;
using WpfColor = System.Windows.Media.Color;

namespace Flow.ViewModels;

// ════════════════════════════════════════════════════════════════════════════
//  ToolsViewModel  —  top-level VM for the Tools tab
// ════════════════════════════════════════════════════════════════════════════

public sealed class ToolsViewModel : IDisposable
{
    public ObservableCollection<PortableToolItem> Tools { get; } = [];

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders =
        {
            { "User-Agent",  "Flow-App/1.0" },
            { "Accept",      "application/json" },
        }
    };

    public ToolsViewModel()
    {
        // ── OpenRGB ───────────────────────────────────────────────────────
        Tools.Add(new PortableToolItem(
            id:          "openrgb",
            name:        "OpenRGB",
            description: "Control RGB lighting on keyboards, mice, GPUs and more.",
            iconGlyph:   "",
            tileBrush:   Solid("#7B2FBE"),
            installDir:  Path.Combine(App.DataFolder, "Tools", "OpenRGB"),
            exeName:     "OpenRGB.exe",
            downloadFunc: async (prog, status, ct) =>
                await new OpenRgbDownloader().DownloadAsync(prog, status, ct)));

        // ── HWiNFO ────────────────────────────────────────────────────────
        var hwInfoDir = Path.Combine(App.DataFolder, "Tools", "HWiNFO");
        Tools.Add(new PortableToolItem(
            id:          "hwinfo",
            name:        "HWiNFO",
            description: "Comprehensive hardware information and real-time system monitoring.",
            iconGlyph:   "",
            tileBrush:   Solid("#1A8754"),
            installDir:  hwInfoDir,
            exeName:     "HWiNFO64.exe",
            downloadFunc: async (prog, status, ct) =>
                await DownloadHwInfoAsync(hwInfoDir, prog, status, ct)));

        // ── ShareX ────────────────────────────────────────────────────────
        var shareXDir = Path.Combine(App.DataFolder, "Tools", "ShareX");
        Tools.Add(new PortableToolItem(
            id:          "sharex",
            name:        "ShareX",
            description: "Powerful screen capture, recording and file sharing.",
            iconGlyph:   "",
            tileBrush:   Solid("#1565C0"),
            installDir:  shareXDir,
            exeName:     "ShareX.exe",
            downloadFunc: async (prog, status, ct) =>
                await DownloadShareXAsync(shareXDir, prog, status, ct)));

        // ── Chris Titus WinUtil  (script — no download) ───────────────────
        Tools.Add(new PortableToolItem(
            id:           "cttwintool",
            name:         "Chris Titus WinUtil",
            description:  "Windows tweaks, debloating, privacy fixes and optimizations.",
            iconGlyph:    "",
            tileBrush:    Solid("#B94C00"),
            scriptCommand: "irm christitus.com/win | iex"));
    }

    private static WpfBrush Solid(string hex)
    {
        var c = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var b = new WpfSolid(c);
        b.Freeze();
        return b;
    }

    public void Dispose()
    {
        foreach (var t in Tools) t.Dispose();
    }

    // ── HWiNFO downloader ─────────────────────────────────────────────────

    private static async Task DownloadHwInfoAsync(
        string installDir,
        Action<int> onProgress,
        Action<string> onStatus,
        CancellationToken ct)
    {
        onStatus("Finding latest HWiNFO release…");
        onProgress(2);

        var (url, name) = await GetHwInfoAssetAsync(ct);

        onStatus($"Downloading {name}…");
        onProgress(5);

        var zipPath = Path.Combine(Path.GetTempPath(), "HWiNFO_portable.zip");
        await DownloadFileAsync(url, zipPath, onProgress, ct);

        onStatus("Extracting…");
        onProgress(96);

        Directory.CreateDirectory(installDir);
        ExtractZip(zipPath, installDir);
        try { File.Delete(zipPath); } catch { }

        onStatus("Done");
        onProgress(100);
    }

    private static async Task<(string Url, string Name)> GetHwInfoAssetAsync(CancellationToken ct)
    {
        // The official HWiNFO download page lists the latest portable ZIP.
        // The portable link is always at hwinfo.com/files/hwi_NNN.zip
        try
        {
            var html = await Http.GetStringAsync("https://www.hwinfo.com/download/", ct);

            // Match relative or absolute path to the portable zip
            var match = Regex.Match(html,
                @"(?:https://www\.hwinfo\.com)?/files/hwi_(\d+)\.zip",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var path = match.Value.StartsWith("http")
                    ? match.Value
                    : "https://www.hwinfo.com" + match.Value;
                return (path, $"hwi_{match.Groups[1].Value}.zip");
            }

            // Fallback: look for any .zip link on the page mentioning hwinfo
            var fallback = Regex.Match(html,
                @"https?://[^""'<>\s]*hwi[^""'<>\s]*\.zip",
                RegexOptions.IgnoreCase);
            if (fallback.Success)
                return (fallback.Value, Path.GetFileName(fallback.Value));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through */ }

        throw new InvalidOperationException(
            "Could not find HWiNFO download URL. " +
            "Visit https://www.hwinfo.com/download/ to download manually.");
    }

    // ── ShareX downloader ─────────────────────────────────────────────────

    private static async Task DownloadShareXAsync(
        string installDir,
        Action<int> onProgress,
        Action<string> onStatus,
        CancellationToken ct)
    {
        onStatus("Finding latest ShareX release…");
        onProgress(2);

        var (url, name) = await GetShareXAssetAsync(ct);

        onStatus($"Downloading {name}…");
        onProgress(5);

        var zipPath = Path.Combine(Path.GetTempPath(), "ShareX_portable.zip");
        await DownloadFileAsync(url, zipPath, onProgress, ct);

        onStatus("Extracting…");
        onProgress(96);

        Directory.CreateDirectory(installDir);
        ExtractZip(zipPath, installDir);
        try { File.Delete(zipPath); } catch { }

        onStatus("Done");
        onProgress(100);
    }

    private static async Task<(string Url, string Name)> GetShareXAssetAsync(CancellationToken ct)
    {
        // GitHub releases API — find latest non-prerelease, pick the portable zip
        var json = await Http.GetStringAsync(
            "https://api.github.com/repos/ShareX/ShareX/releases", ct);

        using var doc = JsonDocument.Parse(json);

        // First stable (non-prerelease) release
        var release = doc.RootElement.EnumerateArray()
            .FirstOrDefault(r =>
                r.ValueKind == JsonValueKind.Object &&
                r.TryGetProperty("prerelease", out var pre) &&
                pre.GetBoolean() == false);

        if (release.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("No stable ShareX release found.");

        if (!release.TryGetProperty("assets", out var assets))
            throw new InvalidOperationException("ShareX release has no assets.");

        // Prefer portable zip (contains "portable" in name) — fall back to any .zip
        foreach (var asset in assets.EnumerateArray())
        {
            var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url       = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";

            if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                assetName.Contains("portable", StringComparison.OrdinalIgnoreCase))
                return (url, assetName);
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url       = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";

            if (assetName.StartsWith("ShareX", StringComparison.OrdinalIgnoreCase) &&
                assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return (url, assetName);
        }

        throw new InvalidOperationException("Could not find a ShareX portable zip in the latest release.");
    }

    // ── Shared download helpers ───────────────────────────────────────────

    private static async Task DownloadFileAsync(
        string url, string dest,
        Action<int> onProgress,
        CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total  = response.Content.Headers.ContentLength ?? -1L;
        var buffer = new byte[81920];
        long read  = 0;

        await using var src   = await response.Content.ReadAsStreamAsync(ct);
        await using var dest_ = File.Create(dest);

        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dest_.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
                onProgress(5 + (int)(read * 90 / total));
        }
    }

    private static void ExtractZip(string zipPath, string destDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        // Strip a single top-level folder prefix if present
        var topDirs = zip.Entries
            .Select(e => e.FullName.Split('/')[0])
            .Distinct()
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        string prefix = (topDirs.Count == 1 && !topDirs[0].EndsWith(".exe"))
            ? topDirs[0] + "/"
            : "";

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var relative = prefix.Length > 0 && entry.FullName.StartsWith(prefix)
                ? entry.FullName[prefix.Length..]
                : entry.FullName;
            var fullDest = Path.Combine(destDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
            entry.ExtractToFile(fullDest, overwrite: true);
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  PortableToolItem  —  VM for a single tool card
// ════════════════════════════════════════════════════════════════════════════

public sealed partial class PortableToolItem : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cts;

    // ── Identity ──────────────────────────────────────────────────────────
    public string   Id          { get; }
    public string   Name        { get; }
    public string   Description { get; }
    public string   IconGlyph   { get; }
    public WpfBrush TileBrush   { get; }

    // ── Tool type ─────────────────────────────────────────────────────────
    private readonly string? _installDir;
    private readonly string? _exeName;
    private readonly string? _scriptCommand;
    private readonly Func<Action<int>, Action<string>, CancellationToken, Task>? _downloadFunc;

    public bool IsScript      => _scriptCommand is not null;
    public bool NeedsDownload => _downloadFunc is not null;
    public bool IsInstalled   => _exeName is not null
                              && File.Exists(Path.Combine(_installDir!, _exeName));

    // ── Observable state ──────────────────────────────────────────────────
    [ObservableProperty] private bool   _isDownloading;
    [ObservableProperty] private int    _progress;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _error      = "";

    // ── Derived display ───────────────────────────────────────────────────
    public string ButtonLabel => IsScript    ? "Run"
                               : IsInstalled ? "Open"
                               :               "Download";

    public string SubLabel
    {
        get
        {
            if (IsScript)    return "Runs via PowerShell · no install needed";
            if (IsInstalled)
            {
                try
                {
                    var rel = Path.GetRelativePath(AppContext.BaseDirectory, _installDir!);
                    return $"Installed  ·  {rel}";
                }
                catch { return "Installed"; }
            }
            return "Not downloaded yet";
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────
    public IAsyncRelayCommand ActionCommand { get; }
    public RelayCommand        CancelCommand { get; }

    // ── Constructors ──────────────────────────────────────────────────────

    /// Downloadable tool
    public PortableToolItem(
        string id, string name, string description, string iconGlyph, WpfBrush tileBrush,
        string installDir, string exeName,
        Func<Action<int>, Action<string>, CancellationToken, Task> downloadFunc)
    {
        Id = id; Name = name; Description = description;
        IconGlyph = iconGlyph; TileBrush = tileBrush;
        _installDir = installDir; _exeName = exeName; _downloadFunc = downloadFunc;
        ActionCommand = new AsyncRelayCommand(ExecuteAsync);
        CancelCommand = new RelayCommand(Cancel);
    }

    /// Script tool — no download, runs a PowerShell one-liner
    public PortableToolItem(
        string id, string name, string description, string iconGlyph, WpfBrush tileBrush,
        string scriptCommand)
    {
        Id = id; Name = name; Description = description;
        IconGlyph = iconGlyph; TileBrush = tileBrush;
        _scriptCommand = scriptCommand;
        ActionCommand  = new AsyncRelayCommand(ExecuteAsync);
        CancelCommand  = new RelayCommand(Cancel);
    }

    // ── Execution ─────────────────────────────────────────────────────────

    private async Task ExecuteAsync()
    {
        Error = "";

        if (IsScript)
        {
            RunScript();
            return;
        }

        if (!IsInstalled)
        {
            await DownloadAndInstallAsync();
            if (!IsInstalled) return;
        }

        LaunchExe();
    }

    private async Task DownloadAndInstallAsync()
    {
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        Progress      = 0;
        StatusText    = "Starting…";
        RefreshDisplay();

        try
        {
            await _downloadFunc!(
                p => UIInvoke(() => Progress   = p),
                s => UIInvoke(() => StatusText = s),
                _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            Error      = ex.Message;
            StatusText = "";
        }
        finally
        {
            IsDownloading = false;
            _cts?.Dispose();
            _cts = null;
            RefreshDisplay();
        }
    }

    private void LaunchExe()
    {
        var exe = Path.Combine(_installDir!, _exeName!);
        if (!File.Exists(exe)) { Error = "Executable not found after install."; return; }
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute  = true,
                WorkingDirectory = _installDir!,
            });
        }
        catch (Exception ex) { Error = $"Launch failed: {ex.Message}"; }
    }

    private void RunScript()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -ExecutionPolicy Bypass -Command \"{_scriptCommand}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Error = $"Failed to run: {ex.Message}"; }
    }

    private void Cancel() => _cts?.Cancel();

    private void RefreshDisplay()
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(ButtonLabel));
        OnPropertyChanged(nameof(SubLabel));
    }

    private static void UIInvoke(Action a) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(a);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
