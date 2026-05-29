using System;
using System.IO;
using System.Threading.Tasks;

namespace Flow.Services;

/// <summary>
/// One-stop "undo everything Flow has done" service.
/// Used by the Settings page's big red Cleanup button, and on uninstall.
/// </summary>
public static class CleanupManager
{
    public class Report
    {
        public bool FirewallRulesRemoved { get; set; }
        public bool QosPoliciesRemoved { get; set; }
        public bool DataFolderRemoved { get; set; }
        public string? Error { get; set; }

        public override string ToString() =>
            $"Firewall rules cleared: {FirewallRulesRemoved}\n" +
            $"QoS policies cleared:   {QosPoliciesRemoved}\n" +
            $"Data folder removed:    {DataFolderRemoved}" +
            (Error is null ? "" : $"\nNote: {Error}");
    }

    /// <summary>
    /// Removes everything Flow has put on the system.
    /// </summary>
    /// <param name="removeDataFolder">If true, also deletes %LocalAppData%\Flow\</param>
    public static async Task<Report> RunAsync(bool removeDataFolder)
    {
        var report = new Report();

        try
        {
            report.FirewallRulesRemoved = await FirewallManager.RemoveAllFlowRulesAsync();
            // Also remove any inbound throttle blocks created by DownloadThrottler
            await DownloadThrottler.RemoveAllAsync();
        }
        catch (Exception ex) { report.Error = "Firewall cleanup: " + ex.Message; }

        try
        {
            report.QosPoliciesRemoved = await QosManager.RemoveAllFlowPoliciesAsync();
        }
        catch (Exception ex) { report.Error = (report.Error is null ? "" : report.Error + "\n") + "QoS cleanup: " + ex.Message; }

        if (removeDataFolder)
        {
            try
            {
                if (Directory.Exists(App.DataFolder))
                {
                    Directory.Delete(App.DataFolder, recursive: true);
                }
                report.DataFolderRemoved = true;
            }
            catch (Exception ex)
            {
                report.Error = (report.Error is null ? "" : report.Error + "\n") + "Data folder: " + ex.Message;
            }
        }

        return report;
    }
}
