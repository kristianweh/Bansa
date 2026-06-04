# Reversibility audit

Every system change Bansa makes, where it lives, and how it gets undone. If this audit is complete, Bansa is fully reversible by definition.

| # | Mutation | Created by | Where it lives | Reversed by |
|---|---|---|---|---|
| 1 | ETW kernel session `Bansa-KernelNetSession` | `NetworkMonitor.Start()` | Kernel memory only | `NetworkMonitor.Stop()` on app exit; `StopOnDispose=true` as safety; orphan-cleanup at next startup; manual `logman stop Bansa-KernelNetSession -ets` |
| 2 | Defender Firewall rule `Bansa-Block-<name>-In` | `FirewallManager.BlockAppAsync` | Windows Defender Firewall config | `FirewallManager.UnblockAppAsync` (per app) or `FirewallManager.RemoveAllBansaRulesAsync` (all). Also visible in Defender Firewall UI for manual removal. |
| 3 | Defender Firewall rule `Bansa-Block-<name>-Out` | Same | Same | Same |
| 4 | Download-throttle firewall rule `Bansa-Throttle-<name>` (inbound, pulsed on/off) | `DownloadThrottler` 100 ms tick | Windows Defender Firewall config | `DownloadThrottler.ClearAndVerifyAsync` (per app) or `DownloadThrottler.RemoveAllAsync` (all). Removed on app exit. |
| 5 | Upload-throttle firewall rule `Bansa-UpThrottle-<name>` (outbound, pulsed) | Same | Same | Same |
| 6 | Global-cap firewall rule `Bansa-GlobalCap-<name>` (outbound, pulsed) | Same — hard layer of the global upload cap | Same | Set cap to 0, or `DownloadThrottler.RemoveAllAsync`. Removed on app exit. |
| 7 | QoS Policy `Bansa-Qos-Global-Upload` (soft layer of the global upload cap) | `QosManager.SetGlobalUploadCapAsync` | `HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS\` (Windows-managed) | Set cap to 0, or `QosManager.RemoveAllBansaPoliciesAsync` (all) |
| 8 | Scheduled task `Bansa_BandwidthMonitor_AutoStart` | `AutoStartManager.SetAsync(true)` | Windows Task Scheduler | `AutoStartManager.SetAsync(false)` (toggle in Settings → General → Behavior) or `schtasks /delete /tn "Bansa_BandwidthMonitor_AutoStart" /f` |
| 9 | SQLite history db | `HistoryStore` ctor | `%LocalAppData%\Bansa\bansa.db` | Delete the folder |
| 10 | Crash log | `App.OnAppDomainUnhandledException` | `%LocalAppData%\Bansa\crash.log` | Delete the folder |

That's the complete list of writable state Bansa touches. Nothing is written outside of these paths.
All firewall rules (block + throttle + global-cap) and the QoS policy are torn down together by `CleanupManager.RunAsync`, the in-app Cleanup button, and `Uninstall-Bansa.ps1`.

## Three independent paths to a clean system

1. **In-app Cleanup button** — Settings → General → System changes — clears all firewall rules + QoS policies, preserves history.
2. **`Uninstall-Bansa.ps1`** — standalone PS script, works even if Bansa won't run. Optionally also deletes the data folder.
3. **Manual** — anyone can `Get-NetFirewallRule -DisplayName 'Bansa-*' | Remove-NetFirewallRule` and `Get-NetQosPolicy | Where-Object Name -like 'Bansa-*' | Remove-NetQosPolicy`.

## Things Bansa does NOT do

- ❌ Install kernel drivers
- ❌ Install Windows services
- ❌ Modify system files (Hosts, system DLLs, etc.)
- ❌ Modify the network stack
- ❌ Touch any TLS / certificate stores
- ❌ Write outside `%LocalAppData%\Bansa\` (other than via Windows-supported Firewall/QoS APIs and the optional Task Scheduler entry)
- ❌ Send any telemetry anywhere
- ❌ Talk to the internet at all
