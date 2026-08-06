# Implementation report — SB3: harness exact-match element selection

**Spec:** `docs/spec-harness-exact-match.md`.
**Commits:** `188bdab` (commit 1 — the pure seam `Select-BestMatchIndex` + the selector
`Select-MatchingElement` + `tools/checks/test-select-best-match.ps1`) · `3611d49` (commit 2 — the
five call sites) · this report (commit 3, plus the ROADMAP §5 row).
**Gate:** run after each commit and again before this report — **GATE PASSED, OrderCheck 268/268**,
unchanged throughout (a `tools/`-only change).
**Censuses:** unchanged — all nine hold. Not touched by this change (`frmMainPageV2.vb`-scoped;
nothing here touches app code) — **checked, not assumed**: re-ran `git diff --stat` against
`DeribitOrderPlacementApp/` and confirmed it is empty.
**No trades placed, no bridge armed, nothing pushed.**

## 1. Deviation from the spec's 4-commit plan

The spec's §Commits laid out four commits: (1) the pure seam, (2) the selector, (3) the five call
sites, (4) the impl report. **Commits 1 and 2 were combined here** — `Select-MatchingElement` has
no independent value without `Select-BestMatchIndex`, which it calls directly, and splitting them
would have left a commit with an uncalled function. So this shipped as three commits, not four:
seam+selector together, call sites, report. Noted in commit 1's message at the time; recorded here
per the spec's own instruction to say explicitly when something is not as specified rather than let
it look identical to compliance.

## 2. What changed

### `tools/harness-common.ps1`

- **`Test-ElementMatch` removed.** It was the first-match-wins substring predicate this spec
  replaces, and after commit 2 it had no remaining callers (grepped to confirm — only the five
  scripts that got converted ever called it).
- **`Select-BestMatchIndex -Candidates <array of @{Name=...;AutomationId=...}> -Pattern <string>`
  added** — pure function, no UIA object touched, per spec §2.1. Precedence: exact `AutomationId`
  (`OrdinalIgnoreCase`) → exact `Name` (`OrdinalIgnoreCase`) → substring on either (the old rule,
  kept as the fallback). Within the winning tier, one candidate wins outright; more than one
  returns `Kind = 'Ambiguous'` with every tied index in `Tied` — never picked arbitrarily, per §2.1's
  explicit instruction not to paper over a tie.
- **`Select-MatchingElement -Windows <...> -TypeName <...> -Pattern <...> [-LabelBuilder
  <scriptblock>]` added** — gathers every `$TypeName` descendant across all `$Windows` first, calls
  `Select-BestMatchIndex` once, and returns the chosen element, its owning window, the raw decision,
  and two label lists: `AllLabels` (the not-found diagnostic, built via `-LabelBuilder` so each call
  site's pre-existing quirk is preserved — see §3 on `click-button.ps1`) and `FullLabels` (one label
  per candidate, always populated, index-aligned with `Result.Tied` — used only to name every tied
  candidate on `Ambiguous`).

### The five call sites

`set-textbox.ps1`, `toggle-checkbox.ps1`, `select-combo-item.ps1`, `click-button.ps1`,
`click-PLACES-ORDER.ps1` all now: build the candidate set via `Select-MatchingElement` → branch on
`Result.Kind` (`Ambiguous` → exit 3, name every tied candidate; `None` → exit 2, the pre-existing
not-found diagnostic) → act on `.Element`. Everything downstream of the match (the blur discipline
in `set-textbox.ps1`, the ARM/TESTNET gates in `toggle-checkbox.ps1`/`select-combo-item.ps1`, the
deny check in `click-button.ps1`, the TESTNET/harness-PID gate in `click-PLACES-ORDER.ps1`) is
**unmodified code, only re-indented** — verified by reading the diffs, not just intent, since that
is exactly the kind of claim this project's memory says to re-verify rather than trust.

`click-button.ps1` and `click-PLACES-ORDER.ps1` pass a custom `-LabelBuilder` that returns `$null`
for a blank-`Name` button, reproducing their original `if ($name) { ... }` not-found-diagnostic
behaviour exactly (`set-textbox.ps1`/`toggle-checkbox.ps1`/`select-combo-item.ps1` always listed a
candidate even with a blank name, so they use the default builder).

