# Read-only timing probe (docs/spec-harness-owned-window.md section Ruling, R3) — discriminates
# between the two competing causes of the original owned-window defect (D1's ruling):
#   Query A - is the settings window a ROOT CHILD of the desktop for this PID
#             (RootElement.FindAll(Children, PID)) - the "topology" cause, section 1.2?
#   Query B - does Find-ByControlType(mainForm, ComboBox) reach cboBridgeMode - the
#             "render/tree-caching latency" cause raised in the spec-back section 5?
# Times each independently, from the moment the settings window is opened, over many rounds.
#
# READ-ONLY: no control inside the settings window is set, toggled or selected. The ONLY
# control driven is the main form's own "Auto Settings" toggle button - InvokePattern only,
# used solely to open/close the settings window between rounds, exactly as R3 steps 3 and 5
# require. No value-bearing control is touched, nothing persists, no trade, no .vb change.
# The timed poll loop itself does not foreground, click or focus anything (R3 watch-item 1) -
# it only calls FindAll/GetCurrentPattern-read, so it cannot perturb what it is measuring.
#
# STATE VERIFICATION (added after a first run desynced at round 9): the toggle button flips
# Visible based on the app's OWN idea of its current state, not the caller's. A single missed
# Invoke() silently inverts the open/close parity for every subsequent round, which looks
# exactly like a UIA finding but is not one. Ground truth is checked via raw Win32
# EnumWindows/IsWindowVisible (independent of the two UIA queries under test) before and after
# every toggle, OUTSIDE the timed measurement window, so it cannot perturb queries A/B.
#
# Usage:
#   powershell -NoProfile -File tools/probe-owned-window-timing.ps1
#   powershell -NoProfile -File tools/probe-owned-window-timing.ps1 -Rounds 20 -BudgetMs 2000 -IntervalMs 75
#
# Exit codes: 0 = probe completed (see report for the outcome - this script does not judge
#             pass/fail, R3's outcome table does) · 1 = app not running / not harness-launched /
#             could not establish a reliable open/close baseline.

param(
    [int]$Rounds = 20,
    [int]$BudgetMs = 2000,
    [int]$IntervalMs = 75
)

. "$PSScriptRoot\harness-common.ps1"

if (-not ("OwnedWindowProbeWin32" -as [type])) {
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class OwnedWindowProbeWin32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint procId);
    // Returns 1 if a visible top-level window with this exact title exists for pid, 0 if it
    // exists but hidden, -1 if no window with this title exists at all for pid.
    public static int VisibleState(uint pid, string title) {
        int state = -1;
        EnumWindows((hWnd, lParam) => {
            uint wPid;
            GetWindowThreadProcessId(hWnd, out wPid);
            if (wPid == pid) {
                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                if (sb.ToString() == title) {
                    state = IsWindowVisible(hWnd) ? 1 : 0;
                    return false; // found it, stop enumerating
                }
            }
            return true;
        }, IntPtr.Zero);
        return state;
    }
}
"@
}

$form = Get-MainForm -RequireTestnet -RequireHarnessPid
Write-Host "Main form: '$($form.Current.Name)' (PID $($form.Current.ProcessId))"
$hpid = [uint32]$form.Current.ProcessId

# Locate the "Auto Settings" toggle button ONCE. Pre-fix Get-ProcessWindows (Children-only,
# post-revert) is used deliberately here - this probe measures the ORIGINAL two queries, not
# any withdrawn union.
$windows0 = Get-ProcessWindows -OwnerPid $form.Current.ProcessId
$mOpen = Select-MatchingElement -Windows $windows0 -TypeName Button -Pattern "Auto Settings"
if ($mOpen.Result.Kind -eq 'None' -or $mOpen.Result.Kind -eq 'Ambiguous') {
    Write-Error "Could not uniquely locate the 'Auto Settings' toggle button (Kind=$($mOpen.Result.Kind)). Aborting - the probe cannot proceed without it."
    exit 1
}
$toggle = $mOpen.Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)

function Get-GroundTruthVisible {
    return ([OwnedWindowProbeWin32]::VisibleState($hpid, 'AutoTradeSettings') -eq 1)
}

