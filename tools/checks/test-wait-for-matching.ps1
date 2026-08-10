# Direct exercise of Wait-ForMatchingElement (docs/spec-harness-owned-window.md section R11.4) -
# shadows Select-MatchingElement with a scripted mock so the poll/deadline/Ambiguous logic is
# provable with no running app. Dot-sourcing harness-common.ps1 brings both functions into this
# script's scope; redefining Select-MatchingElement AFTER that point overrides it for every
# subsequent call Wait-ForMatchingElement makes, because PowerShell resolves a function call by
# name in the current scope at CALL time, not by an early-bound reference.
#
# Run manually:  powershell -NoProfile -ExecutionPolicy Bypass -File tools/checks/test-wait-for-matching.ps1
#
# Exit codes: 0 = all cases passed · 1 = at least one case failed.

. "$PSScriptRoot\..\harness-common.ps1"

$script:failed = $false
function Ok($m)   { Write-Host "OK    $m" -ForegroundColor Green }
function Fail($m) { $script:failed = $true; Write-Host "FAIL  $m" -ForegroundColor Red }

function Reset-Mock { $script:mockCallCount = 0 }

# --- Case 1: returns on attempt 1 when the first result matches ---
Reset-Mock
function Select-MatchingElement {
    param($Windows, $TypeName, $Pattern, $LabelBuilder)
    $script:mockCallCount++
    return @{ Result = @{ Kind = 'ExactId' }; Element = 'FOUND' }
}
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$r = Wait-ForMatchingElement -Windows @() -TypeName Edit -Pattern x -DeadlineMs 500 -IntervalMs 25
$sw.Stop()
if ($r.Result.Kind -eq 'ExactId' -and $script:mockCallCount -eq 1 -and $sw.ElapsedMilliseconds -lt 100) {
    Ok "attempt 1 match: 1 call, no sleep ($($sw.ElapsedMilliseconds) ms)"
} else {
    Fail "attempt 1 match: calls=$($script:mockCallCount) Kind=$($r.Result.Kind) ms=$($sw.ElapsedMilliseconds)"
}

# --- Case 2: RETRIES and succeeds on attempt 3 when the first two return None ---
Reset-Mock
function Select-MatchingElement {
    param($Windows, $TypeName, $Pattern, $LabelBuilder)
    $script:mockCallCount++
    if ($script:mockCallCount -lt 3) { return @{ Result = @{ Kind = 'None' }; Element = $null } }
    return @{ Result = @{ Kind = 'ExactId' }; Element = 'FOUND' }
}
$r = Wait-ForMatchingElement -Windows @() -TypeName Edit -Pattern x -DeadlineMs 500 -IntervalMs 25
if ($r.Result.Kind -eq 'ExactId' -and $script:mockCallCount -eq 3) {
    Ok "retries to attempt 3: $($script:mockCallCount) calls, Kind=$($r.Result.Kind)"
} else {
    Fail "retries to attempt 3: calls=$($script:mockCallCount) Kind=$($r.Result.Kind)"
}

# --- Case 3: returns Ambiguous on attempt 1 without sleeping (the safety case) ---
Reset-Mock
function Select-MatchingElement {
    param($Windows, $TypeName, $Pattern, $LabelBuilder)
    $script:mockCallCount++
    return @{ Result = @{ Kind = 'Ambiguous'; Tied = @(0, 1) }; Element = $null }
}
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$r = Wait-ForMatchingElement -Windows @() -TypeName Edit -Pattern x -DeadlineMs 500 -IntervalMs 25
$sw.Stop()
if ($r.Result.Kind -eq 'Ambiguous' -and $script:mockCallCount -eq 1 -and $sw.ElapsedMilliseconds -lt 100) {
    Ok "Ambiguous on attempt 1: 1 call, no sleep ($($sw.ElapsedMilliseconds) ms)"
} else {
    Fail "Ambiguous on attempt 1: calls=$($script:mockCallCount) Kind=$($r.Result.Kind) ms=$($sw.ElapsedMilliseconds)"
}

# --- Case 4: times out with Kind='None' and does not hang ---
Reset-Mock
function Select-MatchingElement {
    param($Windows, $TypeName, $Pattern, $LabelBuilder)
    $script:mockCallCount++
    return @{ Result = @{ Kind = 'None' }; Element = $null }
}
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$r = Wait-ForMatchingElement -Windows @() -TypeName Edit -Pattern x -DeadlineMs 200 -IntervalMs 25
$sw.Stop()
if ($r.Result.Kind -eq 'None' -and $script:mockCallCount -gt 1 -and $sw.ElapsedMilliseconds -lt 1000) {
    Ok "timeout: Kind=None after $($script:mockCallCount) calls in $($sw.ElapsedMilliseconds) ms (did not hang)"
} else {
    Fail "timeout: calls=$($script:mockCallCount) Kind=$($r.Result.Kind) ms=$($sw.ElapsedMilliseconds)"
}

# --- Case 5 (D1's regression test): a slow first attempt that exceeds the deadline STILL
# permits at least one retry. Fails against the pre-D1 code: there, the clock starts before
# attempt 1, so a 600 ms first attempt alone already exceeds a 500 ms deadline and the loop
# returns without ever reaching Start-Sleep. ---
Reset-Mock
function Select-MatchingElement {
    param($Windows, $TypeName, $Pattern, $LabelBuilder)
    $script:mockCallCount++
    if ($script:mockCallCount -eq 1) {
        Start-Sleep -Milliseconds 600   # simulates the observed 740-950 ms cold-process first query
        return @{ Result = @{ Kind = 'None' }; Element = $null }
    }
    return @{ Result = @{ Kind = 'ExactId' }; Element = 'FOUND' }
}
$r = Wait-ForMatchingElement -Windows @() -TypeName Edit -Pattern x -DeadlineMs 500 -IntervalMs 25
if ($r.Result.Kind -eq 'ExactId' -and $script:mockCallCount -eq 2) {
    Ok "D1 regression: a slow (600ms) first attempt still permits a retry ($($script:mockCallCount) calls)"
} else {
    Fail "D1 regression: calls=$($script:mockCallCount) Kind=$($r.Result.Kind) -- the deadline clock is eating the first attempt's cost"
}

Write-Host ""
if ($script:failed) {
    Write-Host 'WAIT-FOR-MATCHING TESTS FAILED' -ForegroundColor Red
    exit 1
}
Write-Host 'WAIT-FOR-MATCHING TESTS PASSED' -ForegroundColor Green
exit 0
