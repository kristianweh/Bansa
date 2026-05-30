#requires -RunAsAdministrator
<#
    Inspect-Bansa.ps1
    ----------------
    Read-only inventory of everything Bansa has put on the system.
    Use this any time you want to verify what Bansa has done.
#>

Write-Host ""
Write-Host "Bansa — System Inventory" -ForegroundColor Cyan
Write-Host "------------------------"
Write-Host ""

# Firewall rules
$rules = Get-NetFirewallRule -DisplayName 'Bansa-*' -ErrorAction SilentlyContinue
if ($rules) {
    Write-Host "Firewall rules ($($rules.Count)):" -ForegroundColor Yellow
    $rules | Select-Object DisplayName, Direction, Action, Enabled | Format-Table -AutoSize
} else {
    Write-Host "No Bansa firewall rules." -ForegroundColor Green
}

# QoS policies
$policies = Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Bansa-*' }
if ($policies) {
    Write-Host "QoS policies ($($policies.Count)):" -ForegroundColor Yellow
    $policies | Select-Object Name, AppPathNameMatchCondition,
        @{n='RateBps';e={$_.ThrottleRateActionBitsPerSecond}},
        @{n='DSCP';e={$_.DSCPValue}} | Format-Table -AutoSize
} else {
    Write-Host "No Bansa QoS policies." -ForegroundColor Green
}

# ETW session
$session = logman query 'Bansa-KernelNetSession' -ets 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "ETW session 'Bansa-KernelNetSession': running" -ForegroundColor Yellow
} else {
    Write-Host ""
    Write-Host "No Bansa ETW session running." -ForegroundColor Green
}

# Data folder
$dataFolder = Join-Path $env:LocalAppData "Bansa"
if (Test-Path $dataFolder) {
    $size = (Get-ChildItem $dataFolder -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    $sizeMb = [math]::Round($size / 1MB, 2)
    Write-Host ""
    Write-Host "Data folder: $dataFolder ($sizeMb MB)" -ForegroundColor Yellow
    Get-ChildItem $dataFolder | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
} else {
    Write-Host ""
    Write-Host "No Bansa data folder." -ForegroundColor Green
}

Write-Host ""
