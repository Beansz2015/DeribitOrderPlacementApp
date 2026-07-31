# ⚠ THE ONLY SCRIPT WITHOUT THE TRADE-DENY RULE (docs/spec-ui-test-harness.md sections 4 + 6).
# Clicks ANY button — including BUY / SELL / Cancel All / bridge START — and therefore
# HARD-REQUIRES a TESTNET-titled, harness-launched window. The environment title suffix is
# written by the app itself from AppSecrets, so it cannot lie about which world this is.
# Used only with explicit intent; never from a generic drive loop.
#
# Usage:
#   powershell -NoProfile -File tools/click-PLACES-ORDER.ps1 "Limit BUY"
#   powershell -NoProfile -File tools/click-PLACES-ORDER.ps1 "Cancel All Open"
#
# Exit codes: 0 = clicked · 1 = app not running · 2 = no button matched (all names printed) ·
#             3 = refused (title not TESTNET / not the harness-launched PID / not invokable).

#
# -Actuations N (default 1) issues N Invokes against the SAME resolved element, back to back.
# Added 2026-08-01 as the acceptance instrument for docs/spec-placement-single-flight.md §4.2:
# the defect it guards against is only observable by actuating a button several times inside one
# await window, which three separate process launches (seconds apart) cannot do.
# It lives HERE, on the already-privileged script, rather than in a new one, precisely so the
# section 6.1 invariant is untouched: there is still exactly ONE script that can Invoke a
# trade-affecting button. A second placing script would have been a safety-model amendment.
# Capped at 5 and it prints a loud banner for N > 1 - this is a deliberate multi-order action.

param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$NamePattern,
    [ValidateRange(1,5)]
    [int]$Actuations = 1
)

. "$PSScriptRoot\harness-common.ps1"

# Kill rule: TESTNET title AND harness-launched PID, verified before anything is touched.
$form = Get-MainForm -RequireTestnet -RequireHarnessPid

# Environment self-documentation: print the verified title BEFORE acting (spec section 4 table).
Write-Host "Environment verified: '$($form.Current.Name)' (PID $($form.Current.ProcessId))"

$windows = Get-ProcessWindows -OwnerPid $form.Current.ProcessId
$allNames = New-Object System.Collections.Generic.List[string]
foreach ($w in $windows) {
    foreach ($b in (Find-ByControlType -Element $w -TypeName Button)) {
        $name = $b.Current.Name
        if ($name) { $allNames.Add("'$name' (id '$($b.Current.AutomationId)', window '$($w.Current.Name)')") }
        if (-not (Test-ElementMatch -Element $b -Pattern $NamePattern)) { continue }
        try {
            $invoke = $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            if ($Actuations -gt 1) {
                Write-Host "*** BURST: $Actuations actuations of '$name' - this is a DELIBERATE multi-order action ***"
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                for ($i = 1; $i -le $Actuations; $i++) {
                    $invoke.Invoke()
                    Write-Host ("  invoke {0} at t+{1} ms; IsEnabled now = {2}" -f $i, $sw.ElapsedMilliseconds, $b.Current.IsEnabled)
                }
                $sw.Stop()
                Write-Host ("Burst issued (TESTNET): {0} invokes of '{1}' in {2} ms" -f $Actuations, $name, $sw.ElapsedMilliseconds)
            } else {
                $invoke.Invoke()
                Write-Host "Clicked (TESTNET): '$name' (window '$($w.Current.Name)')"
            }
            exit 0
        } catch {
            Write-Error "Button '$name' is not invokable: $_"
            exit 3
        }
    }
}

Write-Error "No button matched '$NamePattern'. Available buttons:"
foreach ($n in $allNames) { Write-Host "  - $n" }
exit 2
