param()
# pack-release.ps1
# Builds Bansa.zip so extracting produces:
#   Bansa\
#   Bansa\Bansa.exe
# Upload that zip to a GitHub Release.
# To update, users replace only Bansa.exe -- Data\ stays intact.

$ErrorActionPreference = "Stop"

$root       = $PSScriptRoot
$project    = Join-Path $root "src\Bansa\Bansa.csproj"
$publishDir = Join-Path $root "publish"
$releaseDir = Join-Path $root "release"
$stageDir   = Join-Path $releaseDir "Bansa"
$zipOut     = Join-Path $releaseDir "Bansa.zip"

Write-Host ""
Write-Host "Bansa release packager" -ForegroundColor Cyan
Write-Host "----------------------"
Write-Host ""

# 1. Publish (framework-dependent, single file)
Write-Host "Building..." -ForegroundColor Yellow
& dotnet publish $project -c Release -r win-x64 --self-contained false `
    "-p:PublishSingleFile=true" `
    "-p:IncludeNativeLibrariesForSelfExtract=true" `
    -o $publishDir --nologo -v quiet

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $publishDir "Bansa.exe"
if (-not (Test-Path $exe)) { throw "Bansa.exe not found after publish" }

$sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "  Bansa.exe -- $sizeMB MB" -ForegroundColor Green

# 2. Stage into Bansa\ subfolder so the zip extracts cleanly
Write-Host "Staging..." -ForegroundColor Yellow
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }
New-Item -ItemType Directory $stageDir | Out-Null
Copy-Item $exe $stageDir

# 3. Zip
Write-Host "Zipping..." -ForegroundColor Yellow
Compress-Archive -Path $stageDir -DestinationPath $zipOut -Force

$zipMB = [math]::Round((Get-Item $zipOut).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: release\Bansa.zip ($zipMB MB)" -ForegroundColor Green
Write-Host "Upload Bansa.zip to a GitHub Release." -ForegroundColor Cyan
Write-Host ""
