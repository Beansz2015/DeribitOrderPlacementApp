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

param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$NamePattern
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
            $invoke.Invoke()
            Write-Host "Clicked (TESTNET): '$name' (window '$($w.Current.Name)')"
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
