# Bansa

A non-intrusive, fully-reversible per-app bandwidth monitor and throttle for Windows — built entirely on the OS's own machinery (no kernel drivers), so every change can be undone with one click.

> **Status:** v0.8 — personal use, no commercial intent.

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
- Tray icon with live ↓/↑ rates and a ping indicator. Left-click to toggle the popup; double-click to open the main window. Popup position is remembered when dragged.
- Continuous ping to a configurable target displayed in the sidebar.
- Hardware panel — live CPU / GPU / RAM usage, temperatures, clocks, VRAM.
- Floating graph window — detachable, always-on-top overlay with network rates and hardware stats. Drag via the rates bar; right-click for options. Position is always remembered between sessions and safely clamped to the visible screen on different machines.

**Filtering**
- Filter by app name.
- Threshold to hide apps below a rate (1 KB/s, 10 KB/s, 50 KB/s, or custom).
- Hide local-only apps (loopback/LAN traffic).

**Network tab chart**
- 30-second live sparkline with smooth Catmull-Rom curves.
- Click to pause/resume; click again to snap back to live.
- Drag right to scroll back through up to **1 hour of history**; label shows how far back you're looking.
- Chart height is remembered between launches (drag the divider between chart and app list).

**Controls (per app, all reversible)**
- **Block** — adds a Defender Firewall rule named `Bansa-Block-<app>`.
- **Upload limit** — smooth kernel-level rate shaping via Windows QoS Policy (`Bansa-Qos-<app>-limit`). No connection drops; takes effect on the app's next connection (the badge goes amber while an existing connection is still over the cap).
- **Download limit** — best-effort throttling via inbound monitor + pulsed firewall block (`Bansa-Throttle-<app>`); Windows has no smooth user-mode way to rate-limit inbound traffic.
- **Limit profiles** — named presets (e.g., "Gaming", "Backup") for quick reuse. Right-click any app → **Quick profile** to apply a profile in one step. Edit profiles in Settings → Network.

**Gaming Mode**
- A saved set of **per-app** upload/download limits that toggle on and off together with one click (sidebar card or Dashboard card).
- Intended to throttle background apps (Spotify, Discord, cloud sync) so they don't compete with your game. Apps not listed are unaffected.
- When you turn it off, each app's previous (non-gaming) limit is restored. Configure the per-app entries in **Settings → Gaming Mode**.

**Global Upload Cap**
- A separate, system-wide outbound cap — keeps bufferbloat from degrading latency while uploading.
- **Two enforcement layers:** QoS Group Policy (zero-overhead, handles new connections) + pulsed firewall rules (catches existing connections, UDP traffic, and anything QoS misses). Set it from the Network tab.
- An **enable/disable switch** pauses the cap without discarding the configured value, so you can drop it for a big upload and switch it back on afterward.

**History tab**
- Total bytes per app over any date range (Today / Last 7 d / Last 30 d / custom).
- Activity log — timestamped record of every block, limit, and priority change.
- Export to CSV.

**Tools tab**
- Browse and launch portable utilities: OpenRGB, HWiNFO, ShareX, Chris Titus WinUtil, FanControl, DDU — all with real brand logos.
- Tools stored as portables in `Data\Tools\`; click the directory link to open the folder directly.
- Website button opens the download page when a tool isn't installed yet.

**Settings**
Four tabs — *General · Network · Gaming Mode · Appearance*:

- *General* — units, global hotkey, startup & window behavior (minimize on close, start minimized to tray, show/hide tray icon), settings backup (export/import), system cleanup.
- *Network* — limit profiles (add / edit / delete / quick-apply), global upload cap, ISP connection speed, ping monitor targets.
- *Gaming Mode* — per-app upload/download limits applied together when Gaming Mode is toggled on.
- *Appearance* — dark/light theme, Windows accent color; separate color pickers for download/upload graph, CPU, GPU, and RAM; gradient endpoint pickers for temperature and ping indicators.

---

## How the global upload cap works

The cap runs two enforcement layers simultaneously:

1. **QoS Group Policy (soft cap)** — written to `HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS`. Applied instantly to new socket connections with zero CPU overhead once set.

2. **Firewall pulse enforcement (hard cap)** — every 100 ms Bansa measures actual bytes sent across all processes, runs a token-bucket calculation, and if the total is over budget it adds outbound firewall block rules for each app that's actively uploading. Rules are removed as soon as the budget recovers. This layer catches what QoS cannot: existing connections that were open before the cap was set, UDP traffic (games, video calls), and any timing gaps during policy propagation.

---

## How download limits work (and what they can't do)

Windows QoS Policy only throttles **outbound** traffic — there's no clean user-mode way to rate-limit inbound traffic per process.

Bansa's download limit works by:
1. Watching the app's actual download rate via ETW (the same monitor that powers the UI).
2. When the rate exceeds the cap, Bansa temporarily adds an inbound firewall block for that app.
3. When the rate drops back, the block is removed.

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
        ├── Services\          ← NetworkMonitor, DownloadThrottler, QosManager, …
        └── ViewModels\
```
