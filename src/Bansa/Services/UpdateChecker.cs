using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Bansa.Services;

/// <summary>
/// Manual update check against the GitHub Releases API. Only runs when the user clicks
/// "Check for updates" in Settings → General — Bansa never phones home on its own.
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/kristianweh/Bansa/releases/latest";

    public sealed record Result(
        bool Success, bool UpdateAvailable, string LatestTag, string ReleaseUrl, string Error);

    public static async Task<Result> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Bansa");   // GitHub API rejects UA-less requests
            var json = await http.GetStringAsync(LatestReleaseApi).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";

            var latest  = ParseVersion(tag);
            var current = GetCurrentVersion();
            bool newer  = latest is not null && current is not null && latest > current;
            return new Result(true, newer, tag, url, "");
        }
        catch (Exception ex)
        {
            Log.Debug("Update check", ex);
            return new Result(false, false, "", "",
                ex is HttpRequestException or TaskCanceledException ? "network error" : ex.Message);
        }
    }

    private static Version? GetCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        return ParseVersion(v);
    }

    /// <summary>Parses "v1.2", "1.2.3", or "1.2.3+sha" into a comparable Version.</summary>
    internal static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        if (!s.Contains('.')) s += ".0";          // "v2" → "2.0" so Version.TryParse accepts it
        return Version.TryParse(s, out var v) ? v : null;
    }
}
