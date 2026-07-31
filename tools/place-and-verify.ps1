# Trade-placing click WITH a mandatory effect assertion (triple-placement investigation,
# 2026-08-01). Wraps click-PLACES-ORDER.ps1 - it does NOT re-implement or relax any of that
# script's gates (TESTNET title + harness-launched PID are still enforced there, first).
#
# WHY THIS EXISTS: on 2026-07-27 a single harness Invoke() of Mkt. BUY produced THREE entries
# and a reduce of 30 (docs/runtime-record-emergency-hoist-2026-07-27.md section 5). The 2026-08-01
# investigation could not reproduce it in 53 placements, so the cause was never identified - only
# bounded. This wrapper converts that residual from a SILENT confound (a run that quietly measured
# the wrong thing) into a LOUD failure (the run stops and says so). Standing lesson, harness
# commit-verification trap: a harness that reports work it did not do is worse than one that errors.
#
# Any harness-driven trade placement MUST go through this script rather than calling
# click-PLACES-ORDER.ps1 directly (ROADMAP-2026-08.md section 5, WATCH protocol as amended).
#
# Usage:
#   powershell -NoProfile -File tools/place-and-verify.ps1 btnMarket "Market buy order placed"
#   powershell -NoProfile -File tools/place-and-verify.ps1 btnLimit "Limit buy order placed" -ExpectCount 1
#
# Exit codes: 0 = clicked AND the log shows exactly the expected effect ·
#             1 = app not running · 2 = no button matched ·
#             3 = refused by the underlying gates, OR THE EFFECT ASSERTION FAILED (evidence dumped).

param(
    [Parameter(Mandatory=$true, Position=0)] [string]$NamePattern,
    [Parameter(Mandatory=$true, Position=1)] [string]$ExpectPattern,
    [int]$ExpectCount = 1,
    [int]$SettleSeconds = 5,
    # Passed through to click-PLACES-ORDER.ps1. The point of a burst is that ExpectCount stays 1
    # while Actuations is 3: N actuations must still yield ONE order once the single-flight guard
    # is in (spec-placement-single-flight.md §4.2). Pre-fix the same call reports observed=3.
    [ValidateRange(1,5)]
    [int]$Actuations = 1
)

$ErrorActionPreference = 'Continue'
$toolsDir = $PSScriptRoot
$repoRoot = Split-Path $toolsDir -Parent
$outDir   = Join-Path $repoRoot 'verify\out'

function Read-AppLog {
    (& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $toolsDir 'read-log.ps1') 2>&1) -join "`n"
}
function Count-In([string]$text, [string]$pattern) { ([regex]::Matches($text, $pattern)).Count }

# Baseline BEFORE the click. Counting a delta rather than clearing the log keeps the surrounding
# session context intact for whoever reads the evidence dump.
$before = Read-AppLog
$baseline = Count-In $before $ExpectPattern

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $toolsDir 'click-PLACES-ORDER.ps1') $NamePattern -Actuations $Actuations
$clickExit = $LASTEXITCODE
if ($clickExit -ne 0) { exit $clickExit }

Start-Sleep -Seconds $SettleSeconds

$after = Read-AppLog
$delta = (Count-In $after $ExpectPattern) - $baseline

if ($delta -eq $ExpectCount) {
    Write-Host "Effect verified: '$ExpectPattern' x$delta after one Invoke of '$NamePattern'."
    exit 0
}

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory $outDir | Out-Null }
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$dump  = Join-Path $outDir "PLACEMENT-ASSERT-FAILED-$stamp.log"
@"
=== place-and-verify.ps1 assertion FAILED ===
button        : $NamePattern
pattern       : $ExpectPattern
expected      : $ExpectCount
observed      : $delta
baseline count: $baseline

=== log BEFORE the click ===
$before

=== log AFTER the click ===
$after
"@ | Out-File -FilePath $dump -Encoding utf8

# The UIA tree at the moment of failure is what distinguishes "UIA invoked the button N times"
# from "the handler re-entered" - the question the 2026-07-27 incident could not answer.
(& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $toolsDir 'inspect-tree.ps1') 2>&1) |
    Out-File -FilePath ($dump -replace '\.log$', '-tree.txt') -Encoding utf8

Write-Error "PLACEMENT ASSERTION FAILED: expected $ExpectCount x '$ExpectPattern', observed $delta. THIS MAY BE THE TRIPLE-PLACEMENT RECURRENCE - stop, do not treat any downstream result as valid, and read $dump (+ -tree.txt)."
exit 3
