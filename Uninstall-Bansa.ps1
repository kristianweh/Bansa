#requires -RunAsAdministrator
<#
    Uninstall-Bansa.ps1
    ------------------
    Standalone cleanup script. Removes every Windows change Bansa has made,
    even if Bansa itself is broken / missing / won't start.

    Run from an elevated PowerShell prompt:
        powershell -ExecutionPolicy Bypass -File .\Uninstall-Bansa.ps1
#>

Write-Host ""
Write-Host "Bansa — Standalone Cleanup" -ForegroundColor Cyan
Write-Host "---------------------------"
Write-Host ""

# 1. Remove all firewall rules tagged 'Bansa-'
$rules = Get-NetFirewallRule -DisplayName 'Bansa-*' -ErrorAction SilentlyContinue
if ($rules) {
    Write-Host "Removing $($rules.Count) Bansa firewall rule(s):" -ForegroundColor Yellow
    foreach ($r in $rules) {
        Write-Host "  - $($r.DisplayName)"
    }
    $rules | Remove-NetFirewallRule
} else {
    Write-Host "No Bansa firewall rules found." -ForegroundColor Green
}

# 2. Remove all QoS policies tagged 'Bansa-'
$policies = Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Bansa-*' }
if ($policies) {
    Write-Host ""
    Write-Host "Removing $($policies.Count) Bansa QoS policy(s):" -ForegroundColor Yellow
    foreach ($p in $policies) {
        Write-Host "  - $($p.Name)"
    }
    $policies | Remove-NetQosPolicy -Confirm:$false
} else {
    Write-Host "No Bansa QoS policies found." -ForegroundColor Green
}

# 3. Stop any leftover ETW session (e.g. after a Bansa crash)
$session = Get-EtwTraceSession -Name 'Bansa-KernelNetSession' -ErrorAction SilentlyContinue
if ($session) {
    Write-Host ""
    Write-Host "Stopping orphaned ETW session 'Bansa-KernelNetSession'..." -ForegroundColor Yellow
    logman stop Bansa-KernelNetSession -ets | Out-Null
}

# 4. Offer to delete the data folder
$dataFolder = Join-Path $env:LocalAppData "Bansa"
if (Test-Path $dataFolder) {
    Write-Host ""
    $answer = Read-Host "Delete history & settings folder at $dataFolder ? [y/N]"
    if ($answer -eq 'y' -or $answer -eq 'Y') {
        Remove-Item -Recurse -Force $dataFolder
        Write-Host "Data folder removed." -ForegroundColor Green
    } else {
        Write-Host "Data folder preserved." -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Cleanup complete. Bansa has left no further trace on the system." -ForegroundColor Cyan
Write-Host ""
