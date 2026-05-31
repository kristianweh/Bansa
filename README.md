# Bansa

A non-intrusive, fully-reversible per-app bandwidth monitor and throttle for Windows — built entirely on the OS's own machinery (no kernel drivers), so every change can be undone with one click.

> **Status:** v0.5 — personal use, no commercial intent.

---

## Installation

1. Download `Bansa.zip` from the [Releases](../../releases/latest) page.
2. Extract it — a `Bansa\` folder is created containing `Bansa.exe`.
3. Run `Bansa.exe` as Administrator (UAC prompt will appear — required for ETW and firewall access).

That's it. No installer, no setup wizard.

**Requirements:** [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) must be installed on the target machine.

### Where data lives

```
Bansa\
├── Bansa.exe          ← the app
└── Data\
    └── Tools\         ← portable tools you've placed here (opened from the Tools tab)

%LocalAppData%\Bansa\
├── settings.json      ← all preferences (export/import via Settings → General)
├── bansa.db           ← bandwidth history (SQLite)
└── crash.log          ← written only on unhandled exceptions
```

Settings and history live in `%LocalAppData%\Bansa\` so they survive updates and are per-user.
Tool executables live next to `Bansa.exe` in `Data\Tools\` so they stay portable with the app folder.

---

## Updating

Replace `Bansa.exe` with the new version. Settings, history, and any tools in `Data\Tools\` are untouched.

---

## What it does

**Live monitoring**
- App list grouped by name (all `chrome.exe` PIDs collapse into one row). Double-click any app to see its individual processes and connections.
- Per-app download rate, upload rate, total bytes, active connection count.
- Tray icon with live ↓/↑ rates and a ping indicator. Hover for top traffic apps.
- Continuous ping to a configurable target displayed in the sidebar.
- Hardware panel — live CPU / GPU / RAM usage, temperatures, clocks, VRAM.
- Floating graph window — detachable, always-on-top overlay with network rates and hardware stats. Drag via the rates bar; right-click for options.

**Filtering**
- Filter by app name.
- Threshold to hide apps below a rate (e.g., 10 KB/s).
- Hide local-only apps (loopback/LAN traffic).

**Controls (per app, all reversible)**
- **Block** — adds a Defender Firewall rule named `Bansa-Block-<app>`.
- **Upload limit** — exact rate cap via Windows QoS Policy (`Bansa-Qos-<app>-limit`).
- **Download limit** — best-effort throttling via inbound monitor + temporary firewall block.
- **High-priority mark** — DSCP 46 (Expedited Forwarding) for games/voice.
- **Limit profiles** — named presets (e.g., "Gaming", "Backup") for quick reuse.

**Gaming Mode**
- Global upload cap via QoS applied system-wide — keeps bufferbloat from degrading latency while uploading. Toggle from the Network tab or the sidebar button.

**History tab**
- Total bytes per app over any date range (Today / Last 7 d / Last 30 d / custom).
- Activity log — timestamped record of every block, limit, and priority change.
- Export to CSV.

**Tools tab**
- Browse and launch portable utilities (HWiNFO, OpenRGB, ShareX, etc.) stored in `Data\Tools\`.
- Website button opens the tool's homepage for download.

**Settings**
- *General* — units, global hotkey, startup & window behavior, settings backup (export/import), system cleanup.
- *Network* — limit profiles, global upload cap, ISP connection speed, ping monitor targets, limit verification / speed test.
- *Appearance* — dark/light theme, Windows accent color, chart and tray icon colors.

---

## How download limits work (and what they can't do)

Windows QoS Policy only throttles **outbound** traffic — there's no clean user-mode way to rate-limit inbound traffic per process.

Bansa's download limit works by:
1. Watching the app's actual download rate via ETW (the same monitor that powers the UI).
2. When the rate exceeds 110% of the cap for several samples, Bansa temporarily adds an inbound firewall block for that app.
3. When the rate drops below 70% of the cap, the block is removed.

This results in a **stutter pattern**: short downloads alternating with short blocks. The **average rate** matches your cap, but the connection isn't smoothly paced like with a kernel-level driver. For most use cases (keeping a backup app from saturating your link, capping a video stream's bitrate) it works well enough.

---

## Design principles

1. No kernel drivers. No installer. No background service.
2. Single executable. Asks for admin elevation when launched.
3. Every change is tagged `Bansa-…` so cleanup finds and removes only Bansa's own changes.
4. Built entirely on Defender Firewall, QoS Policy, ETW, IP Helper API — all native Windows.

See `design.md` for architecture and `REVERSIBILITY.md` for the full audit of system mutations.

---

## Full uninstall (no trace)

1. Open Bansa → **Settings → General → System changes → Clean up**.
2. Close Bansa.
3. Delete the `Bansa\` folder and `%LocalAppData%\Bansa\`.

Or run `Uninstall-Bansa.ps1` from an elevated PowerShell prompt for the same effect without launching the app.

Verify with `Inspect-Bansa.ps1` at any time to see what Bansa currently has on the system.

---

## Build from source

### Prerequisites

1. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
2. Run as Administrator (required for ETW + firewall access during development).

### Run

```powershell
cd path\to\repo\src
dotnet run --project Bansa -c Release
```

### Publish (framework-dependent single exe)

```powershell
cd path\to\repo
.\pack-release.ps1
```

Output: `release\Bansa.zip` — extract to get `Bansa\Bansa.exe`.

---

## Project layout

```
Bansa\                         ← repo root
├── README.md
├── design.md
├── REVERSIBILITY.md
├── pack-release.ps1           ← builds Bansa.zip for distribution
├── Uninstall-Bansa.ps1
├── Inspect-Bansa.ps1
└── src\
    ├── Bansa.sln
    └── Bansa\
        ├── Bansa.csproj
        ├── app.manifest
        ├── App.xaml(.cs)
        ├── MainWindow.xaml(.cs)
        ├── Resources\
        ├── Themes\            ← Dark.xaml, Light.xaml, Controls.xaml
        ├── Views\             ← FloatingGraphWindow, HistoryView, SpeedTestView, …
        ├── Models\
        ├── Services\          ← NetworkMonitor, FirewallManager, QosManager, …
        └── ViewModels\
```
