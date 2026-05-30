# Bansa

A non-intrusive, fully-reversible per-app bandwidth monitor and throttle for Windows — built entirely on the OS's own machinery (no kernel drivers), so every change can be undone with one click.

> **Status:** v0.2 — personal use, no commercial intent.

---

## Installation

1. Download `Bansa.zip` from the [Releases](../../releases/latest) page.
2. Extract it — a `Bansa\` folder is created containing `Bansa.exe`.
3. Run `Bansa.exe` as Administrator (UAC prompt will appear — required for ETW and firewall access).

That's it. No installer, no setup wizard.

**Requirements:** [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) must be installed on the target machine.

### Folder structure after first run

```
Bansa\
├── Bansa.exe          ← the app
└── Data\
    ├── settings.json  ← all preferences
    ├── bansa.db       ← bandwidth history (SQLite)
    └── Tools\         ← portable apps downloaded from the Tools tab
        ├── OpenRGB\
        ├── HWiNFO\
        └── ShareX\
```

Everything lives inside the `Bansa\` folder. Move it anywhere, copy it to another PC — it just works.

---

## Updating

**Only replace `Bansa.exe`.** Everything else (`Data\`, downloaded tools, settings) stays exactly where it is.

```
Bansa\
├── Bansa.exe     ← replace this file with the new version
└── Data\         ← leave this folder alone — settings and tools are here
```

No migration step needed. Settings are backward-compatible across versions.

---

## What it does

**Live monitoring**
- Apps list grouped by name (all `chrome.exe` PIDs collapse into one row). Double-click any app to see its individual processes.
- Per-app upload, download, total bytes, active connection count.
- Tray icon with live ↓/↑ rates and a ping-to-Google indicator. Hover for top traffic apps.
- Continuous ping to 8.8.8.8 displayed in the header.

**Filtering**
- Filter by app name.
- Threshold slider to hide apps below a rate (e.g., 1 KB/s) — gets rid of noisy idle background processes.

**Controls (per app, all reversible)**
- **Block** — adds a Defender Firewall rule named `Bansa-Block-<app>`.
- **Upload limit** — exact rate cap via Windows QoS Policy (`Bansa-Qos-<app>-limit`).
- **Download limit** — best-effort throttling via inbound monitor + temporary firewall block (see below).
- **Custom kbps input** — Set Limits dialog accepts any number plus presets.
- **High-priority mark** — DSCP 46 (Expedited Forwarding) for games/voice.

**Tools tab**
- Download and launch portable utilities (OpenRGB, HWiNFO, ShareX) directly from the app.
- Downloaded tools live in `Data\Tools\` and persist across updates.

**Verifying limits work**
- Built-in self-test downloads 25 MB from Cloudflare and shows the achieved rate. Set a limit on `Bansa.exe` and run the test to verify exactly.
- "Open fast.com" button for testing under your browser's process.

**Themes & UX**
- Dark and Light themes, full-window switch via the header toggle, persisted to `Data\settings.json`.
- Floating graph window (detachable, always-on-top, right-click → Open Bansa).
- Card-based modern layout, status bar, tabbed navigation.

---

## How download limits work (and what they can't do)

Windows QoS Policy only throttles **outbound** traffic — there's no clean user-mode way to rate-limit inbound traffic per process.

Bansa's download limit works by:
1. Watching the app's actual download rate via ETW (the same monitor that powers the UI).
2. When the rate exceeds 110% of the cap for several samples, Bansa temporarily adds an inbound firewall block rule for that app.
3. When the rate drops below 70% of the cap, the block is removed.

This results in a **stutter pattern**: short downloads alternating with short blocks. The **average rate** matches your cap, but the connection isn't smoothly paced like with a kernel-level driver. For most use cases (keeping a backup app from saturating your link, capping a video stream's bitrate) it works well enough.

For perfect bidirectional throttling you'd need a kernel callout driver — explicitly out of scope, since the no-driver design is what makes Bansa fully reversible.

---

## Design principles

1. No kernel drivers. No installer. No background service.
2. Single executable. Asks for admin elevation when launched.
3. Every change is tagged `Bansa-…` so cleanup finds and removes only Bansa's own changes.
4. Built entirely on Defender Firewall, QoS Policy, ETW, IP Helper API — all native Windows.
5. All data lives in `Data\` next to the exe. Move the folder, nothing breaks.

See `design.md` for architecture and `REVERSIBILITY.md` for the full audit of system mutations.

---

## Full uninstall (no trace)

1. Open Bansa → **Cleanup** button (Settings tab) — removes all firewall rules and QoS policies.
2. Close Bansa.
3. Delete the `Bansa\` folder.

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

### Publish (framework-dependent single exe, ~14 MB)

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
        │   ├── Bansa.ico
        │   ├── bansa-light.png
        │   └── bansa-dark.png
        ├── Themes\
        │   ├── Dark.xaml
        │   ├── Light.xaml
        │   └── Controls.xaml
        ├── Views\
        ├── Models\
        ├── Services\
        └── ViewModels\
```

---

## Roadmap

- v0.3: per-app traffic history charts.
- v0.4: configurable Gaming Mode preset.
- v0.5: optional auto-start with Windows.
