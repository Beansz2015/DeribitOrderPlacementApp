# Stops ONLY the harness-launched app recorded in verify/app.pid (launch-app.ps1's counterpart).
# Never kills by name/title — the owner's session is untouchable by construction.
#
# Usage:  powershell -NoProfile -File tools/stop-app.ps1
#
# Exit codes: 0 = stopped (or already gone; PID file removed) · 1 = no PID file.

. "$PSScriptRoot\harness-common.ps1"

if (-not (Test-Path $script:PidFile)) {
    Write-Error "No harness PID file ($script:PidFile) — nothing launched by the harness."
    exit 1
}
$raw = (Get-Content $script:PidFile -TotalCount 1).Trim()
$procId = 0
if ([int]::TryParse($raw, [ref]$procId)) {
    try {
        $proc = Get-Process -Id $procId -ErrorAction Stop
        # CloseMainWindow first: lets the app run its FormClosed teardown (timers, WS close).
        $null = $proc.CloseMainWindow()
        if (-not $proc.WaitForExit(5000)) {
            Stop-Process -Id $procId -Force -Confirm:$false
            Write-Host "PID $procId did not close gracefully — killed."
        } else {
            Write-Host "PID $procId closed."
        }
    } catch {
        Write-Host "PID $procId already gone."
    }
} else {
    Write-Host "PID file content unreadable ('$raw') — removing it."
}
Remove-Item $script:PidFile -Force -ErrorAction SilentlyContinue
exit 0
