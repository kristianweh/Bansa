# Bansa — Design Notes

**Bansa** — a non-intrusive, fully-reversible per-app bandwidth monitor and throttle for Windows.

## Design principles

1. **Use Windows' own machinery, never replace it.** Every "action" we take maps to a built-in Windows feature (Defender Firewall rule, QoS Policy, ETW session). We never install kernel drivers, never modify system files, never run anything as SYSTEM.
2. **Everything has an undo.** Each change we make is tagged with a `Bansa-` prefix so we can list and remove only our own changes. A single "Clean up & remove all Bansa changes" button restores the system to its pre-Bansa state.
3. **One executable, one folder.** No installer required. Uninstall = delete the folder + click cleanup.
4. **Admin only when running.** We don't install a service. Bansa asks for admin elevation when launched, does its work, and exits.

## What we touch on the system (and how we reverse it)

| Action | Windows feature used | Where it lives | How we reverse it |
|---|---|---|---|
| Block an app | Defender Firewall rule (`New-NetFirewallRule`) | Windows Firewall config | `Remove-NetFirewallRule -DisplayName "Bansa-*"` |
| Limit an app's bandwidth | QoS Policy (`New-NetQosPolicy`) | `HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS\` | `Remove-NetQosPolicy -Name "Bansa-*"` |
| Prioritize game traffic | QoS Policy with DSCP value | Same as above | Same as above |
| Read process bandwidth | ETW kernel session (read-only) | Memory only | Session ends when Bansa exits |
| Read active connections | `GetExtendedTcpTable` / `GetExtendedUdpTable` | Read-only API call | No state created |
| Store history | SQLite database | `%LocalAppData%\Bansa\Bansa.db` | Delete the folder |
| App settings | JSON file | `%LocalAppData%\Bansa\settings.json` | Delete the folder |

That's the complete list. Nothing else gets written to disk, the registry, or the network stack.

## Components

```
+--------------------------+
|        Bansa.exe          |   single WPF app, admin elevated
+--------------------------+
|  UI (WPF MVVM)           |
|  - ProcessListView       |
|  - HistoryView           |
|  - SettingsView          |
+--------------------------+
|  Services                |
|  - NetworkMonitor (ETW)  |
|  - ProcessEnumerator     |
|  - FirewallManager       |   <-> netsh / New-NetFirewallRule
|  - QosManager            |   <-> New-NetQosPolicy
|  - HistoryStore (SQLite) |
|  - CleanupManager        |
+--------------------------+
```

No background service. No driver. No IPC layer needed (everything runs in one process).

## Why these choices

- **WPF over WinUI 3** — More stable, mature charting libraries, no packaging gotchas. We're shipping a side-loaded EXE.
- **Defender Firewall over WFP user-mode** — Same underlying mechanism (WFP) but Firewall rules are visible in Windows' own UI, which means a curious user can inspect and remove them by hand if they ever want to.
- **QoS Policy over a custom callout driver** — QoS Policy is Windows' supported way to rate-limit and DSCP-mark traffic per application. It works for both throttling and game prioritization. Reversible with a single PowerShell call.
- **ETW for per-process bytes** — The only reliable user-mode way to get per-process send/recv counts on Windows. Read-only, ephemeral.
- **SQLite over Files** — Mirrors NetBalancer's approach. Good for time-series queries.

## What this design gives up (vs. real NetBalancer)

- **Smooth, kernel-level throttling.** QoS Policy works well but isn't as precise as a callout driver. For personal use, more than good enough.
- **Delayed priority mode.** No equivalent without a driver. We can fake it via rate limit + low DSCP value.
- **Deep packet inspection.** Not feasible from user-mode. Out of scope.

## MVP feature checklist

- [ ] Live process list with up/down rate (KB/s) and connection counts
- [ ] Expand a process to see its active connections (remote IP/port/protocol)
- [ ] Right-click → Block app (firewall rule)
- [ ] Right-click → Limit to N KB/s (QoS policy)
- [ ] Right-click → Remove limits / unblock
- [ ] History tab with date-range chart per app
- [ ] Game Mode preset: detect a configured game, demote everything else
- [ ] Settings tab with one-click "Clean up & remove all Bansa changes"
- [ ] Tray icon with current totals

## Out of scope for v1

- Remote management
- Per-network-adapter rules
- Wi-Fi SSID-based rules
- Time-of-day rules
- Group/tag-based bulk operations
- Auto-start with Windows (can be added trivially via Startup folder later)