Exit codes and stdout success-line shapes are unchanged (`place-and-verify.ps1` and the drive
scripts parse these — spec §3's explicit constraint).

### `tools/checks/test-select-best-match.ps1` (new)

Direct exercise of `Select-BestMatchIndex`, per acceptance 7: exact id, exact name, substring-only,
the `txtTrigger`/`txtTriggerOffset` pair in **both** enumeration orders, no match, and a tie. No
running app, no UIA, no window. `powershell -NoProfile -ExecutionPolicy Bypass -File
tools/checks/test-select-best-match.ps1` — exit 0, all seven cases OK.

## 3. A PS 5.1 quirk hit while wiring this in (not part of the spec, but real)

`Select-MatchingElement` originally called `Select-BestMatchIndex -Candidates @($candidates) ...`
where `$candidates` is a `System.Collections.Generic.List[object]`. On this machine's PS
`5.1.26100.8875`, **`@($list)` throws `System.ArgumentException: Argument types do not match`** —
reproduced in a fresh, non-interactive `powershell.exe` process with `Set-StrictMode -Off`, for a
`List[object]` of plain strings (not hashtable-specific, not session corruption from an earlier
`Add-Type`). Passing the `List` directly into an `[object[]]` parameter, or calling `.ToArray()`,
binds fine. Fixed at `harness-common.ps1` (the call now passes `$candidates` unwrapped) with a
comment recording the reproduction so the next `@($someList)` in this codebase doesn't cost someone
another debugging pass. Filed a note in `winforms-harness-quirks` memory (see below).

## 4. §Acceptance — what was run and what it showed

1. **Gate green.** Run after commit 1, after commit 2, and again before this report:
   **GATE PASSED, OrderCheck 268/268** every time. Censuses unchanged (§ above).
2. **Collision scope re-verified.** Enumerated all Edit AutomationIds via `inspect-tree.ps1` across
   both the main form and the (owned) AutoTradeSettings window: **28**, matching the spec's count.
   `txtTrigger` is a substring of `txtTriggerOffset` and nothing else in the set; no other
   collisions.
3. **🚨 The regression test.** With the harness-launched TESTNET app up:
   `set-textbox.ps1 txtTrigger 777` → `Set '' (id 'txtTrigger', ...) = '777'`. Read back via UIA
   `ValuePattern`: `txtTrigger = '777'`, `txtTriggerOffset = '30'` (unchanged from its pre-test
   value). Also confirmed visually — a full-form screenshot shows `Trig. P.: 777` and
   `Trig.O.: 30`. **Note on the pattern used:** both boxes have an empty UIA `Name` (their visible
   captions, "Trig. P." / "Trig. O.", belong to separate `Label` controls, not the `Edit`'s own
   `Name`/`AccessibleName`), so `"Trig. P."` as a literal pattern matches no textbox at all (exit 2,
   correctly) — the pattern that reproduces the original incident is `"txtTrigger"`, which is
   exactly what the spec's own §1 table names (`txtTrigger` substring of `txtTriggerOffset`).
4. **Ambiguity is loud.** `set-textbox.ps1 Trig 999` → exit 3, `REFUSED: 'Trig' matches more than
   one textbox at the same tier`, naming all three ties (`txtPlacedTrigStopPrice`,
   `txtTriggerOffset`, `txtTrigger` — a third collision this bare pattern surfaces that the spec's
   narrower `txtTrigger`/`txtTriggerOffset` framing didn't call out, since `"Trig"` is a looser
   pattern than the spec's own worked example). Read back: both boxes unchanged (`777` / `30`) —
   nothing was written on the refusal.
5. **No behaviour change elsewhere — each of the five scripts exercised once:**
   - `set-textbox.ps1 txtTrigger 777` (above).
   - `toggle-checkbox.ps1 ATRSlip` → `Off -> On`, then run again → `On -> Off` (restored).
   - `select-combo-item.ps1 cboBridgeMode "Log-only"` → selected; then `select-combo-item.ps1
     cboBridgeMode "Off"` → restored. **Caveat:** the AutoTradeSettings window is `Show(Me)`-owned
     by the main form, and in this run environment `Get-ProcessWindows` (unmodified,
     `harness-common.ps1`, pre-existing — not part of this spec's target list) intermittently did
     not enumerate it as a `RootElement` child, even while Win32 `IsWindowVisible` confirmed it was
     open (confirmed via a raw `EnumWindows` diagnostic and via `TreeScope.Descendants` on the main
     form, where the settings window IS found, nested under its owner). A retry immediately after
     opening the window succeeded. This is an environment/timing gap in a function outside this
     spec's scope, not a regression from this change — the same gap would equally have blocked the
     *old* `Test-ElementMatch` code from reaching that window. Flagging it rather than omitting it,
     per the spec's own instruction on "unchanged" vs "not checked."
   - `click-button.ps1 "Results"` → clicked, opened Trade History; closed via `close-popup.ps1`.
   - `click-PLACES-ORDER.ps1 "Cancel All Open"` → clicked (TESTNET verified first, per its own
     output), no open orders existed so nothing was cancelled — safe, no order placed.
6. **The deny check still fires.**
   - `click-button.ps1 "Limit BUY"` → `REFUSED: ... matches the trade-deny regex`, exit 3.
   - `click-button.ps1 "START"` → `REFUSED: ... matches trade-buttons.txt entry 'START'`, exit 3.
   - `click-PLACES-ORDER.ps1`'s non-TESTNET refusal was **not** live-triggered — doing so would
     require a real non-TESTNET session, which this project's standing rules do not let a harness
     run casually manufacture. Verified instead by diff: `git diff` on `click-PLACES-ORDER.ps1`
     shows the `$form = Get-MainForm -RequireTestnet -RequireHarnessPid` line is unchanged context,
     and `Get-MainForm`'s `-RequireTestnet` implementation in `harness-common.ps1` was not touched
     by either commit.
7. **`Select-BestMatchIndex` exercised directly** — `tools/checks/test-select-best-match.ps1`, all
   seven required cases (exact id, exact name, substring-only, both enumeration orders of the real
   collision, no match, a tie), all pass. Run with no app open.

## 5. §3 — what was NOT established

- **`select-combo-item.ps1`'s live exercise hit the `Get-ProcessWindows` gap above on the first two
  attempts** before succeeding on a retry. The underlying enumeration function was not touched and
  is out of this spec's target list (`tools/harness-common.ps1`'s `Get-ProcessWindows`, not the new
  seam/selector), so no fix was attempted here — flagged as a candidate for a future, separately
  scoped hygiene item (owned/`Show(Me)` windows sometimes missing from
  `RootElement.FindAll(TreeScope.Children, ...)` in this run environment).
- **`click-PLACES-ORDER.ps1`'s non-TESTNET refusal** was verified by code inspection (§Acceptance
  6), not by driving an actual non-TESTNET window — see that item for why.
- **The nested `ListItem` substring match inside `select-combo-item.ps1`** (choosing an item once
  the combo itself is resolved) is untouched and still substring-only. The spec's §1 table scopes
  the exact-first fix to the five *control*-selection call sites (Edit/CheckBox/ComboBox/Button ×2);
  it does not mention combo item selection, and there is no known collision among combo items today
  (`Off`/`Log-only`/`Live`), so this was left as-is rather than expanded beyond the spec's stated
  scope.

## 6. ROADMAP

`docs/ROADMAP-2026-08.md` §5's `Test-ElementMatch` row closed (✅, commits named) with §2.3's note
folded in verbatim (the `-Exact` switch was the wrong shape). The duplicate `Harness txtTrigger
tree-order nit` bullet further down §5 was struck as the same defect, folded into the closed row.

## 7. Memory

Filed the PS 5.1 `@($genericList)` quirk (§3 above) in `winforms-harness-quirks` memory so the next
session that reaches for `@($someList)` on a `System.Collections.Generic.List` doesn't rediscover it
from scratch.
