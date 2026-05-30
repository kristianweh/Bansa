# Reversibility audit

Every system change Bansa makes, where it lives, and how it gets undone. If this audit is complete, Bansa is fully reversible by definition.

| # | Mutation | Created by | Where it lives | Reversed by |
|---|---|---|---|---|
| 1 | ETW kernel session `Bansa-KernelNetSession` | `NetworkMonitor.Start()` | Kernel memory only | `NetworkMonitor.Stop()` on app exit; `StopOnDispose=true` as safety; orphan-cleanup at next startup; manual `logman stop Bansa-KernelNetSession -ets` |
| 2 | Defender Firewall rule `Bansa-Block-<name>-In` | `FirewallManager.BlockAppAsync` | Windows Defender Firewall config | `FirewallManager.UnblockAppAsync` (per app) or `FirewallManager.RemoveAllFlowRulesAsync` (all). Also visible in Defender Firewall UI for manual removal. |
| 3 | Defender Firewall rule `Bansa-Block-<name>-Out` | Same | Same | Same |
| 4 | QoS Policy `Bansa-Qos-<name>-limit` | `QosManager.SetUploadLimitAsync` | `HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS\` (Windows-managed) | Set limit to 0 (per app) or `QosManager.RemoveAllFlowPoliciesAsync` (all) |
| 5 | QoS Policy `Bansa-Qos-<name>-dscp` | `QosManager.SetDscpAsync` | Same | Same |
| 6 | SQLite history db | `HistoryStore` ctor | `%LocalAppData%\Bansa\Bansa.db` | Delete the folder |
| 7 | Crash log | `App.OnAppDomainUnhandledException` | `%LocalAppData%\Bansa\crash.log` | Delete the folder |

That's the complete list of writable state Bansa touches. Nothing is written outside of these paths.

## Three independent paths to a clean system

1. **In-app Cleanup button** — clears all firewall rules + QoS policies, preserves history.
2. **`Uninstall-Bansa.ps1`** — standalone PS script, works even if Bansa won't run. Optionally also deletes the data folder.
3. **Manual** — anyone can `Get-NetFirewallRule -DisplayName 'Bansa-*' | Remove-NetFirewallRule` and `Get-NetQosPolicy | Where-Object Name -like 'Bansa-*' | Remove-NetQosPolicy`.

## Things Bansa does NOT do

- ❌ Install kernel drivers
- ❌ Install Windows services
- ❌ Register scheduled tasks
- ❌ Add startup entries
- ❌ Modify system files (Hosts, system DLLs, etc.)
- ❌ Modify the network stack
- ❌ Touch any TLS / certificate stores
- ❌ Write outside `%LocalAppData%\Bansa\` (other than via Windows-supported Firewall/QoS APIs)
- ❌ Send any telemetry anywhere
- ❌ Talk to the internet at all
