# tools/policy-report.ps1 - session-policy counterfactual report over bridge-dispositions.log
# (docs/spec-quickwins-notifier-signalcols.md Q3). READ-ONLY.
#
# Per UTC analysis-session bucket (the engine-identical boundaries pinned in SignalBridge:
# ASIA 00-07Z, LONDON 08-12Z, NY 13-23Z) x confidence tier, counts of:
#   would-act            log-only actionable signals
#   acted                live placements
#   policy(tier)         refused: policy(<SESSION>/tier)
#   policy(context)      refused: policy(<SESSION>/context)
# since -Since (optional; parsed invariant - use e.g. 2026-07-16 or an ISO timestamp).
#
# Row format (SOAK-FROZEN, the soak-join parser pattern):
#   utc | instance_id | signal_id | verdict | confidence | direction | disposition
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\policy-report.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\policy-report.ps1 -Since 2026-07-16
#   # -LogPath overrides the source log (default: the repo's x64 Debug bin - the REAL session bin)

param(
    [string]$LogPath = '',
    [string]$Since = ''
)

$ErrorActionPreference = 'Stop'

if ($LogPath -eq '') {
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $LogPath = Join-Path $repo 'DeribitOrderPlacementApp\bin\x64\Debug\net9.0-windows8.0\bridge-dispositions.log'
}
if (-not (Test-Path $LogPath)) {
    Write-Host "FAIL  log not found: $LogPath" -ForegroundColor Red
    exit 1
}

$inv = [System.Globalization.CultureInfo]::InvariantCulture
$sinceUtc = [datetime]::MinValue
if ($Since -ne '') {
    $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
    if (-not [datetime]::TryParse($Since, $inv, $styles, [ref]$sinceUtc)) {
        Write-Host "FAIL  -Since '$Since' does not parse (use e.g. 2026-07-16)" -ForegroundColor Red
        exit 1
    }
}

function BucketFor([int]$utcHour) {
    if ($utcHour -ge 13) { return 'NY' }
    if ($utcHour -ge 8)  { return 'LONDON' }
    return 'ASIA'
}

# stats[bucket|tier] = counters. Malformed/short rows are counted, never fatal (read-only tool).
$stats = @{}
$total = 0; $inRange = 0; $malformed = 0

foreach ($line in [System.IO.File]::ReadLines($LogPath)) {
    if ($line.Trim() -eq '') { continue }
    $total++
    # The disposition (last field) can itself contain ' | '-free commas only, but split
    # defensively: 7 parts max, so a disposition containing the separator stays whole.
    $parts = $line -split ' \| ', 7
    if ($parts.Count -lt 7) { $malformed++; continue }

    $rowUtc = [datetime]::MinValue
    $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
    if (-not [datetime]::TryParse($parts[0], $inv, $styles, [ref]$rowUtc)) { $malformed++; continue }
    if ($rowUtc -lt $sinceUtc) { continue }
    $inRange++

    $tier = $parts[4]; if ($tier -eq '') { $tier = '(none)' }
    $disposition = $parts[6]
    $bucket = BucketFor $rowUtc.Hour
    $key = "$bucket|$tier"
    if (-not $stats.ContainsKey($key)) {
        $stats[$key] = @{ WouldAct = 0; Acted = 0; PolicyTier = 0; PolicyContext = 0 }
    }
    $s = $stats[$key]
    if ($disposition.StartsWith('would-act'))            { $s.WouldAct++ }
    elseif ($disposition.StartsWith('acted'))            { $s.Acted++ }
    elseif ($disposition -match '^refused: policy\([^)]+/tier\)')    { $s.PolicyTier++ }
    elseif ($disposition -match '^refused: policy\([^)]+/context\)') { $s.PolicyContext++ }
}

Write-Host ""
Write-Host "policy-report over $LogPath"
Write-Host ("rows: {0} total, {1} in range{2}, {3} malformed" -f $total, $inRange,
            $(if ($Since -ne '') { " (since $($sinceUtc.ToString('yyyy-MM-ddTHH:mm:ssZ', $inv)))" } else { '' }),
            $malformed)
Write-Host ""
Write-Host ("{0,-8} {1,-10} {2,10} {3,8} {4,13} {5,16}" -f 'session', 'tier', 'would-act', 'acted', 'policy(tier)', 'policy(context)')
Write-Host ("{0,-8} {1,-10} {2,10} {3,8} {4,13} {5,16}" -f '-------', '----', '---------', '-----', '------------', '---------------')

$bucketOrder = @{ 'ASIA' = 0; 'LONDON' = 1; 'NY' = 2 }
$keys = $stats.Keys | Sort-Object @{ Expression = { $bucketOrder[($_ -split '\|')[0]] } }, @{ Expression = { ($_ -split '\|')[1] } }
foreach ($key in $keys) {
    $bucket, $tier = $key -split '\|'
    $s = $stats[$key]
    Write-Host ("{0,-8} {1,-10} {2,10} {3,8} {4,13} {5,16}" -f $bucket, $tier, $s.WouldAct, $s.Acted, $s.PolicyTier, $s.PolicyContext)
}
if ($stats.Count -eq 0) { Write-Host "(no rows in range)" }
exit 0
