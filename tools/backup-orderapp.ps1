# tools/backup-orderapp.ps1 - zip the order app's operational state to a dated archive
# (docs/spec-quickwins-notifier-signalcols.md Q3).
#
# Zips, from the app's REAL session bin (the x64 Debug bin - the AnyCPU bin belongs to the
# UI-test harness):
#   orderapp-settings.json    trade defaults + risk/alerts + circuit breaker (item A persistence)
#   trades.db                 the trade journal (SQLite)
#   bridge-state.json         the bridge's acted de-dupe watermark
#   bridge-dispositions.log   the soak/audit disposition stream
# into <TargetDir>\orderapp-backup-YYYYMMDD-HHmmss.zip. Missing files are warned about and
# skipped (a fresh install has no trades.db yet); at least one present file is required.
#
# Read-only with respect to the app: files are copied to a temp staging folder first, so the
# zip never holds the live files open. trades.db writes are short transactions - a copy taken
# mid-session is fine for backup purposes (SQLite recovers a mid-write snapshot via its
# journal); for a guaranteed-consistent copy run this while the app is closed.
#
# S3 sync (owner's scheduled-task wrapper - document only, not run here):
#   powershell -NoProfile -File tools\backup-orderapp.ps1 -TargetDir D:\backups\orderapp; aws s3 sync D:\backups\orderapp s3://<bucket>/orderapp-backups/
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\backup-orderapp.ps1 -TargetDir D:\backups\orderapp
#   # -BinDir overrides the source bin (default: the repo's x64 Debug bin, resolved from this script's location)

param(
    [Parameter(Mandatory = $true)]
    [string]$TargetDir,
    [string]$BinDir = ''
)

$ErrorActionPreference = 'Stop'

if ($BinDir -eq '') {
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $BinDir = Join-Path $repo 'DeribitOrderPlacementApp\bin\x64\Debug\net9.0-windows8.0'
}
if (-not (Test-Path $BinDir)) {
    Write-Host "FAIL  bin directory not found: $BinDir" -ForegroundColor Red
    exit 1
}

$files = @('orderapp-settings.json', 'trades.db', 'bridge-state.json', 'bridge-dispositions.log')

if (-not (Test-Path $TargetDir)) { New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$zipPath = Join-Path (Resolve-Path $TargetDir).Path "orderapp-backup-$stamp.zip"
$staging = Join-Path $env:TEMP "orderapp-backup-staging-$stamp"
New-Item -ItemType Directory -Force -Path $staging | Out-Null

try {
    $found = 0
    foreach ($name in $files) {
        $src = Join-Path $BinDir $name
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $staging $name)
            $size = (Get-Item $src).Length
            Write-Host ("OK    staged {0} ({1:N0} bytes)" -f $name, $size) -ForegroundColor Green
            $found++
        } else {
            Write-Host "WARN  missing (skipped): $name" -ForegroundColor Yellow
        }
    }
    if ($found -eq 0) {
        Write-Host "FAIL  none of the expected files exist in $BinDir - wrong bin?" -ForegroundColor Red
        exit 1
    }

    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath
    $zipSize = (Get-Item $zipPath).Length
    Write-Host ("OK    wrote {0} ({1:N0} bytes, {2}/{3} files)" -f $zipPath, $zipSize, $found, $files.Count) -ForegroundColor Green
    exit 0
}
finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}
