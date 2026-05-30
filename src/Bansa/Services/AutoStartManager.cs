using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Bansa.Services;

/// <summary>
/// Manages a Windows Task Scheduler entry that launches Bansa on user logon with
/// highest privileges (required because Bansa needs administrator rights for ETW).
///
/// Uses schtasks.exe so no third-party dependency is needed.
/// Registry run-key approaches skip UAC elevation on startup, which would break ETW.
/// </summary>
public static class AutoStartManager
{
    private const string TaskName = "Bansa_BandwidthMonitor_AutoStart";

    /// <summary>
    /// Creates or removes the scheduled task.
    /// The task runs as the current user with highest available privileges at logon.
    /// </summary>
    public static async Task<bool> SetAsync(bool enable)
    {
        try
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName
                             ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Bansa.exe");

            string args = enable
                ? $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\"\" " +
                  $"/sc onlogon /rl highest /f"
                : $"/delete /tn \"{TaskName}\" /f";

            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute  = false,
                CreateNoWindow   = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns true if the Bansa auto-start task currently exists.</summary>
    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }
}
