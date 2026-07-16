# Builds the app (Debug) and launches bin\Debug's exe as the HARNESS-OWNED session, recording
# its PID in verify/app.pid. Drive scripts (click/set/toggle/close) only ever operate on this
# PID's windows.
#
# SAFETY (spec section 6.3): REFUSES to launch when ANY "Deribit Order Placement App" window
# already exists — the harness must never end up driving the owner's session, and two running
# copies would fight over bridge-state/DB files.
#
# Usage:  powershell -NoProfile -File tools/launch-app.ps1 [-NoBuild]
#
# Exit codes: 0 = app launched and window found · 2 = exe missing / window never appeared ·
#             3 = refused (a matching window pre-exists / a previous harness PID still runs / build failed).

param([switch]$NoBuild)

. "$PSScriptRoot\harness-common.ps1"

# Refusal 1: any matching window pre-exists (owner session or leftover).
$existing = Find-MainFormElement
if ($null -ne $existing) {
    Write-Error "REFUSED: a window titled '$($existing.Current.Name)' (PID $($existing.Current.ProcessId)) already exists. The harness never drives a pre-existing session — close it (or stop-app.ps1 a leftover harness run) first."
    exit 3
}

# Refusal 2: a previous harness-launched process is still alive (window may just be minimized
# to nothing or mid-startup).
$prevPid = Get-HarnessPid
if ($null -ne $prevPid) {
    Write-Error "REFUSED: a previous harness-launched app (PID $prevPid) is still running. Run tools/stop-app.ps1 first."
    exit 3
}

$proj = Join-Path $script:RepoRoot "DeribitOrderPlacementApp\DeribitOrderPlacementApp.vbproj"
if (-not $NoBuild) {
    Write-Host "Building Debug..."
    & dotnet build $proj -c Debug --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { Write-Error "REFUSED: Debug build failed."; exit 3 }
}

$exe = Join-Path $script:RepoRoot "DeribitOrderPlacementApp\bin\Debug\net9.0-windows8.0\DeribitOrderPlacementApp.exe"
if (-not (Test-Path $exe)) { Write-Error "Exe not found: $exe"; exit 2 }

$proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
$pidDir = Split-Path $script:PidFile -Parent
if (-not (Test-Path $pidDir)) { New-Item -ItemType Directory -Path $pidDir -Force | Out-Null }
Set-Content -Path $script:PidFile -Value $proc.Id -Encoding ascii
Write-Host "Launched PID $($proc.Id); waiting for the main form..."

$deadline = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $deadline) {
    $form = Find-MainFormElement
    if ($null -ne $form -and $form.Current.ProcessId -eq $proc.Id) {
        Write-Host "Up: '$($form.Current.Name)' (PID $($proc.Id); recorded in verify/app.pid)"
        exit 0
    }
    if ($proc.HasExited) {
        Remove-Item $script:PidFile -Force -ErrorAction SilentlyContinue
        Write-Error "App process exited during startup (exit code $($proc.ExitCode))."
        exit 2
    }
    Start-Sleep -Milliseconds 250
}
Write-Error "Timed out waiting for the main form window (PID $($proc.Id) still running — check manually)."
exit 2
