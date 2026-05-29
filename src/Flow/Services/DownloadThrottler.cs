using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Services;

/// <summary>
/// Per-app bandwidth throttler using a token-bucket approach with 100 ms windows.
///
/// HOW IT WORKS (both directions):
/// A true kernel-level rate shaper requires a WFP callout driver. Without one,
/// the closest equivalent is pulsing a Windows Firewall rule on/off at 10 Hz:
///   • Each 100 ms window has a byte budget: limit × 0.1 s.
///   • We diff the raw ETW byte accumulator at window boundaries to measure
///     what ACTUALLY arrived/left — no EMA, no 500 ms lag.
///   • Over budget → add a block rule (inbound for download, outbound for upload).
///   • Under budget next window → remove the rule.
///
/// This gives an AVERAGE rate at the configured limit. The app can burst at wire
/// speed for up to 100 ms before the block kicks in, and the COM API for firewall
/// rule changes takes ~50–100 ms, so the effective granularity is ~200 ms.
/// For background traffic (file downloads, cloud sync, game updates) this is
/// imperceptible. For real-time streams a kernel driver would do better.
///
/// UPLOAD vs QoS:
/// QoS Group Policy (HKLM\…\QoS) only classifies sockets at CREATION time.
/// Any connection open before the policy is written is never capped. The outbound
/// firewall approach here hits existing connections immediately because WFP
/// intercepts every outbound packet, not just new socket setup.
///
/// REVERSIBILITY: "Flow-Throttle-*"  = inbound (download) block rules.
///                "Flow-UpThrottle-*" = outbound (upload) block rules.
/// Both prefixes are removed by CleanupManager / RemoveAllAsync.
/// </summary>
public sealed class DownloadThrottler : IDisposable
{
    public const string ThrottleRulePrefix   = "Flow-Throttle-";
    public const string UpThrottleRulePrefix = "Flow-UpThrottle-";

    private readonly NetworkMonitor _monitor;
    private readonly object _lock = new();
    private readonly Dictionary<string, ThrottleState> _byImagePath =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Threading.Timer _innerTimer;
    private bool _disposed;

    private class ThrottleState
    {
        public required string ImagePath    { get; set;  }   // resolved to running exe; updated on each SetXLimit call
        public required string DownRuleName { get; init; }
        public required string UpRuleName   { get; init; }

        public int DownloadLimitKbps;
        public int UploadLimitKbps;

        // Download (inbound block)
        public long     LastWindowRawBytesIn;
        public long     DownTokenBucket;
        public DateTime DownWindowStart = DateTime.MinValue;
        public bool     CurrentlyBlockingDown;
        public int      DownIdleWindows;   // consecutive 100 ms windows with zero received bytes

        // Upload (outbound block)
        public long     LastWindowRawBytesOut;
        public long     UpTokenBucket;
        public DateTime UpWindowStart = DateTime.MinValue;
        public bool     CurrentlyBlockingUp;
        public int      UpIdleWindows;     // consecutive 100 ms windows with zero sent bytes
    }

