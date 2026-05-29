# Reversibility audit

Every system change Flow makes, where it lives, and how it gets undone. If this audit is complete, Flow is fully reversible by definition.

| # | Mutation | Created by | Where it lives | Reversed by |
|---|---|---|---|---|
| 1 | ETW kernel session `Flow-KernelNetSession` | `NetworkMonitor.Start()` | Kernel memory only | `NetworkMonitor.Stop()` on app exit; `StopOnDispose=true` as safety; orphan-cleanup at next startup; manual `logman stop Flow-KernelNetSession -ets` |
| 2 | Defender Firewall rule `Flow-Block-<name>-In` | `FirewallManager.BlockAppAsync` | Windows Defender Firewall config | `FirewallManager.UnblockAppAsync` (per app) or `FirewallManager.RemoveAllFlowRulesAsync` (all). Also visible in Defender Firewall UI for manual removal. |
| 3 | Defender Firewall rule `Flow-Block-<name>-Out` | Same | Same | Same |
| 4 | QoS Policy `Flow-Qos-<name>-limit` | `QosManager.SetUploadLimitAsync` | `HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS\` (Windows-managed) | Set limit to 0 (per app) or `QosManager.RemoveAllFlowPoliciesAsync` (all) |
| 5 | QoS Policy `Flow-Qos-<name>-dscp` | `QosManager.SetDscpAsync` | Same | Same |
| 6 | SQLite history db | `HistoryStore` ctor | `%LocalAppData%\Flow\flow.db` | Delete the folder |
| 7 | Crash log | `App.OnAppDomainUnhandledException` | `%LocalAppData%\Flow\crash.log` | Delete the folder |

That's the complete list of writable state Flow touches. Nothing is written outside of these paths.

## Three independent paths to a clean system

1. **In-app Cleanup button** — clears all firewall rules + QoS policies, preserves history.
2. **`Uninstall-Flow.ps1`** — standalone PS script, works even if Flow won't run. Optionally also deletes the data folder.
3. **Manual** — anyone can `Get-NetFirewallRule -DisplayName 'Flow-*' | Remove-NetFirewallRule` and `Get-NetQosPolicy | Where-Object Name -like 'Flow-*' | Remove-NetQosPolicy`.

## Things Flow does NOT do

- ❌ Install kernel drivers
- ❌ Install Windows services
- ❌ Register scheduled tasks
- ❌ Add startup entries
- ❌ Modify system files (Hosts, system DLLs, etc.)
- ❌ Modify the network stack
- ❌ Touch any TLS / certificate stores
- ❌ Write outside `%LocalAppData%\Flow\` (other than via Windows-supported Firewall/QoS APIs)
- ❌ Send any telemetry anywhere
- ❌ Talk to the internet at all
