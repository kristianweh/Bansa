#requires -RunAsAdministrator
<#
    Uninstall-Bansa.ps1
    -------------------
    Standalone cleanup script. Removes every Windows change Bansa (or its
    predecessor "Flow") has made, even if the app itself is broken / missing.

    Run from an elevated PowerShell prompt:
        powershell -ExecutionPolicy Bypass -File .\Uninstall-Bansa.ps1
#>

$ErrorActionPreference = "Continue"

Write-Host ""
Write-Host "Bansa — Standalone Cleanup" -ForegroundColor Cyan
Write-Host "---------------------------"
Write-Host ""

# ── 1. Firewall rules ─────────────────────────────────────────────────────────
$comRules = (New-Object -ComObject HNetCfg.FwPolicy2).Rules
$toRemove = @($comRules | Where-Object {
    $_.Name -like "*Bansa*" -or $_.Name -like "*Flow*"
})
if ($toRemove.Count -gt 0) {
    Write-Host "Removing $($toRemove.Count) firewall rule(s):" -ForegroundColor Yellow
    foreach ($r in $toRemove) {
        Write-Host "  - $($r.Name)"
        try { $comRules.Remove($r.Name) } catch { Write-Host "    (already gone)" }
    }
    Write-Host "  Done." -ForegroundColor Green
} else {
    Write-Host "No Bansa / Flow firewall rules." -ForegroundColor Green
}

# ── 2. QoS registry policies ──────────────────────────────────────────────────
Write-Host ""
$qosBase = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS"
$qosPolicies = Get-ChildItem $qosBase -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -like "*Bansa*" -or $_.PSChildName -like "*Flow*" }
if ($qosPolicies) {
    Write-Host "Removing $(@($qosPolicies).Count) QoS policy(s):" -ForegroundColor Yellow
    foreach ($p in $qosPolicies) {
        Write-Host "  - $($p.PSChildName)"
        try { Remove-Item $p.PSPath -Recurse -Force } catch { Write-Host "    (already gone)" }
    }
    Write-Host "  Done." -ForegroundColor Green
} else {
    Write-Host "No Bansa / Flow QoS policies." -ForegroundColor Green
}

# ── 3. Task Scheduler ─────────────────────────────────────────────────────────
Write-Host ""
$tasks = Get-ScheduledTask -ErrorAction SilentlyContinue |
    Where-Object { $_.TaskName -like "*Bansa*" -or $_.TaskName -like "*Flow*" }
if ($tasks) {
    Write-Host "Removing $(@($tasks).Count) scheduled task(s):" -ForegroundColor Yellow
    foreach ($t in $tasks) {
        Write-Host "  - $($t.TaskName)"
        Unregister-ScheduledTask -TaskName $t.TaskName -Confirm:$false -ErrorAction SilentlyContinue
    }
    Write-Host "  Done." -ForegroundColor Green
} else {
    Write-Host "No Bansa / Flow scheduled tasks." -ForegroundColor Green
}

# ── 4. ETW sessions ───────────────────────────────────────────────────────────
Write-Host ""
foreach ($name in @("Bansa-KernelNetSession", "Flow-KernelNetSession")) {
    $out = logman query $name -ets 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Stopping orphaned ETW session '$name'…" -ForegroundColor Yellow
        logman stop $name -ets | Out-Null
        Write-Host "  Done." -ForegroundColor Green
    }
}

# ── 5. Data folders ───────────────────────────────────────────────────────────
Write-Host ""
foreach ($name in @("Bansa", "Flow")) {
    $folder = Join-Path $env:LOCALAPPDATA $name
    if (Test-Path $folder) {
        $answer = Read-Host "Delete data folder '$folder'? [y/N]"
        if ($answer -eq 'y' -or $answer -eq 'Y') {
            Remove-Item $folder -Recurse -Force
            Write-Host "  Removed." -ForegroundColor Green
        } else {
            Write-Host "  Preserved." -ForegroundColor Green
        }
    }
}

Write-Host ""
Write-Host "Cleanup complete." -ForegroundColor Cyan
Write-Host ""
