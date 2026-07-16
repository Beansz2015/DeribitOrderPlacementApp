# Restores the payload backed up by tools/write-payload.ps1 (<path>.harness-backup -> path).
# Same engine-stopped refusal as the writer — restoring under a live emitter is at best a
# no-op and at worst a mid-replace collision.
#
# Usage:  powershell -NoProfile -File tools/restore-payload.ps1 [-BinDir <running-bin>]
#
# Exit codes: 0 = restored (or nothing to restore — check stdout) · 2 = payload path
#             unresolvable · 3 = refused (engine running).

param([string]$BinDir = "")

. "$PSScriptRoot\harness-common.ps1"

$engine = Get-Process -Name "DeribitVerdictEngine" -ErrorAction SilentlyContinue
if ($engine) {
    Write-Error "REFUSED: DeribitVerdictEngine is running (PID $($engine.Id -join ', ')). Stop it before touching the payload."
    exit 3
}

if (-not $BinDir) {
    $form = Find-MainFormElement
    if ($null -ne $form) {
        try { $BinDir = Split-Path (Get-Process -Id $form.Current.ProcessId -ErrorAction Stop).Path -Parent } catch {}
    }
}
if (-not $BinDir) {
    $BinDir = Join-Path $script:RepoRoot "DeribitOrderPlacementApp\bin\Debug\net9.0-windows8.0"
}

$payloadPath = "C:\Dev\DeribitBridge\verdict_signal.json"
$bridgeCfg = Join-Path $BinDir "bridge.json"
if (Test-Path $bridgeCfg) {
    try {
        $cfg = Get-Content $bridgeCfg -Raw | ConvertFrom-Json
        if ($cfg.path) { $payloadPath = [string]$cfg.path }
    } catch {
        Write-Error "bridge.json at $bridgeCfg is unreadable: $_"
        exit 2
    }
}

$backup = "$payloadPath.harness-backup"
if (-not (Test-Path $backup)) {
    Write-Host "No backup at $backup — nothing to restore (write-payload.ps1 never ran, or already restored)."
    exit 0
}
Move-Item -Path $backup -Destination $payloadPath -Force
Write-Host "Restored $payloadPath from $backup"
exit 0
