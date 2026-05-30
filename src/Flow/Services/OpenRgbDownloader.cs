using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Flow.Services;

public sealed class OpenRgbDownloader
{
    // GitLab project ID for CalcProgrammer1/OpenRGB
    private const string ReleasesApi =
        "https://gitlab.com/api/v4/projects/9582376/releases";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "Flow-App" } }
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
        onProgress(0);

        var assetUrl = await GetLatestAssetUrlAsync(ct);

        onStatus("Downloading OpenRGB portable…");
        onProgress(5);

        var zipPath = Path.Combine(Path.GetTempPath(), "OpenRGB_portable.zip");
        await DownloadFileAsync(assetUrl, zipPath, onProgress, ct);

        onStatus("Extracting…");
        onProgress(95);

        Directory.CreateDirectory(InstallDir);
        ExtractZip(zipPath, InstallDir);

        try { File.Delete(zipPath); } catch { }

        onStatus("Done");
        onProgress(100);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task<string> GetLatestAssetUrlAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync(ReleasesApi, ct);
        using var doc = JsonDocument.Parse(json);

        // Releases are returned newest-first
        var release = doc.RootElement.EnumerateArray().First();
        var assets  = release.GetProperty("assets").GetProperty("links");

        foreach (var link in assets.EnumerateArray())
        {
            var name = link.GetProperty("name").GetString() ?? "";
            // Match the Windows 64-bit portable zip
            if (name.Contains("Windows", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("64",      StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".zip",    StringComparison.OrdinalIgnoreCase))
            {
                return link.GetProperty("url").GetString()
                    ?? throw new InvalidOperationException("Asset URL missing");
            }
        }

        // Broader fallback — any zip for Windows/AMD64
        foreach (var link in assets.EnumerateArray())
        {
            var name = link.GetProperty("name").GetString() ?? "";
            if ((name.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("AMD64",   StringComparison.OrdinalIgnoreCase)) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return link.GetProperty("url").GetString()
                    ?? throw new InvalidOperationException("Asset URL missing");
            }
        }

        throw new InvalidOperationException(
            "Could not find a Windows portable zip in the latest OpenRGB release. " +
            "Check https://gitlab.com/CalcProgrammer1/OpenRGB/-/releases for manual download.");
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

        await using var src  = await response.Content.ReadAsStreamAsync(ct);
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

        // Some releases nest everything under a single root folder in the zip.
        // Detect that and strip the prefix so we always get a flat install dir.
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
            if (entry.FullName.EndsWith('/')) continue;  // directory entry

            var relative = prefix.Length > 0 && entry.FullName.StartsWith(prefix)
                ? entry.FullName[prefix.Length..]
                : entry.FullName;

            var fullDest = Path.Combine(destDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);

            entry.ExtractToFile(fullDest, overwrite: true);
        }
    }
}
