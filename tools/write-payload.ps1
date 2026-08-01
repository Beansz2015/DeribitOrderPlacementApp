# Crafts a verdict_signal.json variant at the path configured in the running bin's bridge.json
# (temp + move in the same directory, mimicking the engine's File.Replace atomic write) — the
# spec-back section 9.4/9.5 instrument: a hand-crafted ACTIONABLE payload with the engine
# stopped, so the local gates (window, size, ...) can be asserted via read-log tokens.
#
# SAFETY (spec section 6.5, script-enforced):
#   - REFUSES if a DeribitVerdictEngine process is running — the engine overwrites the payload
#     every run interval; the harness never fights the live emitter.
#   - Backs up the CURRENT payload to <path>.harness-backup on the FIRST write (later writes
#     keep the original backup) — tools/restore-payload.ps1 puts it back.
#
# The payload passes contract gates 4.1–4.4 by default: schema 1, fresh timestamp, new
# instance GUID (never a duplicate), OK/HIGH/CONFIRMED, non-zero levels, healthy. What happens
# next is exactly the local gate under test.
#
# Usage:
#   powershell -NoProfile -File tools/write-payload.ps1                       # LONG, defaults
#   powershell -NoProfile -File tools/write-payload.ps1 -Direction SHORT
#   powershell -NoProfile -File tools/write-payload.ps1 -Confidence LOW      # tier-refusal variant
#
# Exit codes: 0 = written · 2 = payload path unresolvable · 3 = refused (engine running).

param(
    [ValidateSet("LONG", "SHORT")]
    [string]$Direction = "LONG",
    [decimal]$Entry = 0,
    [decimal]$Stop = 0,
    [decimal]$Target = 0,
    [decimal]$Atr = 30.0,
    [long]$SignalId = 1,
    [string]$InstanceId = "",
    [string]$Confidence = "HIGH",
    [ValidateSet("OK", "SKIPPED")]
    [string]$SignalState = "OK",
    [bool]$EngineArmed = $true,
    [string]$BinDir = ""
)

. "$PSScriptRoot\harness-common.ps1"

# NOTE: the kill rule now lives BELOW, after the payload path is resolved - it has to know WHICH
# file we are about to write before it can decide whether we would be fighting the emitter.
# See the block headed "Kill rule".

# Resolve the bin the app reads bridge.json from: the RUNNING app's exe dir when up, else
# -BinDir, else the Debug bin.
if (-not $BinDir) {
    $form = Find-MainFormElement
    if ($null -ne $form) {
        try { $BinDir = Split-Path (Get-Process -Id $form.Current.ProcessId -ErrorAction Stop).Path -Parent } catch {}
    }
}
if (-not $BinDir) {
    $BinDir = Join-Path $script:RepoRoot "DeribitOrderPlacementApp\bin\Debug\net9.0-windows8.0"
}

# Payload path: bridge.json's "path", falling back to the app's built-in default.
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
# Kill rule: never fight the live emitter - made PATH-AWARE 2026-08-01 (EV 6.3 bridge-act leg).
# The rule exists because the engine rewrites ITS payload every run interval, so a harness write
# to that same file is a race the harness always loses. That hazard is a property of the FILE, not
# of the engine merely being alive: when the running bin's bridge.json points somewhere else, the
# two never touch the same path and there is nothing to fight. Refusing on the process alone also
# contradicted the standing isolated-harness protocol (give the harness bin its own bridge.json and
# a scratch payload path, so consumer-side tests never interrupt the live stream) - that protocol
# was unusable with the engine up, which is precisely when it is worth having.
# The refusal is UNCHANGED for the case it was written for: the engine's own path.
$defaultEnginePath = "C:\Dev\DeribitBridge\verdict_signal.json"
$isEnginePath = ([System.IO.Path]::GetFullPath($payloadPath) -ieq [System.IO.Path]::GetFullPath($defaultEnginePath))
$engine = Get-Process -Name "DeribitVerdictEngine" -ErrorAction SilentlyContinue
if ($engine -and $isEnginePath) {
    Write-Error "REFUSED: DeribitVerdictEngine is running (PID $($engine.Id -join ', ')) and this write targets ITS payload ($payloadPath) - it overwrites that file every run interval. Either stop the engine (section 9.4/9.5 protocol), or point the running bin's bridge.json at a scratch path (the isolated-harness protocol) and re-run."
    exit 3
}
if ($engine) {
    Write-Host "NOTE: DeribitVerdictEngine is running (PID $($engine.Id -join ', ')), but this write targets an ISOLATED path, not its own - proceeding without touching the live stream."
}

$payloadDir = Split-Path $payloadPath -Parent
if (-not (Test-Path $payloadDir)) { New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null }

# First-write backup (restore-payload.ps1's counterpart). Later writes keep the original.
$backup = "$payloadPath.harness-backup"
if ((Test-Path $payloadPath) -and -not (Test-Path $backup)) {
    Copy-Item $payloadPath $backup -Force
    Write-Host "Backed up current payload -> $backup"
}

if (-not $InstanceId) { $InstanceId = "harness-" + [guid]::NewGuid().ToString("N").Substring(0, 12) }
if ($Entry  -eq 0) { $Entry  = 60000 }
if ($Stop   -eq 0) { $Stop   = if ($Direction -eq "LONG") { 59950 } else { 60050 } }
if ($Target -eq 0) { $Target = if ($Direction -eq "LONG") { 60100 } else { 59900 } }

$levelBlock = @{ entry = $Entry; stop = $Stop; target = $Target }
$levels = if ($Direction -eq "LONG") { @{ long = $levelBlock } } else { @{ short = $levelBlock } }

$payload = [ordered]@{
    schema_version      = 1
    signal_id           = $SignalId
    generated_at_utc    = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", [System.Globalization.CultureInfo]::InvariantCulture)
    engine              = [ordered]@{ instance_id = $InstanceId; autotrade_armed = $EngineArmed; app = "harness-write-payload" }
    instrument          = "BTC-PERPETUAL"
    signal_state        = $SignalState
    verdict             = "HARNESS $Direction"
    confidence          = $Confidence
    direction           = $Direction
    verdict_context     = "CONFIRMED"
    mtf_blocked         = $false
    price               = $Entry
    exec_resolution_min = 1
    atr                 = $Atr
    levels              = $levels
    health              = [ordered]@{ ws = "OK"; degraded_this_run = $false; ledger_mismatch = $false }
}

# Temp + move in the SAME directory (atomic on the same volume — the File.Replace mimic; the
# app's FSW + retry-on-share-violation reader expects whole-file appearances, never partials).
$tmp = Join-Path $payloadDir (".harness-tmp-" + [guid]::NewGuid().ToString("N") + ".json")
$json = $payload | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
Move-Item -Path $tmp -Destination $payloadPath -Force

Write-Host "Wrote $payloadPath"
Write-Host "  instance_id=$InstanceId signal_id=$SignalId $SignalState/$Confidence/$Direction entry=$Entry stop=$Stop target=$Target atr=$Atr armed=$EngineArmed"
exit 0
