# Spec — the harness must find OWNED windows (`Get-ProcessWindows`)

**Origin:** `review-harness-exact-match.md` §5.2 and `impl-report-harness-exact-match.md` §4.5/§5,
both 2026-08-06. Filed on the backlog at `ROADMAP-2026-08.md` §5, third bullet.
**Status at writing:** open. Verified still present in code on 2026-08-07 at HEAD `64fd613`.
**Scope:** `tools/` only. **No `.vb` file is touched. No app behaviour changes.**
**Ships:** ON. This is a defect fix in test tooling, not a feature behind a knob.

**One-line statement of the job:** `tools/harness-common.ps1`'s `Get-ProcessWindows` sometimes fails
to return the `AutoTradeSettings` window, so any harness step that drives that window fails at
random. Make the lookup find owned windows deterministically, without widening what the harness is
allowed to touch.

---

## 🚫 Do-not-touch — read this before editing anything

These are safety machinery. They are **out of scope** and must be byte-identical when you are done.

| Thing | Where | Why it is untouchable |
|---|---|---|
| The trade-button deny check | `tools/click-button.ps1` | Refuses to click anything matching `tools/trade-buttons.txt`. It is the reason a harness cannot place an order by accident. |
| The TESTNET + harness-PID gate | `tools/click-PLACES-ORDER.ps1`, and `Get-MainForm -RequireTestnet -RequireHarnessPid` in `tools/harness-common.ps1:42-69` | A drive script must never touch the owner's live session. |
| `Select-BestMatchIndex` and `Select-MatchingElement` | `tools/harness-common.ps1:116-194` | Landed and approved 2026-08-06. Do not re-open the selection rules. |
| The PID filter itself | `tools/harness-common.ps1:90-92` | See §2.3. Losing it on either half of the fix is the one way this change can become dangerous. |

**Standing rules that apply to you** (`HANDOVER-6.md` §7):

- **A seat never places a trade and never arms the bridge.** The owner drives every trade, ARM and
  START.
- **If you find a defect in this spec, escalate to the owner BEFORE implementing.** Do not work
  around it. The spec gets amended; rulings fold back in.
- Finding-ID convention (`HANDOVER-6.md` §7bb): `E` = escalation raised before code · `D` = defect
  found in code · `SB` = finding against the docs. Number yours within this feature.

---

## §1 — The defect, precisely

### 1.1 The code as it stands

`tools/harness-common.ps1:87-93`:

```powershell
function Get-ProcessWindows {
    param([Parameter(Mandatory=$true)][int]$OwnerPid)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $OwnerPid)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
}
```

It asks the desktop for its **direct children** whose process id matches.

### 1.2 Why that misses the settings window

- The app opens its settings window with `Show(Me)` — `frmMainPageV2.vb:6090`. The window is
  **owned** by the main form.
- An owned window is **not reliably a direct child of the desktop root** in the UIA tree. It is
  reachable under `TreeScope::Descendants` of its owner.
- So `Get-ProcessWindows` returns a set that is missing a window that is genuinely open.

### 1.3 The evidence — this is observed, not theorised

From `impl-report-harness-exact-match.md` §4.5, during the SB3 acceptance run on 2026-08-06:

- `select-combo-item.ps1 cboBridgeMode` failed on **the first two attempts** and succeeded on a
  retry immediately after.
- Win32 `IsWindowVisible` reported the window open at the time it was not enumerated. This was
  confirmed with a raw `EnumWindows` diagnostic.
- The same window **was** found under `TreeScope::Descendants` of the main form, nested under its
  owner.

### 1.4 What it costs

- Every settings-window step is flaky: `set-textbox`, `toggle-checkbox` and `select-combo-item` all
  target controls that live there.
- A flaky step in the middle of a runtime acceptance produces a **failure that reads like a product
  defect**. That costs a re-run and, worse, invites a wrong diagnosis.
- **It is pre-existing and it is not an SB3 regression.** The old `Test-ElementMatch` code would have
  been blocked identically. Do not describe it as one.

### 1.5 What it is NOT

- It is **not** the SB3 defect. SB3 was wrong-control selection — the harness set `Trig. O.` when
  asked for `Trig. P.` and reported success. That is fixed and approved.
- This defect **fails loudly** (exit 2, target not found). Nothing silently acts on the wrong
  control. That is why it is a hygiene item and not an emergency.

---

## §2 — The fix

### 2.1 The rule

`Get-ProcessWindows` must return the **union** of:

1. **Root children** with the matching process id — exactly what it returns today, unchanged; **and**
2. **`Window`-type descendants of each of those root children**, also filtered to the matching
   process id.

The main form is never owned, so it is always in set 1. Its owned windows are then reached by
descending from it in set 2.

### 2.2 Order and de-duplication are load-bearing

