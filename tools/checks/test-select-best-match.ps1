# Direct exercise of Select-BestMatchIndex (docs/spec-harness-exact-match.md acceptance 7) —
# a pure function over plain data, so this runs with no app, no UIA and no window.
#
# Run manually:  powershell -NoProfile -ExecutionPolicy Bypass -File tools/checks/test-select-best-match.ps1
#
# Exit codes: 0 = all cases passed · 1 = at least one case failed.

. "$PSScriptRoot\..\harness-common.ps1"

$script:failed = $false
function Ok($m)   { Write-Host "OK    $m" -ForegroundColor Green }
function Fail($m) { $script:failed = $true; Write-Host "FAIL  $m" -ForegroundColor Red }

function Check($caseName, $result, $expectedKind, $expectedIndex, $expectedTied = @()) {
    $problems = @()
    if ($result.Kind -ne $expectedKind) { $problems += "Kind = '$($result.Kind)', expected '$expectedKind'" }
    if ($result.Index -ne $expectedIndex) { $problems += "Index = $($result.Index), expected $expectedIndex" }
    $tiedActual = @($result.Tied) -join ','
    $tiedExpected = @($expectedTied) -join ','
    if ($tiedActual -ne $tiedExpected) { $problems += "Tied = [$tiedActual], expected [$tiedExpected]" }
    if ($problems.Count -eq 0) {
        Ok "$caseName"
    } else {
        Fail "$caseName -- $($problems -join '; ')"
    }
}

# --- exact AutomationId wins over a substring match elsewhere in the set ---
$cands = @(
    @{ Name = 'Trigger Offset'; AutomationId = 'txtTriggerOffsetExtra' },  # substring hit only
    @{ Name = 'Trig. P.';       AutomationId = 'txtTrigger' }              # exact id hit
)
Check 'exact AutomationId' (Select-BestMatchIndex -Candidates $cands -Pattern 'txtTrigger') 'ExactId' 1

# --- exact Name wins when no AutomationId is exact ---
$cands = @(
    @{ Name = 'Trig. Padding'; AutomationId = 'txtTriggerPad' },  # substring hit only
    @{ Name = 'Trig. P.';      AutomationId = 'txtTriggerP' }     # exact name hit
)
Check 'exact Name' (Select-BestMatchIndex -Candidates $cands -Pattern 'Trig. P.') 'ExactName' 1

# --- substring-only: no exact hit at either tier, one substring match ---
$cands = @(
    @{ Name = 'Amount';  AutomationId = 'txtAmount' },
    @{ Name = 'Trig. O.'; AutomationId = 'txtTriggerOffset' }
)
Check 'substring-only' (Select-BestMatchIndex -Candidates $cands -Pattern 'TriggerOff') 'Substring' 1

# --- the real-world collision, txtTrigger / txtTriggerOffset, BOTH enumeration orders ---
# 'Trig. P.' is an exact Name hit; 'Trig. O.' is only a substring hit -> exact tier must win
# regardless of which element the loop reaches first.
$order1 = @(
    @{ Name = 'Trig. P.'; AutomationId = 'txtTrigger' },
    @{ Name = 'Trig. O.'; AutomationId = 'txtTriggerOffset' }
)
Check 'txtTrigger/txtTriggerOffset, txtTrigger first' (Select-BestMatchIndex -Candidates $order1 -Pattern 'Trig. P.') 'ExactName' 0

$order2 = @(
    @{ Name = 'Trig. O.'; AutomationId = 'txtTriggerOffset' },
    @{ Name = 'Trig. P.'; AutomationId = 'txtTrigger' }
)
Check 'txtTrigger/txtTriggerOffset, txtTriggerOffset first' (Select-BestMatchIndex -Candidates $order2 -Pattern 'Trig. P.') 'ExactName' 1

# --- no match at any tier ---
$cands = @(
    @{ Name = 'Amount'; AutomationId = 'txtAmount' },
    @{ Name = 'Trig. O.'; AutomationId = 'txtTriggerOffset' }
)
Check 'no match' (Select-BestMatchIndex -Candidates $cands -Pattern 'NoSuchControl') 'None' -1

# --- a tie: two candidates share the winning tier ---
$cands = @(
    @{ Name = 'Trig. P.'; AutomationId = 'txtTriggerA' },
    @{ Name = 'Trig. P.'; AutomationId = 'txtTriggerB' }
)
Check 'tie' (Select-BestMatchIndex -Candidates $cands -Pattern 'Trig. P.') 'Ambiguous' -1 @(0, 1)

Write-Host ""
if ($script:failed) {
    Write-Host 'SELECT-BEST-MATCH TESTS FAILED' -ForegroundColor Red
    exit 1
}
Write-Host 'SELECT-BEST-MATCH TESTS PASSED' -ForegroundColor Green
exit 0
