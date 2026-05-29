using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Services;

/// <summary>
/// Background pinger. Shells out to ping.exe so it works on every Windows
/// configuration without raw-socket permission issues.
/// </summary>
public sealed class PingMonitor : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _task;
    private readonly string _target;

    public int CurrentRttMs { get; private set; } = -1;
    public string Status { get; private set; } = "—";

    public event Action<int, string>? Updated;

    // Matches "time=14ms", "time<1ms", "Time=14ms" etc.
    private static readonly Regex RttRegex =
        new(@"[Tt]ime[=<](\d+)\s*ms", RegexOptions.Compiled);

    public PingMonitor(string target = "8.8.8.8")
    {
        _target = string.IsNullOrWhiteSpace(target) ? "8.8.8.8" : target;
    }

    public void Start()
    {
        if (_task is not null) return;
        _cts  = new CancellationTokenSource();
        _task = Task.Run(() => Loop(_cts.Token));
    }

    private async Task Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo("ping.exe")
                    {
                        Arguments             = $"-n 1 -w 2000 {_target}",
                        RedirectStandardOutput = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                    }
                };

                proc.Start();
                string output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync(token);

                var m = RttRegex.Match(output);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int rtt))
                {
                    CurrentRttMs = rtt;
                    Status       = "ok";
                }
                else if (output.IndexOf("time<1", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    CurrentRttMs = 0;
                    Status       = "ok";
                }
                else
                {
                    CurrentRttMs = -1;
                    Status       = "timeout";
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                CurrentRttMs = -1;
                Status       = ex.GetType().Name;
            }

            try { Updated?.Invoke(CurrentRttMs, Status); } catch { }
            try { await Task.Delay(2000, token); } catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _task?.Wait(500); } catch { }
        _cts?.Dispose();
    }
}
