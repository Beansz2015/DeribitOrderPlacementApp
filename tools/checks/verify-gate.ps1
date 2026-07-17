# tools/checks/verify-gate.ps1 — the build + logic-fixture gate (docs/spec-ui-test-harness.md
# section 5; owner decision 3: build + logic fixtures ONLY — no UI layer on the gate).
#
# Sequence: build Release (sln) -> build Debug (app + OrderCheck vbproj directly — the sln maps
# Debug|AnyCPU to the app's Release config, so an sln Debug build would silently skip the config
# the owner debugs in) -> run OrderCheck -> repo guards -> one GATE PASSED/FAILED line.
#
# Run manually:  powershell -NoProfile -ExecutionPolicy Bypass -File tools/checks/verify-gate.ps1
# Installed as the pre-push hook by tools/install-hooks.ps1.
#
# Exit codes: 0 = GATE PASSED · 1 = GATE FAILED.

$ErrorActionPreference = 'Continue'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo

$script:failed = $false
function Section($t) { Write-Host ""; Write-Host "=== $t ===" }
function Ok($m)   { Write-Host "OK    $m" -ForegroundColor Green }
function Fail($m) { $script:failed = $true; Write-Host "FAIL  $m" -ForegroundColor Red }

function Build($proj, $config) {
    Section "build $proj ($config)"
    & dotnet build $proj -c $config --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { Fail "build failed: $proj ($config)"; return $false }
    Ok "build $proj ($config)"
    return $true
}

# --- builds: Release (sln) then Debug (vbproj — see header for why not sln Debug) ---
$ok = Build 'DeribitOrderPlacementApp.sln' 'Release'
if ($ok) { $ok = Build 'DeribitOrderPlacementApp\DeribitOrderPlacementApp.vbproj' 'Debug' }
if ($ok) { $ok = Build 'tools\OrderCheck\OrderCheck.vbproj' 'Debug' }

# --- OrderCheck logic harness ---
if ($ok) {
    Section 'OrderCheck fixtures'
    $out = & dotnet run --project 'tools\OrderCheck\OrderCheck.vbproj' -c Release --no-build
    $code = $LASTEXITCODE
    $out | ForEach-Object { Write-Host $_ }
    if ($code -ne 0 -or -not ($out -match '^OK \d+/\d+$')) {
        Fail "OrderCheck did not report OK n/n (exit $code)"
    } else {
        Ok 'OrderCheck all fixtures pass'
    }
}

# --- repo guards ---
Section 'repo guards'
$tracked = @(& git ls-files)

# Local-only config must NEVER be tracked (secrets/keys, machine paths, harness enablement).
foreach ($name in @('secrets.json', 'bridge.json', 'bridge-state.json', 'harness.json', 'orderapp-settings.json')) {
    $hits = @($tracked | Where-Object { ($_ -split '/')[-1] -ieq $name })
    if ($hits.Count -gt 0) {
        Fail "local-only file is TRACKED: $($hits -join ', ') — untrack it before pushing"
    } else {
        Ok "$name untracked"
    }
}

# The safety tier's deny list and the shipped .example templates must exist.
foreach ($path in @('tools/trade-buttons.txt',
                    'DeribitOrderPlacementApp/secrets.example.json',
                    'DeribitOrderPlacementApp/bridge.example.json',
                    'DeribitOrderPlacementApp/harness.example.json',
                    'DeribitOrderPlacementApp/orderapp-settings.example.json')) {
    if (Test-Path (Join-Path $repo $path)) { Ok "$path present" } else { Fail "$path MISSING" }
}

# --- result ---
Section 'result'
if ($script:failed) {
    Write-Host 'GATE FAILED' -ForegroundColor Red
    exit 1
}
Write-Host 'GATE PASSED' -ForegroundColor Green
exit 0