    public DownloadThrottler(NetworkMonitor monitor)
    {
        _monitor = monitor;
        _innerTimer = new System.Threading.Timer(_ => InnerTick(), null, 100, 100);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void SetDownloadLimit(string imagePath, int kbps)
    {
        if (string.IsNullOrEmpty(imagePath)) return;
        lock (_lock)
        {
            if (kbps <= 0)
            {
                if (_byImagePath.TryGetValue(imagePath, out var st))
                {
                    if (st.CurrentlyBlockingDown) _ = RemoveBlockAsync(st.DownRuleName);
                    st.DownloadLimitKbps     = 0;
                    st.CurrentlyBlockingDown = false;
                    if (st.UploadLimitKbps <= 0) _byImagePath.Remove(imagePath);
                }
                return;
            }
            var s = GetOrCreate(imagePath);
            var resolved               = _monitor.ResolveImagePath(imagePath);
            s.ImagePath                = resolved;
            s.DownloadLimitKbps        = kbps;
            s.LastWindowRawBytesIn     = _monitor.GetRawBytesIn(resolved);
            s.DownWindowStart          = DateTime.MinValue;
        }
    }

    public void SetUploadLimit(string imagePath, int kbps)
    {
        if (string.IsNullOrEmpty(imagePath)) return;
        lock (_lock)
        {
            if (kbps <= 0)
            {
                if (_byImagePath.TryGetValue(imagePath, out var st))
                {
                    if (st.CurrentlyBlockingUp) _ = RemoveBlockAsync(st.UpRuleName);
                    st.UploadLimitKbps     = 0;
                    st.CurrentlyBlockingUp = false;
                    if (st.DownloadLimitKbps <= 0) _byImagePath.Remove(imagePath);
                }
                return;
            }
            var s = GetOrCreate(imagePath);
            var resolved               = _monitor.ResolveImagePath(imagePath);
            s.ImagePath                = resolved;
            s.UploadLimitKbps          = kbps;
            s.LastWindowRawBytesOut    = _monitor.GetRawBytesOut(resolved);
            s.UpWindowStart            = DateTime.MinValue;
        }
    }

    public int GetDownloadLimit(string imagePath)
    {
        lock (_lock) { return _byImagePath.TryGetValue(imagePath, out var s) ? s.DownloadLimitKbps : 0; }
    }

    public int GetUploadLimit(string imagePath)
    {
        lock (_lock) { return _byImagePath.TryGetValue(imagePath, out var s) ? s.UploadLimitKbps : 0; }
    }

    /// <summary>
    /// Returns true when the inbound block rule for this app is currently ACTIVE
    /// (i.e. the download limit is set and the token bucket is in debt this window).
    /// Matches on the resolved <see cref="ThrottleState.ImagePath"/>, so it works even
    /// when the stored settings key differs from the live ETW path (Squirrel updater, etc.).
    /// </summary>
    public bool IsActivelyThrottlingDown(string imagePath)
    {
        lock (_lock)
        {
            foreach (var st in _byImagePath.Values)
                if (string.Equals(st.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase))
                    return st.CurrentlyBlockingDown;
            return false;
        }
    }

    /// <summary>Returns true when the outbound block rule for this app is currently ACTIVE.</summary>
    public bool IsActivelyThrottlingUp(string imagePath)
    {
        lock (_lock)
        {
            foreach (var st in _byImagePath.Values)
                if (string.Equals(st.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase))
                    return st.CurrentlyBlockingUp;
            return false;
        }
    }

    private ThrottleState GetOrCreate(string imagePath)
    {
        if (!_byImagePath.TryGetValue(imagePath, out var s))
        {
            s = new ThrottleState
            {
                ImagePath    = imagePath,
                DownRuleName = MakeRuleName(ThrottleRulePrefix,   imagePath),
                UpRuleName   = MakeRuleName(UpThrottleRulePrefix, imagePath),
            };
            _byImagePath[imagePath] = s;
        }
        return s;
    }

    // ── Inner 100 ms loop ─────────────────────────────────────────────────────

    private void InnerTick()
    {
        List<(string RuleName, string ExePath, int Dir, bool TurnOn)>? actions = null;
        var now = DateTime.UtcNow;

        lock (_lock)
        {
            foreach (var st in _byImagePath.Values)
            {
                // ── Download (inbound) ───────────────────────────────────────
                if (st.DownloadLimitKbps > 0)
                {
                    long limitBytes = (long)st.DownloadLimitKbps * 1024 / 10;

                    if ((now - st.DownWindowStart).TotalMilliseconds >= 100)
                    {
                        long cur      = _monitor.GetRawBytesIn(st.ImagePath);
                        long received = Math.Max(0, cur - st.LastWindowRawBytesIn);
                        st.LastWindowRawBytesIn = cur;
                        st.DownWindowStart      = now;

                        // Mid-session path staleness: if the app auto-updated (Squirrel, Steam, etc.)
                        // the ETW tally moves to the new path, received stays 0 here. After 30 s
                        // (300 × 100 ms windows) of zero received, re-resolve the path by filename.
                        if (received == 0)
                        {
                            if (++st.DownIdleWindows >= 300) { TryReroutePath(st); st.DownIdleWindows = 0; }
                        }
                        else st.DownIdleWindows = 0;

                        // Carry-over debt: add this window's budget to whatever credit/debt
                        // remains from previous windows, then subtract what was received.
                        // Cap credit at limitBytes so idle time can't build up a burst allowance.
                        // Debt persists until repaid — this is what keeps the average accurate.
                        st.DownTokenBucket = Math.Min(st.DownTokenBucket + limitBytes - received, limitBytes);
                    }

                    bool wantBlock = st.DownTokenBucket < 0;
                    if (wantBlock != st.CurrentlyBlockingDown)
                    {
                        actions ??= new();
                        actions.Add((st.DownRuleName, st.ImagePath, NET_FW_RULE_DIR_IN, wantBlock));
                        st.CurrentlyBlockingDown = wantBlock;
                    }
                }

                // ── Upload (outbound) ────────────────────────────────────────
                if (st.UploadLimitKbps > 0)
                {
                    long limitBytes = (long)st.UploadLimitKbps * 1024 / 10;

                    if ((now - st.UpWindowStart).TotalMilliseconds >= 100)
                    {
                        long cur  = _monitor.GetRawBytesOut(st.ImagePath);
                        long sent = Math.Max(0, cur - st.LastWindowRawBytesOut);
                        st.LastWindowRawBytesOut = cur;
                        st.UpWindowStart         = now;

                        if (sent == 0)
                        {
                            if (++st.UpIdleWindows >= 300) { TryReroutePath(st); st.UpIdleWindows = 0; }
                        }
                        else st.UpIdleWindows = 0;

                        // Same carry-over debt logic as download.
                        st.UpTokenBucket = Math.Min(st.UpTokenBucket + limitBytes - sent, limitBytes);
                    }

                    bool wantBlock = st.UpTokenBucket < 0;
                    if (wantBlock != st.CurrentlyBlockingUp)
                    {
                        actions ??= new();
                        actions.Add((st.UpRuleName, st.ImagePath, NET_FW_RULE_DIR_OUT, wantBlock));
                        st.CurrentlyBlockingUp = wantBlock;
                    }
                }
            }
        }

        if (actions == null) return;
        foreach (var (ruleName, exePath, dir, on) in actions)
        {
            if (on) _ = AddBlockAsync(ruleName, exePath, dir);
            else    _ = RemoveBlockAsync(ruleName);
        }
    }

    // ── Mid-session path re-routing ───────────────────────────────────────────

    /// <summary>
    /// Called when a throttle state has reported zero bytes for 30 seconds.
    /// Asks NetworkMonitor to resolve the path by filename — handles apps that
    /// updated mid-session via Squirrel / Steam, changing their version directory.
    /// Must be called under <c>_lock</c> (already held by InnerTick).
    /// </summary>
    private void TryReroutePath(ThrottleState st)
    {
        var newPath = _monitor.ResolveImagePath(st.ImagePath);
        if (string.Equals(newPath, st.ImagePath, StringComparison.OrdinalIgnoreCase)) return;

        System.Diagnostics.Debug.WriteLine(
            $"DownloadThrottler: rerouted '{st.DownRuleName}' → '{newPath}'");
        st.ImagePath            = newPath;
        st.LastWindowRawBytesIn  = _monitor.GetRawBytesIn(newPath);
        st.LastWindowRawBytesOut = _monitor.GetRawBytesOut(newPath);
        st.DownWindowStart      = DateTime.MinValue;
        st.UpWindowStart        = DateTime.MinValue;
        // Reset idle counters so we don't immediately re-check
        st.DownIdleWindows = 0;
        st.UpIdleWindows   = 0;
    }

    // ── Firewall helpers (Windows Firewall COM API) ───────────────────────────

    private const int NET_FW_ACTION_BLOCK = 0;
    private const int NET_FW_RULE_DIR_IN  = 1;
    private const int NET_FW_RULE_DIR_OUT = 2;
    private const int NET_FW_PROFILE2_ALL = 0x7FFFFFFF;

    private static Task<bool> AddBlockAsync(string ruleName, string exePath, int direction)
        => Task.Run(() =>
        {
            try
            {
                dynamic rules = GetFwRules();
                TryRemoveFwRule(rules, ruleName);

                var t = Type.GetTypeFromProgID("HNetCfg.FwRule")
                    ?? throw new InvalidOperationException("HNetCfg.FwRule not registered.");
                dynamic r = Activator.CreateInstance(t)!;
                r.Name            = ruleName;
                r.ApplicationName = exePath;
                r.Action          = NET_FW_ACTION_BLOCK;
                r.Direction       = direction;
                r.Enabled         = true;
                r.Profiles        = NET_FW_PROFILE2_ALL;
                rules.Add(r);
                return true;
            }
            catch { return false; }
        });

    private static Task<bool> RemoveBlockAsync(string ruleName)
        => Task.Run(() =>
        {
            try { TryRemoveFwRule(GetFwRules(), ruleName); return true; }
            catch { return false; }
        });

    public static Task RemoveAllAsync()
        => Task.Run(() =>
        {
            try
            {
                dynamic rules = GetFwRules();
                var toRemove = new List<string>();
                foreach (dynamic rule in (System.Collections.IEnumerable)rules)
                {
                    var n = (string)rule.Name;
                    if (n.StartsWith(ThrottleRulePrefix,   StringComparison.OrdinalIgnoreCase) ||
                        n.StartsWith(UpThrottleRulePrefix, StringComparison.OrdinalIgnoreCase))
                        toRemove.Add(n);
                }
                foreach (var n in toRemove) TryRemoveFwRule(rules, n);
            }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
        });

    /// <summary>
    /// Clears in-memory limit state, awaits any active block-rule removals, then queries
    /// Windows Firewall to confirm the rules are actually gone. Returns true if verified
    /// absent; false if a rule is still present or the COM call failed.
    /// </summary>
    public async Task<bool> ClearAndVerifyAsync(string imagePath, bool clearDown, bool clearUp)
    {
        // Always remove by rule name regardless of in-memory state — this also handles
        // stale rules left by a previous session.  App restart resets CurrentlyBlocking*
        // to false, but Windows Firewall rules survive reboots on disk.
        Task<bool>? downTask = clearDown
            ? RemoveBlockAsync(MakeRuleName(ThrottleRulePrefix,   imagePath)) : null;
        Task<bool>? upTask   = clearUp
            ? RemoveBlockAsync(MakeRuleName(UpThrottleRulePrefix, imagePath)) : null;

        lock (_lock)
        {
            if (_byImagePath.TryGetValue(imagePath, out var st))
            {
                if (clearDown) { st.DownloadLimitKbps = 0; st.CurrentlyBlockingDown = false; }
                if (clearUp)   { st.UploadLimitKbps   = 0; st.CurrentlyBlockingUp   = false; }
                if (st.DownloadLimitKbps <= 0 && st.UploadLimitKbps <= 0)
                    _byImagePath.Remove(imagePath);
            }
        }

        if (downTask != null) await downTask;
        if (upTask   != null) await upTask;

        return await VerifyRuleAbsentAsync(imagePath, clearDown, clearUp);
    }

    /// <summary>
    /// Returns true when the specified throttle rules are absent from Windows Firewall.
    /// </summary>
    public static Task<bool> VerifyRuleAbsentAsync(string exePath, bool checkDownload, bool checkUpload)
        => Task.Run(() =>
        {
            try
            {
                string downRule = MakeRuleName(ThrottleRulePrefix,   exePath);
                string upRule   = MakeRuleName(UpThrottleRulePrefix, exePath);
                dynamic rules = GetFwRules();
                foreach (dynamic rule in (System.Collections.IEnumerable)rules)
                {
                    var n = (string)rule.Name;
                    if (checkDownload && n.Equals(downRule, StringComparison.OrdinalIgnoreCase)) return false;
                    if (checkUpload   && n.Equals(upRule,   StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            }
            catch { return false; }
        });

    private static string MakeRuleName(string prefix, string exePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(exePath);
        var safe = new string(fileName
            .Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.').ToArray());
        if (string.IsNullOrEmpty(safe)) safe = "app";
        return prefix + safe;
    }

    private static dynamic GetFwRules()
    {
        var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException("HNetCfg.FwPolicy2 not registered.");
        dynamic policy = Activator.CreateInstance(t)!;
        return policy.Rules;
    }

    private static void TryRemoveFwRule(dynamic rules, string name)
    {
        try { rules.Remove(name); } catch { }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _innerTimer.Dispose();
    }
}