- **Order:** every window from set 1 must come before any window from set 2, and the relative order
  inside each set must be preserved.
  **Why:** `Select-BestMatchIndex` (`tools/harness-common.ps1:116-151`) still has a **substring
  fallback tier**. Within a tier it takes the single match, or fails loudly on a tie. Changing the
  order changes which control is reached when a pattern matches one control on the main form and a
  different control on the settings window. Main-form-first preserves today's behaviour.
- **De-duplication:** a window that appears in both sets must appear **once**.
  **Why:** a duplicate turns a clean single match into a false `Ambiguous` tie, and the SB3 rules
  then refuse the whole operation loudly. The fix would break the very scripts it is meant to
  stabilise.
  **Use the existing helper:** `Test-SameElement` (`tools/harness-common.ps1:199-206`) compares
  `GetRuntimeId()`. **Do not compare AutomationElement objects or their `Name` values** — the file
  already records that element instances are not reference-equal across separate queries.

### 2.3 The PID filter must be applied to BOTH halves

Set 2 must carry the same `ProcessIdProperty` condition as set 1.

**Why this is the safety-relevant line in the whole change:** the harness safety model is *drive
scripts only ever touch windows belonging to the harness-launched PID*. A `Window` descendant
collected without that condition could belong to another process — an embedded or hosted window —
and a drive script would then act outside the harness session. Keep the condition on both halves.

### 2.4 Extract a pure seam for the merge — the house pattern

The UIA query cannot be tested without a running app. **The merge rule can.**

- Put the order-and-de-duplicate logic in its own function that takes **plain data**, not UIA
  objects — for example a list of `@{ Id = <runtime-id-as-string>; Label = <string> }`.
- `Get-ProcessWindows` then does the two UIA queries and calls that function.
- This is exactly what SB3 did with `Select-BestMatchIndex`, and it is why that fix could be proven
  with no app open.
- Add its cases to a check script beside the existing one:
  `tools/checks/test-select-best-match.ps1` is the pattern to copy — same shape, same exit codes
  (`0` = all passed, `1` = at least one failed).

### 2.5 Why not the alternatives

| Alternative | Why not |
|---|---|
| **Retry loop** — call the existing query again after a short wait | It is a timing patch. The impl report shows a retry often works, but the window is genuinely absent from the root-children set, so a retry can fail again. It also adds latency to every call. **Do not ship this as the fix.** It is acceptable only as an extra safety net *on top of* §2.1, and only if you state it as such. |
| **`TreeScope::Descendants` on `RootElement`** | That walks every control of every application on the desktop. It is enormous, slow, and would make the harness depend on unrelated windows. Reject. |
| **Win32 `EnumWindows` via P/Invoke, then `AutomationElement.FromHandle`** | It would work — the diagnostic in §1.3 used it. But it adds a second window-discovery mechanism in a file that already has one, and it needs new `Add-Type` P/Invoke. Hold it in reserve: use it only if §2.1 fails acceptance 1, and escalate before you do. |

### 2.6 PowerShell traps in this file — read before writing code

- **`Set-StrictMode -Version 2` is on** (`tools/harness-common.ps1:16`). Unassigned variables and
  missing properties throw.
- 🚨 **Do not wrap a `System.Collections.Generic.List` in `@()`.** On this PS 5.1 build that throws
  `Argument types do not match`. The file documents it at `tools/harness-common.ps1:185-188`, and
  the review narrowed it to **`List[object]`** specifically. Pass the `List` straight into an
  `[object[]]` parameter, or call `.ToArray()`.
- **Comma-wrap a returned collection** (`return ,$list`). Without it, PowerShell unrolls a 0- or
  1-element result to `$null` or a bare item and `.Count` breaks silently. The file records this at
  `tools/harness-common.ps1:138-141`.
- The current function returns an `AutomationElementCollection`. Returning a `List[object]` instead
  is fine — see §3.

---

## §3 — Call sites: seven, and none of them should need to change

`Get-ProcessWindows` has seven callers:

`click-button.ps1:43` · `click-PLACES-ORDER.ps1:39` · `close-popup.ps1:20` · `inspect-tree.ps1:21` ·
`select-combo-item.ps1:33` · `set-textbox.ps1:31` · `toggle-checkbox.ps1:26`

**Every one of them consumes the result only by `foreach`, or by passing it to
`Select-MatchingElement`, which also only does `foreach`.** No caller reads `.Count`. No caller wraps
it in `@()`. This was checked at HEAD `64fd613`.

**Therefore: keep the signature `-OwnerPid <int>` and change no call site.** That is the goal, not a
convenience — a fix that requires seven callers to remember something new is the shape this house
already rejected once, in `spec-harness-exact-match.md` §2.3 (the `-Exact` switch).

If you find you must change the signature, **stop and escalate** — that is a spec defect, not an
implementation detail.

---

## §Acceptance

**Test the defect, not the theory of the fix** (`HANDOVER-6.md` §7, lesson 2). And **prove every
conjunct or claim nothing** (lesson 4).

Preconditions: launch the app through `tools/launch-app.ps1` so `verify/app.pid` is written. Confirm
the window title carries `— TESTNET`. No trade is placed at any point in this acceptance.

