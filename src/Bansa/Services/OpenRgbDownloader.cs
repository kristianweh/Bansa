using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Bansa.Services;

public sealed class OpenRgbDownloader
{
    // Codeberg API — fetch enough releases to find the latest *stable* one
    // (latest may be an rc/alpha/beta; we scan back through up to 10 releases)
    private const string ReleasesApi =
        "https://codeberg.org/api/v1/repos/OpenRGB/OpenRGB/releases?limit=10";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "Bansa-App" } }
    };

    public string InstallDir { get; } =
        Path.Combine(App.DataFolder, "Tools", "OpenRGB");

    public string ExePath => Path.Combine(InstallDir, "OpenRGB.exe");

    public bool IsInstalled => File.Exists(ExePath);

    /// <summary>
    /// Downloads and extracts the latest portable OpenRGB release.
    /// Reports 0–100 progress via <paramref name="onProgress"/> and
    /// status messages via <paramref name="onStatus"/>.
    /// </summary>
    public async Task DownloadAsync(
        Action<int>    onProgress,
        Action<string> onStatus,
        CancellationToken ct = default)
    {
        onStatus("Finding latest OpenRGB release…");
        onProgress(2);

        var (assetUrl, assetName) = await GetLatestAssetAsync(ct);

        onStatus($"Downloading {assetName}…");
        onProgress(5);

        var zipPath = Path.Combine(Path.GetTempPath(), "OpenRGB_portable.zip");
        await DownloadFileAsync(assetUrl, zipPath, onProgress, ct);

        onStatus("Extracting…");
        onProgress(96);

        Directory.CreateDirectory(InstallDir);
        ExtractZip(zipPath, InstallDir);

        try { File.Delete(zipPath); } catch { }

        onStatus("Done");
        onProgress(100);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task<(string Url, string Name)> GetLatestAssetAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync(ReleasesApi, ct);
        using var doc = JsonDocument.Parse(json);

        // Prefer the latest stable release (skip rc/alpha/beta tags).
        // OpenRGB 1.0rc2 requires the PawnIO kernel driver which may not be installed.
        var release = doc.RootElement.EnumerateArray()
            .Where(r => r.ValueKind == JsonValueKind.Object)
            .Where(r =>
            {
                var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                return !tag.Contains("rc",    StringComparison.OrdinalIgnoreCase) &&
                       !tag.Contains("alpha", StringComparison.OrdinalIgnoreCase) &&
                       !tag.Contains("beta",  StringComparison.OrdinalIgnoreCase);
            })
            .FirstOrDefault();

        // Fall back to whatever is newest if every release is pre-release
        if (release.ValueKind != JsonValueKind.Object)
            release = doc.RootElement.EnumerateArray()
                .FirstOrDefault(r => r.ValueKind == JsonValueKind.Object);

        if (release.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("No releases found on Codeberg.");

        if (!release.TryGetProperty("assets", out var assets))
            throw new InvalidOperationException("Release has no assets array.");

        // Prefer Windows 64-bit portable zip
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url  = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";

            if (name.Contains("Windows_64", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return (url, name);
        }

        // Fallback: any Windows zip
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url  = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";

            if (name.Contains("Windows", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return (url, name);
        }

        throw new InvalidOperationException(
            "Could not find a Windows 64-bit portable zip in the latest OpenRGB release.");
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

        // Detect and strip a single top-level folder prefix if present
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
