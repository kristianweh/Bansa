using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Bansa.Services;

/// <summary>
/// Wraps Windows' built-in QoS throttling by writing to the Group Policy QoS
/// registry path read directly by the ms_pacer (QoS Packet Scheduler) kernel driver.
///
/// HOW IT WORKS:
/// HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS\{PolicyName} is read by the
/// Packet Scheduler on each new socket connection. When a process opens a TCP/UDP
/// socket that matches the "Application Name" value, the scheduler applies the
/// Throttle Rate cap at the kernel level. This is a true hard cap for outbound
/// (upload) traffic, using the same Windows built-in bandwidth policy mechanism
/// available since Windows Vista.
///
/// WHY NOT WMI (MSFT_NetQosPolicySettingData):
/// The System.Management API throws ManagementStatus.NotFound when creating new
/// instances of MSFT_NetQosPolicySettingData because that class requires the
/// newer CIM session interface (Microsoft.Management.Infrastructure), not the
/// legacy WMI ManagementClass.CreateInstance().Put() path. Direct registry writes
/// bypass this entirely and avoid spawning powershell.exe which triggers Defender.
///
/// THROTTLE RATE UNIT: bits per second (consistent with ThrottleRateActionBitsPerSecond
/// in the PowerShell New-NetQosPolicy cmdlet and the ms_pacer internals).
///
/// Reversibility: every policy key is named with the prefix "Bansa-Qos-".
/// CleanupManager removes them all in one call. They are visible in
/// HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS for manual inspection.
/// </summary>
public static class QosManager
{
    public const string PolicyPrefix = "Bansa-Qos-";

    // The Group Policy QoS registry root — read by ms_pacer on every new connection.
    private const string QosRegBase = @"SOFTWARE\Policies\Microsoft\Windows\QoS";

    public class Outcome
    {
        public bool Success { get; init; }
        public string? Detail { get; init; }
        public override string ToString() => Success ? "OK" : (Detail ?? "failed");
    }

