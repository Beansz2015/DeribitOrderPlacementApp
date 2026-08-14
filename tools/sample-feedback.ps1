# tools/sample-feedback.ps1 - READ-ONLY sampler for executor_feedback.json.
#
# WHY THIS EXISTS
# ---------------
# executor_feedback.json is OVERWRITTEN on every publish. The ws DOWN state therefore lives in the
# file only from the disconnect until the reconnect - 23 seconds in the 2026-08-14 ws-edge session.
# A human reading the file by hand will almost always miss that window and will instead find the
# graceful-close DOWN written at shutdown, which is a DIFFERENT and uninformative artefact. That
# exact confusion is recorded in docs/HANDOVER-6.md section 2 item 7.
#
# This script archives every distinct publish so the DOWN snapshot is captured whether or not
# anyone is watching, and so it can be joined to the ws-edges.log row on timestamp.
#
# SAFETY: read-only. It never writes to, locks, or deletes the emitter's file. It opens with
# FileShare Read+Write+Delete so it can never block the emitter's atomic File.Replace.
#
# Run:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\sample-feedback.ps1 -Minutes 45
#
# Stop with Ctrl-C at any time; everything already archived is kept.

[CmdletBinding()]
param(
    # The emitter's output file. Default matches bridge.json's feedback_output_path.
    [string] $Path = 'C:\Dev\DeribitBridge\executor_feedback.json',

    # Where the snapshots go. Created if absent. Never inside the app's bin.
    [string] $OutDir = '',

    # How long to sample for. The session ends early on Ctrl-C.
    [int] $Minutes = 60,

    # Poll interval. 250 ms is far below the heartbeat and below any realistic edge window.
    [int] $IntervalMs = 250
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $OutDir = Join-Path $repo 'verify\feedback-samples'
}
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }

$indexPath = Join-Path $OutDir 'index.txt'

Write-Host "sample-feedback: watching $Path"
Write-Host "sample-feedback: archiving to $OutDir"
Write-Host "sample-feedback: $Minutes minute(s), polling every $IntervalMs ms. Ctrl-C to stop."
Write-Host ''

# Read the file without ever blocking the emitter. Returns $null on a transient race.
function Read-Shared([string] $p) {
    try {
        $fs = New-Object IO.FileStream($p, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                                       ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try {
            $sr = New-Object IO.StreamReader($fs)
            try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
        } finally { $fs.Dispose() }
    } catch { return $null }
}

$deadline = (Get-Date).AddMinutes($Minutes)
$lastRaw  = ''
$captured = 0
$missing  = $true

while ((Get-Date) -lt $deadline) {

    $raw = Read-Shared $Path

    if ($null -eq $raw) {
        # Either the file does not exist yet, or we hit the atomic replace. Both are normal.
        if (-not (Test-Path $Path) -and -not $missing) {
            Write-Host ("{0}  FILE DISAPPEARED" -f (Get-Date -Format 'HH:mm:ss.fff')) -ForegroundColor Yellow
            $missing = $true
        }
        Start-Sleep -Milliseconds $IntervalMs
        continue
    }

    if ($missing) {
        Write-Host ("{0}  file present" -f (Get-Date -Format 'HH:mm:ss.fff')) -ForegroundColor Gray
        $missing = $false
    }

    if ($raw -ne $lastRaw -and -not [string]::IsNullOrWhiteSpace($raw)) {

        $stampFile = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        $stampView = Get-Date -Format 'HH:mm:ss.fff'

        # Archive the bytes verbatim BEFORE parsing, so a malformed publish is still evidence.
        $dest = Join-Path $OutDir "feedback-$stampFile.json"
        [IO.File]::WriteAllText($dest, $raw)
        $captured++

        $fid = '?'; $ws = '?'; $mode = '?'; $armed = '?'; $started = '?'; $dir = '?'; $gen = '?'
        try {
            $j       = $raw | ConvertFrom-Json
            $fid     = $j.feedback_id
            $gen     = $j.generated_at_utc
            $ws      = $j.executor.ws
            $mode    = $j.executor.mode
            $armed   = $j.executor.armed
            $started = $j.executor.started
            $dir     = $j.position.direction
        } catch {
            $fid = 'PARSE-FAIL'
        }

        $row = "{0} | local {1} | id {2} | ws {3} | mode {4} | armed {5} | started {6} | pos {7} | gen {8}" -f `
               $stampFile, $stampView, $fid, $ws, $mode, $armed, $started, $dir, $gen
        Add-Content -Path $indexPath -Value $row -Encoding utf8

        $colour = 'Gray'
        if ($ws -eq 'DOWN') { $colour = 'Red' } elseif ($ws -eq 'OK') { $colour = 'Green' }
        Write-Host $row -ForegroundColor $colour

        $lastRaw = $raw
    }

    Start-Sleep -Milliseconds $IntervalMs
}

Write-Host ''
Write-Host "sample-feedback: done. $captured distinct publish(es) archived."
Write-Host "sample-feedback: index -> $indexPath"
