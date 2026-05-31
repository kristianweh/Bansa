#requires -RunAsAdministrator
<#
    Inspect-Bansa.ps1
    -----------------
    Read-only inventory of everything Bansa (and its predecessor "Flow") has
    put on the system. Run any time you want to verify what is still active.
#>

Write-Host ""
Write-Host "Bansa — System Inventory" -ForegroundColor Cyan
Write-Host "------------------------"
Write-Host ""

# ── Firewall rules ────────────────────────────────────────────────────────────
$fwRules = (New-Object -ComObject HNetCfg.FwPolicy2).Rules |
    Where-Object { $_.Name -like "*Bansa*" -or $_.Name -like "*Flow*" }
if ($fwRules) {
    Write-Host "Firewall rules ($(@($fwRules).Count)):" -ForegroundColor Yellow
    $fwRules | Select-Object Name,
        @{n='Dir';   e={ if ($_.Direction -eq 1) { 'In' } else { 'Out' } }},
        @{n='Action';e={ if ($_.Action   -eq 0) { 'Block' } else { 'Allow' } }},
        ApplicationName |
        Format-Table -AutoSize
} else {
    Write-Host "No Bansa / Flow firewall rules." -ForegroundColor Green
}

# ── QoS registry policies ─────────────────────────────────────────────────────
$qosBase = "HKLM:\SOFTWARE\Policies\Microsoft\Windows\QoS"
$qosPolicies = Get-ChildItem $qosBase -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -like "*Bansa*" -or $_.PSChildName -like "*Flow*" }
if ($qosPolicies) {
    Write-Host ""
    Write-Host "QoS registry policies ($(@($qosPolicies).Count)):" -ForegroundColor Yellow
    $qosPolicies | ForEach-Object {
        $k = $_
        $throttle = (Get-ItemProperty $k.PSPath -Name "Throttle Rate" -EA SilentlyContinue)."Throttle Rate"
        $dscp     = (Get-ItemProperty $k.PSPath -Name "DSCP Value"    -EA SilentlyContinue)."DSCP Value"
        $app      = (Get-ItemProperty $k.PSPath -Name "Application Name" -EA SilentlyContinue)."Application Name"
        [PSCustomObject]@{ Name = $k.PSChildName; App = $app; ThrottleBps = $throttle; DSCP = $dscp }
    } | Format-Table -AutoSize
} else {
    Write-Host ""
    Write-Host "No Bansa / Flow QoS policies." -ForegroundColor Green
}

# ── Task Scheduler (auto-start) ───────────────────────────────────────────────
$tasks = Get-ScheduledTask -ErrorAction SilentlyContinue |
    Where-Object { $_.TaskName -like "*Bansa*" -or $_.TaskName -like "*Flow*" }
if ($tasks) {
    Write-Host ""
    Write-Host "Scheduled tasks ($(@($tasks).Count)):" -ForegroundColor Yellow
    $tasks | Select-Object TaskName, TaskPath, State | Format-Table -AutoSize
} else {
    Write-Host ""
    Write-Host "No Bansa / Flow scheduled tasks." -ForegroundColor Green
}

# ── ETW session ───────────────────────────────────────────────────────────────
$etwNames = @("Bansa-KernelNetSession", "Flow-KernelNetSession")
foreach ($name in $etwNames) {
    $out = logman query $name -ets 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "ETW session '$name': RUNNING" -ForegroundColor Yellow
    }
}
Write-Host ""
Write-Host "ETW check done." -ForegroundColor Green

# ── Data folders ──────────────────────────────────────────────────────────────
Write-Host ""
foreach ($name in @("Bansa", "Flow")) {
    $folder = Join-Path $env:LOCALAPPDATA $name
    if (Test-Path $folder) {
        $size = (Get-ChildItem $folder -Recurse -EA SilentlyContinue |
                 Measure-Object -Property Length -Sum).Sum
        $sizeMb = [math]::Round($size / 1MB, 2)
        Write-Host "Data folder: $folder  ($sizeMb MB)" -ForegroundColor Yellow
        Get-ChildItem $folder | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
    }
}

Write-Host ""
