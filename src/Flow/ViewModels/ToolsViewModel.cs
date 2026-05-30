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

    // General API requests (GitHub, Codeberg)
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders =
        {
            { "User-Agent",  "Flow-App/1.0" },
            { "Accept",      "application/json" },
        }
    };

    // Some download servers (e.g. hwinfo.com) validate the browser UA / Referer.
    // Use a separate client that looks like a real browser for those downloads.
    private static readonly HttpClient BrowserHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(120),
        DefaultRequestHeaders =
        {
            { "User-Agent",     "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
            { "Accept",         "*/*" },
            { "Accept-Language","en-US,en;q=0.9" },
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
                await DownloadHwInfoAsync(hwInfoDir, prog, status, ct),
            websiteUrl:  "https://www.hwinfo.com/download/"));

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
        // hwinfo.com checks the browser User-Agent and Referer; use BrowserHttp
        await DownloadFileAsync(url, zipPath, onProgress, ct,
            referer: "https://www.hwinfo.com/download/", useBrowserClient: true);

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
        // HWiNFO's website uses JavaScript-rendered download buttons, so scraping the page
        // fails.  Instead we query the WinGet package manifest index on GitHub — it's always
        // up to date and gives us the authoritative version number.  The direct portable ZIP
        // URL follows the predictable pattern: hwinfo.com/files/hwi_{major}{minor}.zip
        // e.g., v8.16 → hwi_816.zip

        var json = await Http.GetStringAsync(
            "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/r/REALiX/HWiNFO",
            ct);

        using var doc = JsonDocument.Parse(json);

        // Version directory names look like "8.16" or "8.16.0" — take the highest
        var latestVersion = doc.RootElement
            .EnumerateArray()
            .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "dir")
            .Select(e => e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
            .Where(s => s.Length > 0)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();

        if (string.IsNullOrEmpty(latestVersion))
            throw new InvalidOperationException("Could not determine latest HWiNFO version from WinGet.");

        // Build version code: "8.16" → "816",  "8.16.0" → "816"
        var parts       = latestVersion.Split('.');
        var versionCode = parts[0] + (parts.Length > 1 ? parts[1] : "00");

        var url  = $"https://www.hwinfo.com/files/hwi_{versionCode}.zip";
        var name = $"hwi_{versionCode}.zip";

        return (url, name);
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
        CancellationToken ct,
        string? referer = null,
        bool useBrowserClient = false)
    {
        var client = useBrowserClient ? BrowserHttp : Http;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer is not null)
            req.Headers.TryAddWithoutValidation("Referer", referer);
        using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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

    public string? WebsiteUrl { get; }

    public bool IsScript      => _scriptCommand is not null;
    public bool NeedsDownload => _downloadFunc is not null;
    public bool IsInstalled   => _exeName is not null
                              && File.Exists(Path.Combine(_installDir!, _exeName));

    // ── Observable state ──────────────────────────────────────────────────
    [ObservableProperty] private bool   _isDownloading;
    [ObservableProperty] private int    _progress;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _error      = "";

    // Notify ShowFallbackButton whenever Error changes
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(ShowFallbackButton));

    // ── Derived display ───────────────────────────────────────────────────
    public string ButtonLabel      => IsScript    ? "Run"
                                    : IsInstalled ? "Open"
                                    :               "Download";

    /// True when a download attempt failed and a website fallback URL is available
    public bool ShowFallbackButton => WebsiteUrl is not null && !string.IsNullOrEmpty(Error);

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
    public RelayCommand?       OpenWebsiteCommand { get; }

    // ── Constructors ──────────────────────────────────────────────────────

    /// Downloadable tool
    public PortableToolItem(
        string id, string name, string description, string iconGlyph, WpfBrush tileBrush,
        string installDir, string exeName,
        Func<Action<int>, Action<string>, CancellationToken, Task> downloadFunc,
        string? websiteUrl = null)
    {
        Id = id; Name = name; Description = description;
        IconGlyph = iconGlyph; TileBrush = tileBrush;
        _installDir = installDir; _exeName = exeName; _downloadFunc = downloadFunc;
        WebsiteUrl = websiteUrl;
        ActionCommand      = new AsyncRelayCommand(ExecuteAsync);
        CancelCommand      = new RelayCommand(Cancel);
        if (websiteUrl is not null)
            OpenWebsiteCommand = new RelayCommand(() =>
                Process.Start(new ProcessStartInfo(websiteUrl) { UseShellExecute = true }));
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
        OnPropertyChanged(nameof(ShowFallbackButton));
    }

    private static void UIInvoke(Action a) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(a);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
