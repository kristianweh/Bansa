using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Flow.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

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
        DefaultRequestHeaders = { { "User-Agent", "Flow-App/1.0" } }
    };

    public ToolsViewModel()
    {
        // ── OpenRGB ───────────────────────────────────────────────────────
        Tools.Add(new PortableToolItem(
            "openrgb",
            "OpenRGB",
            "Control RGB lighting on keyboards, mice, GPUs and more.",
            "",
            Path.Combine(App.DataFolder, "Tools", "OpenRGB"),
            "OpenRGB.exe",
            async (prog, status, ct) =>
                await new OpenRgbDownloader().DownloadAsync(prog, status, ct)));

        // ── HWiNFO ────────────────────────────────────────────────────────
        var hwInfoDir = Path.Combine(App.DataFolder, "Tools", "HWiNFO");
        Tools.Add(new PortableToolItem(
            "hwinfo",
            "HWiNFO",
            "Comprehensive hardware information and real-time system monitoring.",
            "",
            hwInfoDir,
            "HWiNFO64.exe",
            async (prog, status, ct) =>
                await DownloadHwInfoAsync(hwInfoDir, prog, status, ct)));

        // ── Chris Titus WinUtil  (script — no download) ───────────────────
        Tools.Add(new PortableToolItem(
            "cttwintool",
            "Chris Titus WinUtil",
            "Windows tweaks, debloating, privacy fixes and optimizations.",
            "",
            scriptCommand: "irm christitus.com/win | iex"));
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
        try
        {
            var html = await Http.GetStringAsync("https://www.hwinfo.com/downloads/", ct);
            var match = Regex.Match(html,
                @"https://[^""'<>]*hwi_\d+\.zip",
                RegexOptions.IgnoreCase);
            if (match.Success)
                return (match.Value, Path.GetFileName(match.Value));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to error */ }

        throw new InvalidOperationException(
            "Could not find HWiNFO download on hwinfo.com. " +
            "Visit https://www.hwinfo.com/downloads/ to download manually.");
    }

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
    public string Id          { get; }
    public string Name        { get; }
    public string Description { get; }
    public string IconGlyph   { get; }

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
            if (IsScript)    return "Runs via PowerShell — no install needed";
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
        string id, string name, string description, string iconGlyph,
        string installDir, string exeName,
        Func<Action<int>, Action<string>, CancellationToken, Task> downloadFunc)
    {
        Id = id; Name = name; Description = description; IconGlyph = iconGlyph;
        _installDir = installDir; _exeName = exeName; _downloadFunc = downloadFunc;
        ActionCommand = new AsyncRelayCommand(ExecuteAsync);
        CancelCommand = new RelayCommand(Cancel);
    }

    /// Script tool — no download, runs a PowerShell one-liner
    public PortableToolItem(
        string id, string name, string description, string iconGlyph,
        string scriptCommand)
    {
        Id = id; Name = name; Description = description; IconGlyph = iconGlyph;
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
            if (!IsInstalled) return;   // failed or cancelled
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
            // Opens a new visible PowerShell window — the CTT script has its own UI
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
