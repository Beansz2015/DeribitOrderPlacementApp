# Shared plumbing for the DeribitOrderPlacementApp UI-automation harness
# (docs/spec-ui-test-harness.md section 4; ported from DeribitVerdictEngine's tools/*.ps1).
# Dot-source from the sibling scripts:  . "$PSScriptRoot\harness-common.ps1"
#
# Conventions carried here (every script):
#   - The window is located by the FROZEN title prefix "Deribit Order Placement App"
#     (load-bearing, set by frmMainPageV2 at Load; the environment suffix — TESTNET / — LIVE
#     is what the script safety tier gates on).
#   - Exit codes: 0 = ok, 1 = app not found, 2 = target not found (all candidates printed
#     first — the self-diagnostic), 3 = refused or action failed.
#   - DRIVE scripts (anything that clicks/types/toggles/closes) operate ONLY on the
#     harness-launched PID's windows (verify/app.pid, written by launch-app.ps1) — never
#     the owner's session. Observation scripts (find/inspect/read/screenshot) may look at
#     any matching window.

Set-StrictMode -Version 2
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$script:TitlePrefix = "Deribit Order Placement App"
$script:RepoRoot    = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$script:PidFile     = Join-Path $script:RepoRoot "verify\app.pid"
$script:OutDir      = Join-Path $script:RepoRoot "verify\out"

# Returns the AutomationElement of the main form, or $null. Match = title substring on the
# frozen prefix (case-insensitive), root children only.
function Find-MainFormElement {
    $root  = [System.Windows.Automation.AutomationElement]::RootElement
    $forms = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
                           [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($f in $forms) {
        $n = $f.Current.Name
        if ($n -and $n.IndexOf($script:TitlePrefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $f
        }
    }
    return $null
}

# Main form or exit 1. -RequireTestnet: exit 3 unless the title carries TESTNET (kill rule:
# cannot confirm the environment => do nothing). -RequireHarnessPid: exit 3 unless the window
# belongs to the PID in verify/app.pid (drive scripts must never drive the owner's session).
function Get-MainForm {
    param(
        [switch]$RequireTestnet,
        [switch]$RequireHarnessPid
    )
    $form = Find-MainFormElement
    if ($null -eq $form) {
        Write-Error "Main form not found (title prefix '$script:TitlePrefix') — app not running?"
        exit 1
    }
    $title = $form.Current.Name
    if ($RequireTestnet -and $title.IndexOf("TESTNET", [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        Write-Error "REFUSED: window title is '$title' — TESTNET required and not confirmed."
        exit 3
    }
    if ($RequireHarnessPid) {
        $harnessPid = Get-HarnessPid
        if ($null -eq $harnessPid) {
            Write-Error "REFUSED: no harness PID file ($script:PidFile). Drive scripts only operate on a session launched by launch-app.ps1 — never a pre-existing (owner) session."
            exit 3
        }
        if ($form.Current.ProcessId -ne $harnessPid) {
            Write-Error "REFUSED: window PID $($form.Current.ProcessId) is not the harness-launched PID $harnessPid. Not driving a session the harness did not launch."
            exit 3
        }
    }
    return $form
}

# The PID launch-app.ps1 recorded, or $null (missing file / stale PID whose process is gone).
function Get-HarnessPid {
    if (-not (Test-Path $script:PidFile)) { return $null }
    $raw = (Get-Content $script:PidFile -TotalCount 1).Trim()
    $procId = 0
    if (-not [int]::TryParse($raw, [ref]$procId)) { return $null }
    try {
        $null = Get-Process -Id $procId -ErrorAction Stop
        return $procId
    } catch {
        return $null
    }
}

# All top-level windows belonging to a process (the settings window "AutoTradeSettings" is a
# separate top-level window of the same PID — drive scripts search every window they own).
function Get-ProcessWindows {
    param([Parameter(Mandatory=$true)][int]$OwnerPid)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $OwnerPid)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
}

# Descendants of $Element with the given control type ("Button", "Edit", "CheckBox", ...).
function Find-ByControlType {
    param(
        [Parameter(Mandatory=$true)]$Element,
        [Parameter(Mandatory=$true)][string]$TypeName
    )
    $ct = [System.Windows.Automation.ControlType]::$TypeName
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    return $Element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

# Case-insensitive substring on UIA Name OR AutomationId (WinForms maps control Text -> Name
# and, for the harness's targets, AccessibleName -> Name; AutomationId is the designer name).
function Test-ElementMatch {
    param($Element, [string]$Pattern)
    foreach ($v in @($Element.Current.Name, $Element.Current.AutomationId)) {
        if ($v -and $v.IndexOf($Pattern, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    }
    return $false
}

# True when two AutomationElements are the SAME control. RuntimeId is the reliable identity -
# AutomationElement instances are not reference-equal across separate queries, so comparing the
# objects (or their Names) gives wrong answers.
function Test-SameElement {
    param($A, $B)
    if ($null -eq $A -or $null -eq $B) { return $false }
    try { $ra = $A.GetRuntimeId(); $rb = $B.GetRuntimeId() } catch { return $false }
    if ($null -eq $ra -or $null -eq $rb -or $ra.Length -ne $rb.Length) { return $false }
    for ($i = 0; $i -lt $ra.Length; $i++) { if ($ra[$i] -ne $rb[$i]) { return $false } }
    return $true
}

# Picks a control in $Window to hand keyboard focus to when blurring $Exclude.
#
# WHY THIS EXISTS: focusing the WINDOW does not blur a child control. A WinForms Form routes
# activation straight back to its last-active child, so "focus the box, then focus the form"
# leaves focus exactly where it started and the Leave event never fires - which is precisely how
# set-textbox used to report a successful commit that never happened (2026-07-21 testnet pass).
# A real blur has to land on a DIFFERENT focusable control.
#
# Edits only, deliberately: focusing a control never activates it (no click, no invoke), but
# restricting the sink to text boxes keeps focus away from the deny-listed trade buttons entirely
# rather than relying on that argument. Every window in this app that has a target Edit has
# others; if none is found the caller must fail loudly rather than fall back to the broken
# focus-the-window behaviour.
function Get-FocusSink {
    param($Window, $Exclude)
    foreach ($c in (Find-ByControlType -Element $Window -TypeName Edit)) {
        if (Test-SameElement -A $c -B $Exclude) { continue }
        if (-not $c.Current.IsKeyboardFocusable) { continue }
        if ($c.Current.IsOffscreen) { continue }
        return $c
    }
    return $null
}

# Win11 blocks SetForegroundWindow under foreground-steal restrictions; attaching to the current
# foreground's input queue lifts the lockout so focus genuinely transfers and SendKeys reaches
# the app instead of the calling terminal. Copied verbatim from the engine harness.
function Set-AppForeground {
    param([Parameter(Mandatory=$true)][IntPtr]$Hwnd)
    if (-not ("WFG" -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WFG {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
}
"@
    }
    $fgHwnd    = [WFG]::GetForegroundWindow()
    $fgThread  = [WFG]::GetWindowThreadProcessId($fgHwnd, [IntPtr]::Zero)
    $curThread = [WFG]::GetCurrentThreadId()
    [WFG]::AttachThreadInput($curThread, $fgThread, $true)  | Out-Null
    [WFG]::ShowWindow($Hwnd, 9) | Out-Null    # SW_RESTORE — undo any minimize
    [WFG]::BringWindowToTop($Hwnd) | Out-Null
    [WFG]::SetForegroundWindow($Hwnd) | Out-Null
    [WFG]::AttachThreadInput($curThread, $fgThread, $false) | Out-Null
}