**1 — The defect is fixed, and you can show WHICH half found the window.**
- Add a temporary diagnostic that prints, for each returned window, whether it came from the
  root-children half or the descendants half.
- Open the settings window. Run a settings-window drive step **ten times**, closing and reopening the
  settings window between runs.
- **Required: 10/10 succeed on the first attempt, with no retry.**
- 🚨 **Also required: report how many of those ten found the settings window via the DESCENDANTS
  half.** If the answer is zero, the environment did not reproduce the fault that day, and **you must
  say the fix is unproven rather than say it passed.** A green run that never exercised the new path
  proves nothing.

**2 — No duplicates.** With the settings window open, print the returned set. Assert each window
appears exactly once, by `GetRuntimeId()`.

**3 — Order is main-form-first.** With the settings window open, assert the main form precedes the
settings window in the returned set.

**4 — The five drive scripts are unchanged in behaviour.** Exercise each once and record the output:
`set-textbox.ps1`, `toggle-checkbox.ps1`, `select-combo-item.ps1`, `click-button.ps1`,
`click-PLACES-ORDER.ps1`. Restore any value you change, and read it back to prove the restore.
- ⚠ `tools/set-textbox.ps1` on the settings window: check `HANDOVER-6.md` §5.4 first. The main form's
  `FormClosing` persists all 11 standing input fields unconditionally. **Do not leave a harness value
  in a field that persists.**

**5 — The deny check and the TESTNET gate still refuse.**
- `click-button.ps1 "Limit BUY"` → refused, exit 3.
- Verify the `click-PLACES-ORDER.ps1` TESTNET refusal **by diff, not by manufacturing a live
  session** — the same way `impl-report-harness-exact-match.md` §Acceptance 6 did it.

**6 — The pure merge function passes with no app open.** Run your new check script. Include at
minimum: both sets empty · set 2 empty · a window present in both sets (de-dup) · two distinct
windows (order) · three windows where the duplicate is in the middle.

**7 — Nothing else regressed.**
- `tools/checks/test-select-best-match.ps1` → all cases pass.
- `tools/checks/verify-gate.ps1` → `GATE PASSED`, OrderCheck **268/268**.
- The nine `frmMainPageV2.vb` censuses → **68 occurrences across 64 lines**. They must not move: you
  are not touching that file. Run them anyway — prose has moved them before
  (`HANDOVER-6.md` §7, lesson 5). ⚠ They are **occurrence** counts. A count-mode grep returns 64 and
  reads as four missing.

**8 — Remove the temporary diagnostic from acceptance 1** before the final commit, or keep it behind
an explicit `-Diagnostic` switch that defaults off. Say which you did.

---

## §Commits

Small and reviewable, in this order:

1. The pure merge function + its check script. No behaviour change yet.
2. `Get-ProcessWindows` rewired to the union rule, using that function.
3. The impl report.

Docs are tracked and committed with the work. **The owner is the only pusher** — do not push.

**The impl report must include:**
- The acceptance results, including the descendants-half count from acceptance 1.
- A **numbered §** answering the question in §Model and effort below.
- An explicit "what was NOT established" section. If something was checked by reading rather than by
  running, say so in those words.

---

## §Model and effort (required by `HANDOVER-6.md` §7b)

> **Recommendation: Sonnet, medium effort, fresh conversation.**

**Why, tied to what the work touches:**
- The change is confined to `tools/`. **No `.vb` file is touched**, so the order, stop-loss, receive,
  bridge and act paths — the live trading surface — are not in scope at all. That is what
  `HANDOVER-6.md` §7a reserves Opus at high effort for.
- The logic is a set union with an ordering rule. It is small and it is fully specified above.
- SB3 was the same class of work — `tools/`-only, one pure seam, five call sites — and Sonnet at
  medium was correct for it.

**Where the thinking should go — the three places a competent implementer working quickly will get
this wrong:**
1. **The PID filter on the second half (§2.3).** It is easy to collect `Window` descendants and
   forget the condition. That is the only way this change can widen what a drive script can touch.
2. **Order and de-duplication (§2.2).** Both are invisible in a quick test with one window open, and
   both change which control the substring tier resolves to when two windows are open.
3. **The PS 5.1 collection traps (§2.6).** The `@()`-around-a-`List` throw and the missing comma-wrap
   both produce failures that look like logic bugs and are not.

**What would change this answer — escalate rather than proceed:**
- If §2.1's union does **not** fix the fault in the run environment, and the fix needs Win32
  P/Invoke (§2.5) or any change inside the app, **stop and escalate to the owner.** A change that
  reaches into the app moves this to **Opus, high effort**.
- If the signature must change and the seven call sites must be edited (§3), that is a spec defect.
  Escalate; do not absorb it.

**Answer this in the impl report, either way:** *was Sonnet at medium the right tier?* Name the
places where more reasoning depth would have helped, or say plainly that it was not needed. That is
how the tiering table in `HANDOVER-6.md` §7a earns corrections instead of drifting.
