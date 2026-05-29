using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Flow.Services;

/// <summary>
/// Manages app-level "Block" actions by adding Windows Defender Firewall rules
/// via the Windows Firewall COM API (HNetCfg.FwPolicy2).
///
/// WHY COM INSTEAD OF POWERSHELL:
/// The previous implementation spawned powershell.exe with New-NetFirewallRule.
/// Windows Defender's behavior monitoring treats "unsigned process + powershell.exe
/// spawn + New-NetFirewallRule" as a high-confidence malware indicator — it matches
/// the exact pattern used by ransomware droppers to disable network traffic.
/// Using the COM API directly (HNetCfg.FwPolicy2) is the same underlying mechanism
/// that Windows Security Center, AV software, and firewall managers all use.
/// Defender recognizes it as a legitimate administrative path.
///
/// Reversibility: every rule we create is named with the prefix "Flow-".
/// CleanupManager removes them all in one call. Rules are visible in Windows
/// Defender Firewall's UI for manual inspection/removal.
/// </summary>
public static class FirewallManager
{
    public const string RulePrefix = "Flow-Block-";

    // NET_FW constants (netfw.h)
    private const int NET_FW_ACTION_BLOCK = 0;
    private const int NET_FW_RULE_DIR_IN  = 1;
    private const int NET_FW_RULE_DIR_OUT = 2;
    private const int NET_FW_PROFILE2_ALL = 0x7FFFFFFF; // Domain | Private | Public

    /// <summary>
    /// Block all inbound and outbound traffic for the given executable.
    /// Creates two rules (one per direction).
    /// </summary>
    public static Task<bool> BlockAppAsync(string exePath)
        => Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
            try
            {
                dynamic rules = GetFwRules();
                string baseName = MakeRuleName(exePath);
                AddOrReplaceRule(rules, baseName + "-Out", exePath, NET_FW_RULE_DIR_OUT);
                AddOrReplaceRule(rules, baseName + "-In",  exePath, NET_FW_RULE_DIR_IN);
                return true;
            }
            catch { return false; }
        });

    public static Task<bool> UnblockAppAsync(string exePath)
        => Task.Run(() =>
        {
            try
            {
                dynamic rules = GetFwRules();
                string baseName = MakeRuleName(exePath);
                TryRemoveRule(rules, baseName + "-Out");
                TryRemoveRule(rules, baseName + "-In");
                return true;
            }
            catch { return false; }
        });

    public static Task<bool> IsBlockedAsync(string exePath)
        => Task.Run(() =>
        {
            try
            {
                dynamic rules = GetFwRules();
                // INetFwRules.Item(name) throws if the rule doesn't exist.
                _ = rules[MakeRuleName(exePath) + "-Out"];
                return true;
            }
            catch { return false; }
        });

    /// <summary>Used by CleanupManager.</summary>
    public static Task<bool> RemoveAllFlowRulesAsync()
        => Task.Run(() =>
        {
            try
            {
                dynamic rules = GetFwRules();
                // Enumerate all rules, collect names with our prefix, then remove.
                // Two-pass avoids modifying the collection while iterating.
                var toRemove = new List<string>();
                foreach (dynamic rule in (System.Collections.IEnumerable)rules)
                {
                    var name = (string)rule.Name;
                    if (name.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase))
                        toRemove.Add(name);
                }
                foreach (var n in toRemove) TryRemoveRule(rules, n);
                return true;
            }
            catch { return false; }
        });

    // ── COM helpers ────────────────────────────────────────────────────────────

    private static dynamic GetFwRules()
    {
        var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException("HNetCfg.FwPolicy2 not registered.");
        dynamic policy = Activator.CreateInstance(t)!;
        return policy.Rules;
    }

    private static void AddOrReplaceRule(dynamic rules, string name, string exePath, int direction)
    {
        // Remove first so re-applying a block is always idempotent.
        TryRemoveRule(rules, name);

        var t = Type.GetTypeFromProgID("HNetCfg.FwRule")
            ?? throw new InvalidOperationException("HNetCfg.FwRule not registered.");
        dynamic r = Activator.CreateInstance(t)!;
        r.Name            = name;
        r.ApplicationName = exePath;
        r.Action          = NET_FW_ACTION_BLOCK;
        r.Direction       = direction;
        r.Enabled         = true;
        r.Profiles        = NET_FW_PROFILE2_ALL;
        rules.Add(r);
    }

    private static void TryRemoveRule(dynamic rules, string name)
    {
        try { rules.Remove(name); } catch { /* not present — fine */ }
    }

    private static string MakeRuleName(string exePath)
    {
        var fn   = Path.GetFileNameWithoutExtension(exePath);
        var safe = new string(fn.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.').ToArray());
        if (string.IsNullOrEmpty(safe)) safe = "app";
        return RulePrefix + safe;
    }
}
