using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Services;

/// <summary>
/// Lightweight built-in speed tester used to verify that bandwidth limits are working.
///
/// Two roles:
///  1. SELF-TEST: download/upload directly from Flow.exe. Useful for proving
///     QoS upload limits work (set a limit on Flow.exe → run this → confirm
///     achieved rate matches the limit).
///  2. EXTERNAL: launches a browser to fast.com / speedtest.net so the user
///     can test under their preferred app (e.g. measure Chrome's traffic
///     under a Chrome-specific limit).
/// </summary>
public class SpeedTester
{
    // Cloudflare's speed-test endpoint — small, stable, low-friction.
    // Downloads 25 MB by default; sized to be quick on most connections.
    private const string DefaultDownloadUrl = "https://speed.cloudflare.com/__down?bytes=26214400";

    public event Action<double>? ProgressMbps;  // throughput in Mbps as it runs

    public async Task<double> RunDownloadTestAsync(CancellationToken token = default, string? url = null)
    {
        url ??= DefaultDownloadUrl;
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        var sw = Stopwatch.StartNew();
        long totalBytes = 0;
        long lastReportBytes = 0;
        var lastReport = sw.Elapsed;

        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(token);

            var buf = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buf, 0, buf.Length, token)) > 0)
            {
                totalBytes += read;

                var now = sw.Elapsed;
                if ((now - lastReport).TotalMilliseconds >= 250)
                {
                    var bytes = totalBytes - lastReportBytes;
                    var seconds = (now - lastReport).TotalSeconds;
                    var mbps = (bytes * 8.0 / 1_000_000) / Math.Max(0.001, seconds);
                    try { ProgressMbps?.Invoke(mbps); } catch { }
                    lastReportBytes = totalBytes;
                    lastReport = now;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"Speed test error: {ex.Message}"); }

        sw.Stop();
        var avgMbps = (totalBytes * 8.0 / 1_000_000) / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        return avgMbps;
    }

    public static void OpenExternalSpeedTest()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://fast.com",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Debug.WriteLine($"Failed to open browser: {ex.Message}"); }
    }
}