# Establish a known-closed baseline before round 1 (ground truth, not assumed).
$settleDeadline = (Get-Date).AddSeconds(5)
if (Get-GroundTruthVisible) {
    Write-Host "Baseline: settings window is open - closing it before round 1."
    $toggle.Invoke()
}
while ((Get-Date) -lt $settleDeadline -and (Get-GroundTruthVisible)) { Start-Sleep -Milliseconds 100 }
if (Get-GroundTruthVisible) {
    Write-Error "Could not establish a closed baseline (window still open per Win32 after 5s). Aborting rather than run on an unverified baseline."
    exit 1
}
Write-Host "Baseline confirmed closed (Win32 ground truth)."

$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $form.Current.ProcessId)
$rootElement = [System.Windows.Automation.AutomationElement]::RootElement

function Test-QueryA {
    foreach ($c in $rootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) {
        if ($c.Current.Name -eq 'AutoTradeSettings') { return $true }
    }
    return $false
}

function Test-QueryB {
    foreach ($cb in (Find-ByControlType -Element $form -TypeName ComboBox)) {
        if ($cb.Current.AutomationId -eq 'cboBridgeMode' -or $cb.Current.Name -eq 'cboBridgeMode') { return $true }
    }
    return $false
}

$results = New-Object System.Collections.Generic.List[object]
$desyncCorrections = 0

for ($round = 1; $round -le $Rounds; $round++) {
    # Pre-check (ground truth, OUTSIDE the timed window): must be closed here.
    if (Get-GroundTruthVisible) {
        Write-Host "Round $round : WARNING - window already open before this round's toggle (desync). Correcting."
        $toggle.Invoke()
        Start-Sleep -Milliseconds 400
        $desyncCorrections++
        if (Get-GroundTruthVisible) {
            Write-Error "Round $round : could not recover a closed baseline. Aborting."
            exit 1
        }
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $toggle.Invoke()   # open

    $aFirst = $null
    $bFirst = $null
    while ($sw.ElapsedMilliseconds -le $BudgetMs -and (($null -eq $aFirst) -or ($null -eq $bFirst))) {
        $elapsed = $sw.ElapsedMilliseconds
        if ($null -eq $aFirst -and (Test-QueryA)) { $aFirst = $elapsed }
        if ($null -eq $bFirst -and (Test-QueryB)) { $bFirst = $elapsed }
        if (($null -eq $aFirst) -or ($null -eq $bFirst)) { Start-Sleep -Milliseconds $IntervalMs }
    }
    $sw.Stop()

    # Post-open ground truth, taken AFTER the timed window closes (does not affect A/B timing).
    $openedForReal = Get-GroundTruthVisible

    $row = [PSCustomObject]@{
        Round        = $round
        AFirstMs     = if ($null -eq $aFirst) { "never" } else { $aFirst }
        BFirstMs     = if ($null -eq $bFirst) { "never" } else { $bFirst }
        Win32Visible = $openedForReal
    }
    $results.Add($row)
    Write-Host ("Round {0,2}: A={1,-8} B={2,-8} Win32Visible={3}" -f $row.Round, $row.AFirstMs, $row.BFirstMs, $row.Win32Visible)

    Start-Sleep -Milliseconds 300
    $toggle.Invoke()   # close
    Start-Sleep -Milliseconds 300   # settle before the next round's pre-check
}

Write-Host ""
Write-Host "=== SUMMARY ($Rounds rounds, budget ${BudgetMs}ms, interval ${IntervalMs}ms) ==="
$results | Format-Table -AutoSize | Out-String | Write-Host
$aNever = @($results | Where-Object { $_.AFirstMs -eq 'never' }).Count
$bNever = @($results | Where-Object { $_.BFirstMs -eq 'never' }).Count
$falseOpen = @($results | Where-Object { -not $_.Win32Visible }).Count
Write-Host "Query A (root-children) never-succeeded: $aNever/$Rounds"
Write-Host "Query B (main-form descendants) never-succeeded: $bNever/$Rounds"
Write-Host "Rounds where Win32 ground truth says the window was NOT actually open at the end of the timed window: $falseOpen/$Rounds"
Write-Host "Desync corrections needed (toggle silently didn't register): $desyncCorrections"
exit 0
