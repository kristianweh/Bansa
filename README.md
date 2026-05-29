# Flow

A non-intrusive, fully-reversible per-app bandwidth monitor and throttle for Windows — built entirely on the OS's own machinery (no kernel drivers), so every change can be undone with one click.

> **Status:** v0.2 — second iteration. Personal use only, no commercial intent.

## What it does

**Live monitoring**
- Apps list grouped by name (all `chrome.exe` PIDs collapse into one row). Double-click any app to see its individual processes.
- Per-app upload, download, total bytes, active connection count.
- Tray icon with live ↓/↑ rates and a ping-to-Google indicator. Hover for top traffic apps.
- Continuous ping to 8.8.8.8 displayed in the header.

**Filtering**
- Filter by app name.
- Threshold slider to hide apps below a rate (e.g., 1 KB/s) — gets rid of the noisy idle background processes.

**Controls (per app, all reversible)**
- **Block** — adds a Defender Firewall rule named `Flow-Block-<app>`.
- **Upload limit** — exact rate cap via Windows QoS Policy (`Flow-Qos-<app>-limit`).
- **Download limit** — best-effort throttling via inbound monitor + temporary firewall block (see "How download limits work" below).
- **Custom kbps input** — Set Limits dialog accepts any number plus presets.
- **High-priority mark** — DSCP 46 (Expedited Forwarding) for games/voice.

**Verifying limits work**
- Built-in self-test downloads 25 MB from Cloudflare and shows the achieved rate. Set a limit on `Flow.exe` and run the test to verify exactly.
- "Open fast.com" button for testing under your browser's process.

**Themes & UX**
- Dark and Light themes, full-window switch via the header toggle, persisted to `settings.json`.
- Properly-styled context menu (no more white-on-white).
- Card-based modern layout, status bar, tabbed navigation.

**Storage**
- SQLite history at `%LocalAppData%\Flow\flow.db` with hourly rollup so the file stays small.
- All settings at `%LocalAppData%\Flow\settings.json`.

**One-click cleanup** removes every firewall rule, QoS policy, and throttle block Flow has added.

## How download limits work (and what they can't do)

Windows QoS Policy only throttles **outbound** traffic — there's no clean user-mode way to rate-limit inbound traffic per process.

Flow's download limit works by:
1. Watching the app's actual download rate via ETW (the same monitor that powers the UI).
2. When the rate exceeds 110% of the cap for several samples, Flow temporarily adds an inbound firewall block rule for that app.
3. When the rate drops below 70% of the cap, the block is removed.

This results in a **stutter pattern**: short downloads alternating with short blocks. The **average rate** matches your cap, but the connection isn't smoothly paced like with a kernel-level driver. For most use cases (keeping a backup app from saturating your link, capping a video stream's bitrate) it works well enough.

For perfect bidirectional throttling you'd need a kernel callout driver — explicitly out of scope, since the no-driver design is what makes Flow fully reversible.

## Design principles

1. No kernel drivers. No installer. No background service.
2. Single executable. Asks for admin elevation when launched.
3. Every change is tagged `Flow-…` so cleanup finds and removes only Flow's own changes.
4. Built entirely on Defender Firewall, QoS Policy, ETW, IP Helper API — all native Windows.
5. Data lives in one folder (`%LocalAppData%\Flow\`).

See `design.md` for architecture and `REVERSIBILITY.md` for the full audit of system mutations.

## Build & run

### One-time setup

1. Install the .NET 8 SDK from <https://dotnet.microsoft.com/download/dotnet/8.0>.
2. Done.

### Build & run

Open PowerShell **as Administrator**, then:

```powershell
cd "path\to\Flow, bandwidth monitor\src"
dotnet run --project Flow -c Release
```

Windows will prompt for UAC because Flow needs admin to start ETW sessions and manage firewall / QoS rules. Click **Yes**.

## Using Flow

**Apps list (Processes tab)**
- Sort by clicking any column header.
- Filter by typing in the search box.
- Drag the threshold slider to hide low-traffic apps.
- Double-click a row to see individual PIDs for that app.
- Right-click for actions.

**Right-click menu**
- **Set limits…** — opens the dialog with upload + download fields, presets, and custom input.
- **Block / Unblock this app** — firewall rule.
- **Mark as high-priority (game / voice)** — DSCP marking. Effective only if your router honors DSCP; harmless otherwise.
- **Show individual processes** — same as double-click.

**Speed Test tab**
- Self-test for verifying limits on Flow.exe itself.
- External launcher for fast.com.

**Settings tab**
- Theme toggle.
- Threshold slider with higher max (up to 500 KB/s).
- Cleanup button.

**Tray icon**
- Live ↓/↑ rate rendered in the icon image.
- Hover tooltip shows totals, ping ms, and top 5 traffic apps.
- Left-click brings the window back; right-click for Show / Quit.
- Window minimizes to tray.

## Full uninstall (no trace)

1. Open Flow → **Cleanup** button (header or Settings).
2. Close Flow.
3. Delete `%LocalAppData%\Flow\`.
4. Delete the project folder.

Or run `Uninstall-Flow.ps1` from an elevated PowerShell prompt for the same effect.

Verify with `Inspect-Flow.ps1` at any time to see what Flow has on the system.

## Project layout

```
Flow, bandwidth monitor/
├── README.md
├── design.md
├── REVERSIBILITY.md
├── Uninstall-Flow.ps1
├── Inspect-Flow.ps1
└── src/
    ├── Flow.sln
    └── Flow/
        ├── Flow.csproj
        ├── app.manifest         ← requires admin
        ├── App.xaml(.cs)
        ├── MainWindow.xaml(.cs)
        ├── Themes/
        │   ├── Dark.xaml
        │   ├── Light.xaml
        │   └── Controls.xaml     ← shared styles (incl. context menu fix)
        ├── Views/
        │   ├── SetLimitWindow.xaml(.cs)   ← upload/download with custom input
        │   └── SpeedTestView.xaml(.cs)
        ├── Models/
        │   └── ProcessNetInfo.cs
        ├── Services/
        │   ├── NetworkMonitor.cs        ← ETW kernel session
        │   ├── ProcessEnumerator.cs     ← IP Helper API
        │   ├── FirewallManager.cs       ← Defender Firewall rules
        │   ├── QosManager.cs            ← QoS upload limits + DSCP
        │   ├── DownloadThrottler.cs     ← user-mode download limiter
        │   ├── HistoryStore.cs          ← SQLite
        │   ├── CleanupManager.cs        ← undoes everything
        │   ├── PowerShellRunner.cs
        │   ├── SettingsManager.cs       ← settings.json
        │   ├── ThemeManager.cs          ← dark/light swap
        │   ├── PingMonitor.cs           ← background ping
        │   ├── SpeedTester.cs           ← Cloudflare download test
        │   └── TrayIconManager.cs       ← NotifyIcon with live image + tooltip
        └── ViewModels/
            ├── MainViewModel.cs
            ├── AppRowViewModel.cs       ← grouped by app name
            └── ProcessRowViewModel.cs   ← individual PIDs
```

## Roadmap notes

- v0.3: history chart view (LiveCharts2 or similar), per-app traffic graphs.
- v0.4: configurable "Game Mode" preset — pick a game, one button demotes everything else.
- v0.5: optional auto-start with Windows (via Startup folder shortcut, still reversible).