    public static Task<Outcome> SetUploadLimitAsync(string exePath, int kiloBytesPerSec)
        => Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(exePath))
                return new Outcome { Success = false, Detail = "No executable path." };
            try
            {
                string policyName = MakePolicyName(exePath, "limit");
                string appName    = Path.GetFileName(exePath);

                DeletePolicy(policyName);
                if (kiloBytesPerSec <= 0)
                {
                    FlushQosPolicy();
                    return new Outcome { Success = true };
                }

                long bps = (long)kiloBytesPerSec * 1024 * 8;
                WritePolicy(policyName, appName, throttleBps: bps, dscp: -1);
                FlushQosPolicy();
                return new Outcome { Success = true };
            }
            catch (Exception ex)
            {
                return new Outcome { Success = false, Detail = ex.Message };
            }
        });

    /// <summary>
    /// Sets (or removes) a system-wide upload cap — empty Application Name matches
    /// ALL traffic from all apps. Prevents the upstream buffer from filling, which
    /// eliminates the latency spike that kills gaming when any app saturates upload.
    /// kiloBytesPerSec = 0 removes the cap.
    /// </summary>
    public static Task<Outcome> SetGlobalUploadCapAsync(int kiloBytesPerSec)
        => Task.Run(() =>
        {
            try
            {
                const string policyName = PolicyPrefix + "Global-Upload";
                DeletePolicy(policyName);
                if (kiloBytesPerSec <= 0)
                {
                    FlushQosPolicy();
                    return new Outcome { Success = true };
                }

                long bps = (long)kiloBytesPerSec * 1024 * 8;
                // Empty Application Name = policy applies to all processes.
                WritePolicy(policyName, appName: "", throttleBps: bps, dscp: -1);
                FlushQosPolicy();
                return new Outcome { Success = true };
            }
            catch (Exception ex)
            {
                return new Outcome { Success = false, Detail = ex.Message };
            }
        });

    public static Task<bool> RemovePolicyAsync(string policyName)
        => Task.Run(() =>
        {
            try { DeletePolicy(policyName); return true; }
            catch { return false; }
        });

    public static Task<bool> RemoveAllBansaPoliciesAsync()
        => Task.Run(() =>
        {
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(QosRegBase, writable: true);
                if (baseKey == null) return true; // nothing to clean up

                // Broad match: any QoS policy whose name contains "Bansa",
                // regardless of what prefix an older version used.
                var toDelete = baseKey.GetSubKeyNames()
                    .Where(n => n.Contains("Bansa", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (var name in toDelete)
                    try { baseKey.DeleteSubKey(name, throwOnMissingSubKey: false); } catch { }
                return true;
            }
            catch { return false; }
        });

    /// <summary>
    /// One-time check that the QoS Packet Scheduler driver is present and active.
    /// Returns null if prerequisites are met; an explanatory string otherwise.
    ///
    /// Windows naming history:
    ///   XP/Vista/7  → service key "PktSched"
    ///   Win 10/11   → kernel component exposed as "Pacer" (pacer.sys)
    /// Both names map to the same ms_pacer kernel driver that enforces QoS policies.
    /// </summary>
    public static Task<string?> DiagnosePrerequisitesAsync()
        => Task.Run(() =>
        {
            try
            {
                // Step 1: verify the Psched kernel driver is registered.
                // Windows registers the QoS Packet Scheduler under different names across versions:
                //   Win 10/11 (most common) → "Psched"
                //   Legacy Win 7 / Server   → "PktSched"
                //   Rarely seen             → "Pacer" (pacer.sys reference)
                using var psched   = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Psched");
                using var pacerKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Pacer");
                using var pktSched = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PktSched");

                if (psched == null && pacerKey == null && pktSched == null)
                    return "The QoS Packet Scheduler (ms_pacer) service was not found. " +
                           "Upload limits won't be enforced. Enable it via: Network Connections → " +
                           "adapter Properties → tick \"QoS Packet Scheduler\".";

                // Step 2: check adapter binding — only applicable on Windows 8 and earlier.
                // On Windows 10/11, Psched is integrated into the networking stack and the
                // Linkage subkey is never created; QoS policies are enforced automatically
                // without per-adapter binding. Treat a missing Linkage key as "modern Windows — OK".
                // Only warn when Linkage exists AND Bind is null/empty (old Windows, genuinely unbound).
                using var linkage = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Psched\Linkage");
                if (linkage != null)
                {
                    var binds = linkage.GetValue("Bind") as string[];
                    if (binds == null || binds.Length == 0)
                        return "QoS Packet Scheduler is installed but not bound to any network adapter — " +
                               "upload limits will have no effect. Fix: Network Connections → " +
                               "right-click your active adapter → Properties → tick \"QoS Packet Scheduler\".";
                }

                return null; // driver present — QoS policies will be enforced
            }
            catch { return null; } // can't diagnose — assume OK
        });

    // ── userenv.dll — Group Policy refresh ───────────────────────────────────

    // After writing to HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS, the ms_pacer
    // driver only picks up the new policy for NEW socket connections by default.
    // RefreshPolicyEx(bMachine=true, RP_FORCE) signals the Group Policy client to
    // re-push all machine-level policies (including QoS) to the kernel driver, so
    // existing TCP/UDP flows get re-classified under the new throttle rate
    // immediately — no need to close and reopen connections.
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool RefreshPolicyEx(bool bMachine, uint dwOptions);
    private const uint RP_FORCE = 1;

    private static void FlushQosPolicy()
    {
        try { RefreshPolicyEx(true, RP_FORCE); } catch { }
    }

    // ── Registry helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Writes one Group Policy QoS policy key. ms_pacer evaluates these on every
    /// new socket connection opened by the matching application.
    /// </summary>
    private static void WritePolicy(string policyName, string appName, long throttleBps, int dscp)
    {
        using var key = Registry.LocalMachine.CreateSubKey(
            $@"{QosRegBase}\{policyName}", writable: true)
            ?? throw new InvalidOperationException(
                "Cannot create QoS registry key — verify the process is running as administrator.");

        // All values are REG_SZ. Wildcards ("*") match any value for that field.
        key.SetValue("Version",                   "1.0",   RegistryValueKind.String);
        key.SetValue("Application Name",          appName, RegistryValueKind.String);
        key.SetValue("Protocol",                  "*",     RegistryValueKind.String);
        key.SetValue("Local Port",                "*",     RegistryValueKind.String);
        key.SetValue("Remote Port",               "*",     RegistryValueKind.String);
        key.SetValue("Local IP",                  "*",     RegistryValueKind.String);
        key.SetValue("Local IP Prefix Length",    "*",     RegistryValueKind.String);
        key.SetValue("Remote IP",                 "*",     RegistryValueKind.String);
        key.SetValue("Remote IP Prefix Length",   "*",     RegistryValueKind.String);
        key.SetValue("DSCP Value",
            dscp < 0 ? "-1" : dscp.ToString(), RegistryValueKind.String);
        key.SetValue("Throttle Rate",
            throttleBps <= 0 ? "-1" : throttleBps.ToString(), RegistryValueKind.String);
    }

    private static void DeletePolicy(string policyName)
    {
        Registry.LocalMachine.DeleteSubKey(
            $@"{QosRegBase}\{policyName}", throwOnMissingSubKey: false);
    }

    private static string MakePolicyName(string exePath, string suffix)
    {
        var fileName = Path.GetFileNameWithoutExtension(exePath);
        var sb = new System.Text.StringBuilder(Math.Min(fileName.Length, 20));
        foreach (char c in fileName)
        {
            if (sb.Length == 20) break;
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                sb.Append(c);
        }
        return $"{PolicyPrefix}{(sb.Length > 0 ? sb.ToString() : "app")}-{suffix}";
    }
}
