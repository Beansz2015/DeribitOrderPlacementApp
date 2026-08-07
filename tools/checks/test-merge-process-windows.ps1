# Direct exercise of Merge-ProcessWindows (docs/spec-harness-owned-window.md section 2.4/§Acceptance 6)
# a pure function over plain data, so this runs with no app, no UIA and no window.
#
# Run manually:  powershell -NoProfile -ExecutionPolicy Bypass -File tools/checks/test-merge-process-windows.ps1
#
# Exit codes: 0 = all cases passed · 1 = at least one case failed.

. "$PSScriptRoot\..\harness-common.ps1"

$script:failed = $false
function Ok($m)   { Write-Host "OK    $m" -ForegroundColor Green }
function Fail($m) { $script:failed = $true; Write-Host "FAIL  $m" -ForegroundColor Red }

function Check($caseName, $result, $expectedIds) {
    $actualIds = @($result | ForEach-Object { $_.Id }) -join ','
    $expected = @($expectedIds) -join ','
    if ($actualIds -eq $expected) {
        Ok "$caseName"
    } else {
        Fail "$caseName -- Ids = [$actualIds], expected [$expected]"
    }
}

# --- both sets empty ---
$result = Merge-ProcessWindows -RootItems @() -DescendantItems @()
Check 'both sets empty' $result @()

# --- set 2 empty: root items pass through unchanged, order preserved ---
$root = @(
    @{ Id = '1'; Label = 'Main Form' },
    @{ Id = '2'; Label = 'Other Root Window' }
)
$result = Merge-ProcessWindows -RootItems $root -DescendantItems @()
Check 'set 2 empty' $result @('1', '2')

# --- a window present in both sets: de-duped, kept once (the root-side occurrence) ---
$root = @(@{ Id = '1'; Label = 'Main Form' })
$desc = @(@{ Id = '1'; Label = 'Main Form' })
$result = Merge-ProcessWindows -RootItems $root -DescendantItems $desc
Check 'de-dup, present in both sets' $result @('1')

# --- two distinct windows: root-set item precedes descendant-set item ---
$root = @(@{ Id = '1'; Label = 'Main Form' })
$desc = @(@{ Id = '2'; Label = 'AutoTradeSettings' })
$result = Merge-ProcessWindows -RootItems $root -DescendantItems $desc
Check 'order: root before descendant' $result @('1', '2')

# --- three windows, duplicate in the middle: root=[1,2], descendant=[2,3] -> [1,2,3] ---
$root = @(
    @{ Id = '1'; Label = 'Main Form' },
    @{ Id = '2'; Label = 'AutoTradeSettings' }
)
$desc = @(
    @{ Id = '2'; Label = 'AutoTradeSettings' },
    @{ Id = '3'; Label = 'Popup' }
)
$result = Merge-ProcessWindows -RootItems $root -DescendantItems $desc
Check 'duplicate in the middle' $result @('1', '2', '3')

Write-Host ""
if ($script:failed) {
    Write-Host 'MERGE-PROCESS-WINDOWS TESTS FAILED' -ForegroundColor Red
    exit 1
}
Write-Host 'MERGE-PROCESS-WINDOWS TESTS PASSED' -ForegroundColor Green
exit 0
