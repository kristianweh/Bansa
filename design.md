# Bansa — Design Notes

**Bansa** — a non-intrusive, fully-reversible per-app bandwidth monitor and throttle for Windows.

## Design principles

1. **Use Windows' own machinery, never replace it.** Every "action" we take maps to a built-in Windows feature (Defender Firewall rule, QoS Policy, ETW session). The network side uses zero kernel drivers. The one exception in the app is hardware sensors: CPU/GPU temperatures can't be read from user mode, so LibreHardwareMonitor loads its temporary WinRing0 driver (`Bansa.sys`) while Bansa runs — unloaded and deleted on exit, never installed, never persistent. We never modify system files, never run anything as SYSTEM.
2. **Everything has an undo.** Each change we make is tagged with a `Bansa-` prefix so we can list and remove only our own changes. A single "Clean up & remove all Bansa changes" button restores the system to its pre-Bansa state.
3. **One executable, one folder.** No installer required. Uninstall = delete the folder + click cleanup.
4. **Admin only when running.** We don't install a service. Bansa asks for admin elevation when launched, does its work, and exits.

## What we touch on the system (and how we reverse it)

| Action | Windows feature used | Where it lives | How we reverse it |
|---|---|---|---|
| Block an app | Defender Firewall rule (`HNetCfg.FwPolicy2` COM) | Windows Firewall config | `Remove-NetFirewallRule -DisplayName "Bansa-*"` |
| Limit an app's **download** | Pulsed Defender Firewall rule (token-bucket, ~10 Hz) | Windows Firewall config | Remove `Bansa-Throttle-*` rules |
| Limit an app's **upload** | QoS Policy (smooth kernel-level shaping via ms_pacer) | `HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS\` | Remove `Bansa-Qos-<app>-limit` policy |
| Global upload cap | QoS Group Policy (soft) + pulsed firewall rules (hard) | `HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS\` + Firewall config | `Remove-NetQosPolicy -Name "Bansa-*"` + remove `Bansa-GlobalCap-*` rules |
| Read process bandwidth | ETW kernel session (read-only) | Memory only | Session ends when Bansa exits |
| Read active connections | `GetExtendedTcpTable` / `GetExtendedUdpTable` | Read-only API call | No state created |
| Register auto-start task | Windows Task Scheduler (`schtasks.exe`) | Task Scheduler, task name `Bansa_BandwidthMonitor_AutoStart` | Toggle off in Settings → General, or `schtasks /delete /tn "Bansa_BandwidthMonitor_AutoStart" /f` |
| Store history | SQLite database | `%LocalAppData%\Bansa\bansa.db` | Delete the folder |
| App settings | JSON file | `%LocalAppData%\Bansa\settings.json` | Delete the folder |

That's the complete list. Nothing else gets written to disk, the registry, or the network stack.

## Components

```
+--------------------------+
|        Bansa.exe          |   single WPF app, admin elevated
+--------------------------+
|  UI (WPF MVVM)           |
|  - MainWindow + panels   |   Dashboard · Network · Hardware · Tools · History · Settings
|  - FloatingGraphWindow   |
|  - TrayIconManager       |   tray icon + TrayPopupWindow
+--------------------------+
|  Services                |
|  - NetworkMonitor (ETW)  |
|  - ProcessEnumerator     |
|  - FirewallManager       |   <-> HNetCfg.FwPolicy2 (COM)
|  - DownloadThrottler     |   <-> pulsed firewall rules (up/down/global cap)
|  - QosManager            |   <-> QoS Group Policy registry
|  - HardwareMonitor       |   <-> LibreHardwareMonitor
|  - PingMonitor / SpeedTester
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

## Feature status (v1.0)

- ✅ Live process list with up/down rate and connection counts
- ✅ Double-click to see individual processes and connections
- ✅ Right-click → Set limits / Quick profile / Clear limits / Block / Unblock
- ✅ History tab with date-range totals per app + activity log
- ✅ Scenarios — a saved set of per-app up/down limits toggled on/off together
- ✅ Global Upload Cap — system-wide outbound cap (QoS soft cap + pulsed-firewall hard cap)
- ✅ Settings with one-click "Clean up & remove all Bansa changes"
- ✅ Tray icon with live rates, hover popup, ping indicator
- ✅ Hardware monitor panel (CPU / GPU / RAM temps, loads, clocks)
- ✅ Floating graph window — detachable overlay
- ✅ Auto-start with Windows (Task Scheduler, elevated)
- ✅ Settings export / import

## Out of scope

- Remote management
- Per-network-adapter rules
- Wi-Fi SSID-based rules
- Time-of-day rules
- Group/tag-based bulk operations
- Kernel-level smooth pacing (would require a callout driver — explicitly out of scope)
