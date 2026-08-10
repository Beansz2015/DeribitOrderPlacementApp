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

# 🚨 AMENDED 2026-08-07 — READ THIS BEFORE §1 AND §2

**§2's union rule is WITHDRAWN. Do not implement it. It was implemented, it was correct to the
letter of this spec, and it made things worse.**

- Raised by the implementer seat as **D1** (a defect found in code):
  `spec-back-harness-owned-window-duplicate-controls.md`, 2026-08-07.
- **Owner ruling, same day: D1 UPHELD. The defect is in this spec, not in their code.**
- Commit `2c5e986` (the union rewire) was **reverted** by `59de8c1`. Commit `1f4b931` (the
  `Merge-ProcessWindows` pure seam and its check script) **stays** — it is correct and tested, and it
  is currently **unused** pending the probe in §Ruling below.
- The full ruling, the reasoning, and **what to do instead** are in **§Ruling** at the end of this
  document. **Go there next.** §1 and §2 are kept for the record, with corrections marked in place.

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

### 1.2 Why that misses the settings window — ⚠ NOT ESTABLISHED, see §Ruling

> ⚠ **Correction 2026-08-07.** This subsection states a *topology* cause as fact. It is a
> hypothesis, and a competing one now fits the evidence better: **UIA render/tree-caching latency
> immediately after `Show()`**. The implementer observed the main form's descendants search reaching
> the settings window's controls reliably after about 800 ms. If the window had been reachable from
> the main form at the original failure moment, the old code would have found the control — it did
> not, so at that moment it was in **neither** place. **The cause is undecided. §Ruling's probe
> decides it.** Everything below is retained as written.

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

## §2 — The fix — 🚫 WITHDRAWN 2026-08-07, KEPT FOR THE RECORD

> 🚫 **Do not implement §2.1–§2.4.** They were implemented exactly as written and the result was a
> deterministic regression. The one-line reason, proved by entailment from the code: **half 2 can
> only return windows that are already inside a half-1 window's subtree, so it can never contribute
> a control that half 1 could not already reach — it can only duplicate them.** §Ruling has the
> full argument. §2.5's table of rejected alternatives is **partly wrong** and is corrected in
> place below.

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
| **Retry loop** — call the existing query again after a short wait | ⚠ **THIS REJECTION WAS WRONG, corrected 2026-08-07.** Retired reasoning, quoted: ~~*"It is a timing patch… the window is genuinely absent from the root-children set, so a retry can fail again."*~~ **That reasoning assumed §1.2's topology cause, which is not established.** If the cause is render latency, then waiting is not a patch — it is the fix. **Distinguish two different things:** a *blind retry* (run it again and hope) stays rejected; a **bounded wait-for-condition** (poll until the target is present, with a deadline and a loud timeout) is a legitimate candidate and is now a live option in §Ruling. |
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

---

# §Ruling — 2026-08-07, owner-approved. This supersedes §2.

## R1 — D1 is UPHELD. The defect is in this spec.

The implementer's `spec-back-harness-owned-window-duplicate-controls.md` is correct. Their
implementation was verified independently at the coordinator seat and is correct to the letter of
§2: process-id filter on both halves via `AndCondition`, `RuntimeId` de-duplication, root-first
order, a genuine pure seam, `-Diagnostic` off by default, do-not-touch list respected, zero `.vb`
files touched.

**The finding is stronger than the spec-back states.** Traced through the code:

1. §2.1's half 2 collects `Window`-type descendants **of half-1 windows**.
2. So every window half 2 returns is, by construction, already inside a half-1 window's subtree.
3. `Find-ByControlType` (`tools/harness-common.ps1:96-105`) searches `TreeScope::Descendants` of
   **every** returned window.
4. **Therefore half 2 can never contribute a control that half 1 could not already reach. It can
   only duplicate them.**

So for the five control-searching call sites the union is not "a fix with a side effect". It is a
**pure regression**: no new reachable control, and every settings-window control doubled, which
`Select-BestMatchIndex` correctly reports as `Ambiguous` and refuses.

**And it does not fix the original defect under either reading of the cause.** If the settings
window is genuinely absent from the main form's subtree at failure time, half 2 finds nothing
either.

## R2 — Options ruled

| Option from the spec-back | Ruling |
|---|---|
| **(a)** bound `Find-ByControlType` with a `TreeWalker` | **REJECTED for now.** It adds a manual tree walk to restore correct window attribution for a union that contributes no controls. It treats a problem this spec created. Reconsider only if the probe proves a union is needed. |
| **(b)** de-dup inside `Select-MatchingElement` | **REJECTED.** It suppresses the symptom and needs the do-not-touch list lifted for no gain. |
| **(c)** callers differ by purpose | **REJECTED**, for the reason the implementer gives — it is the shape §3 already rejected. |
| **(d)** blind retry loop | **REJECTED as a fix.** But see §2.5's correction: a *bounded wait-for-condition* is a different thing and is now live. |

**The implementer's judgement in stopping was right, and their lean toward (a) was reasonable on the
information they had.** The argument that changes the answer — that half 2 is structurally
incapable of adding a control — is not visible from inside the implementation.

## R3 — Do this next: a read-only timing probe

**Nothing gets fixed until the cause is known.** `HANDOVER-6.md` §7 lesson 2: an acceptance must test
the defect, not the fix's theory of it. This spec skipped that step and shipped a theory.

**Probe requirements:**

1. **Read-only.** No control is set, toggled, clicked or selected. No trade. No `.vb` change.
2. Launch through `tools/launch-app.ps1`. Confirm `— TESTNET` in the title.
3. Open the settings window. Immediately begin polling, and record for each attempt:
   - **Query A** — is the settings window in `RootElement.FindAll(Children, PID)`?
   - **Query B** — does `Find-ByControlType(mainForm, ComboBox)` reach `cboBridgeMode`?
4. Poll from 0 ms to about 2000 ms. Suggested interval 50–100 ms. Record the **first success time**
   for A and for B, independently.
5. **Repeat about 20 times**, closing and reopening the settings window each round.
6. Report a table: round · first-success ms for A · first-success ms for B · never-succeeded.

**What each outcome means:**

| Outcome | Cause | Fix that follows |
|---|---|---|
| B succeeds within a bounded time, every round | **Render latency.** The main form's traversal always reaches the controls once the tree has settled. | A **bounded wait-for-condition** before the search, with a deadline and a loud timeout. **No union. No change to `Get-ProcessWindows`.** |
| B never succeeds in some rounds, but A does | **Topology.** The window really is a separate top-level in those rounds. | A union *is* needed — and then option (a) becomes necessary to stop the double-count. Re-scope both together. |
| Neither A nor B succeeds in some rounds | The window is absent from the UIA tree entirely for a while. | Bounded wait, plus the Win32 `EnumWindows` route held in reserve at §2.5. |

**Report the numbers even if they are inconvenient.** If the fault does not reproduce at all in
20 rounds, say exactly that. "Did not reproduce" is a result. A fix built on an unreproduced fault
is what produced this ruling.

## R4 — `Merge-ProcessWindows` stays, and is currently unused

Commit `1f4b931` is kept. The seam is correct and its check script passes 5/5. It is **dead code as
of `59de8c1`** — recorded here so nobody later reads it as mystery leftovers. If the probe rules out
every union shape, remove it and its check script in the same commit.

## R5 — Screenshot rule, owner-approved 2026-08-07

The implementer disclosed a desktop-wide screenshot that captured unrelated sensitive content, and
deleted it. **Disclosing it was correct.** The tooling was checked in response:

- `tools/screenshot-mainform.ps1` captures **the main form only** (Win32 `PrintWindow`).
- `tools/screenshot-full.ps1` is **not** a desktop capture despite the name — it is a full-**form**
  capture through the app's own hotkey.
- **Neither repo tool can capture the desktop.** That capture came from a method outside the harness.

🚨 **Standing rule, now in `HANDOVER-6.md` §5: a seat captures the APP only, via
`tools/screenshot-mainform.ps1` or `tools/screenshot-full.ps1`. Desktop-wide capture by any other
means is not permitted.** The pre-existing rule in those scripts only said screenshots go to
git-ignored `verify/out/` and are deleted after use — it never said *do not capture the desktop*,
because nobody had thought to.

## §Model and effort for the probe (`HANDOVER-6.md` §7b)

> **Recommendation: Sonnet, medium effort — the SAME implementer seat, continued.**

- **Why:** read-only diagnostics in `tools/`. No app change, no trade, no `.vb` file. The seat
  already holds the context, has the app launch working, and has demonstrated it stops at the right
  moment rather than working around a spec.
- **Where the thinking should go:** (1) polling must not itself perturb what it measures — do not
  foreground, click or focus anything between samples; (2) A and B must be timed **independently**,
  because the whole question is whether they diverge; (3) the round count matters more than the
  interval — an intermittent fault needs repetitions.
- **What would change the answer:** if the probe shows the fault needs Win32 P/Invoke or any change
  inside the app, **stop and escalate**. That moves the fix to **Opus, high effort**.
- **Answer in the impl report:** was Sonnet at medium right for the probe?

---

# §Probe verdict and the FIX — 2026-08-07. This is the live section.

## R6 — The probe is ACCEPTED. Verified independently, not taken on report.

`impl-report-harness-owned-window-probe.md`, commit `a0e59bf`. Checked at the coordinator seat:

- **Query A and Query B measure what the report says they measure** —
  `tools/probe-owned-window-timing.ps1:109-121`. Query B correctly uses the **main form** as its
  search element.
- **The headline is not a string artefact.** Query A matches on `Name -eq 'AutoTradeSettings'`, and
  the form really is titled that: `AutoTradeSettings.Designer.vb:698` sets `Text = "AutoTradeSettings"`.
  A wrong string would have produced the same 0/39, so this had to be checked.
- **Read-only holds.** The only mutation anywhere in the script is `Invoke()` on the main form's own
  Auto Settings toggle. No control inside the settings window is touched. Zero `.vb` files.
- **Finding their own probe defect, discarding all 20 rounds, and rebuilding with independent Win32
  ground truth was the right call** — and reporting the discard rather than quietly re-running is
  the behaviour this house wants. It is the same discipline as `HANDOVER-6.md` §7 lesson 1.

## R7 — The result is stronger than "render latency". The ORIGINAL defect description was wrong.

**Query A succeeded 0 times in 39 rounds.** Not intermittently — **never**.

The consequences run backwards through this whole feature:

1. The settings window is **never** a child of the desktop root. It is always only a descendant of
   the main form.
2. So `Get-ProcessWindows` — root children only — has **never** returned it, on any run, ever.
3. So the harness has **always** driven that window through the main form's descendants search.
   Query B is exactly that path, and it succeeds in **37 of 38** rounds where the window was
   confirmed open, in **9–22 ms**.
4. **Therefore `review-harness-exact-match.md` §5.2's framing is wrong on causation.** It says
   `Get-ProcessWindows` *"intermittently misses the owned window"* and that this *"makes any
   settings-window drive step flaky."* The miss is **total, permanent and harmless**. The flakiness
   was the descendants search not having resolved yet.
5. **And §1.2 of this spec was wrong**, which is why §2 was wrong. I specified a fix for a
   window-enumeration problem that was never the cause. The probe is what caught it.

## R8 — The fix: a bounded wait. And state its limit honestly.

**Shape:**

- Add **one new function** to `tools/harness-common.ps1` that wraps `Select-MatchingElement` in a
  bounded poll. Do not modify `Select-MatchingElement` or `Select-BestMatchIndex` — the do-not-touch
  table stands.
- Poll until the result `Kind` is anything other than `None`, or the deadline expires.
- 🚨 **Never wait on `Ambiguous`.** An ambiguous match is a real, immediate refusal. Waiting on it
  would delay a correct refusal and could let a late-appearing control change the answer. Return it
  the instant it appears.
- On timeout, fail exactly as today: exit 2, all candidates printed. **Loud, never silent.**
- Convert the five drive scripts to the wrapper: `set-textbox.ps1`, `toggle-checkbox.ps1`,
  `select-combo-item.ps1`, `click-button.ps1`, `click-PLACES-ORDER.ps1`.

**Deadline: 500 ms, with a 25 ms poll interval.** Observed successes are 9–22 ms, so 500 ms is more
than twenty times the worst observed case. A genuine not-found now costs 500 ms — acceptable, and
the reason the deadline is not larger.

🚨 **What this fix does NOT do, and the implementer must not claim it does.** In Set 1 round 1 the
window was open for the **full 2000 ms** and Query B never resolved. **No deadline fixes that
round.** So this fix converts a random failure into: deterministic success in about 97% of cases,
and a **loud timeout** in the rest. It is an improvement, not a cure. Say so in the report.

## R9 — Two things to clean up in the same pass

1. **Remove `Merge-ProcessWindows` and `tools/checks/test-merge-process-windows.ps1`.** R4 said they
   stay only if a union shape might still be needed. The probe rules out every union shape — a union
   can only add a window whose controls are already reachable. Remove both in one commit, and say in
   the message that they are being removed because the probe rules them out, not because they were
   wrong.
2. **Record a known limitation, do not fix it:** `close-popup.ps1` iterates `Get-ProcessWindows`, so
   it **cannot close the settings window** — that window is never in the list. Use the main form's
   Auto Settings toggle instead, which is what the probe does. Add a comment saying so at
   `tools/close-popup.ps1`.

## R10 — Left open deliberately

- **Set 1 round 1** — window confirmed open by Win32 for 2000 ms, neither UIA query resolved. No
  root cause. The implementer's guess is a UI-thread message race around the toggle. **Not
  established, and not a blocker.** Recorded here so it is not rediscovered as new.
- **Only `ComboBox` / `cboBridgeMode` was measured.** The pattern is assumed to hold for `Edit` and
  `CheckBox`. The acceptance below tests all three, which closes this cheaply.

## §Acceptance for the fix

1. **The wait works.** With the settings window freshly opened, run `set-textbox.ps1`,
   `toggle-checkbox.ps1` and `select-combo-item.ps1` against a settings-window control, **20 rounds
   each**, opening the window immediately before each attempt. Report the success rate and the
   observed wait time. Restore every value you change and read it back to prove the restore.
   - ⚠ `HANDOVER-6.md` §5.4 first: the main form's `FormClosing` persists all 11 standing input
     fields unconditionally. **Do not leave a harness value in a persisted field.**
2. **`Ambiguous` still refuses instantly.** Use a pattern that ties — a bare `Trig` ties three ways.
   Assert the refusal is immediate, not delayed by the deadline.
3. **A genuine not-found still fails loudly** — exit 2, candidates printed, after about 500 ms.
4. **The deny check and the TESTNET gate still refuse.** `click-button.ps1 "Limit BUY"` → exit 3.
   Verify the `click-PLACES-ORDER.ps1` TESTNET refusal **by diff**, not by manufacturing a live
   session.
5. **`tools/checks/test-select-best-match.ps1`** → 7/7.
6. **Gate** → `GATE PASSED`, OrderCheck **268/268**. **Censuses** → 68 occurrences across 64 lines.
   You are touching no `.vb` file; run them anyway.

## §Model and effort for the fix (`HANDOVER-6.md` §7b)

> **Recommendation: Sonnet, medium effort — the same seat, continued.**

- **Why:** `tools/` only. No `.vb` file. No order, stop-loss, receive, bridge or act path. The seat
  has now built and debugged the probe, and has demonstrated it stops when a spec is wrong.
- **Where the thinking should go:** (1) the `Ambiguous`-must-not-wait rule — it is the one place a
  naive retry loop changes a safety-relevant answer; (2) the five call-site conversions must not
  disturb the deny check or the TESTNET/PID gate; (3) the honest framing in R8 — do not report a
  cure.
- **What would change the answer:** if the fix needs anything inside the app, stop and escalate.
  That moves it to **Opus, high effort**.

---

# §Coordinator review of the fix — 2026-08-07. APPROVED WITH ONE DEFECT (D1).

Reviewing `impl-report-harness-owned-window-fix.md`, commits `2c6cb7b` · `4fe4a33` · `9341888` ·
`689a812` · `6d207cd`. Per `HANDOVER-6.md` §7 the review verifies the code, executes the gate,
re-runs the censuses, and runs an adversarial pass. All four were done at the coordinator seat.

## R11.1 — Executed here, not taken on report

| Check | Result |
|---|---|
| `tools/checks/verify-gate.ps1` | **GATE PASSED**, OrderCheck **268/268** |
| Nine `frmMainPageV2.vb` censuses | **68 occurrences across 64 lines** |
| `.vb` / `.vbproj` files touched | **0** |
| Five call-site diffs | **one line each**, `-LabelBuilder` preserved on both button scripts |
| `tools/close-popup.ps1` | comment-only, plus a stale usage example dropped |
| Dangling references to the removed seam | none outside the historical spec-back, which is correct |
| Harness bin settings after the run | `session_policy.enabled` False · `max_slippage_atr_checked` False · `trigger` 777 · `market_stop_loss` 200 — **identical to the values recorded before the run, so the restores held** |

## R11.2 — What the implementation got right

- The wrapper **wraps**. `Select-MatchingElement` and `Select-BestMatchIndex` are untouched.
- `if ($m.Result.Kind -ne 'None') { return $m }` — **`Ambiguous` is returned instantly.** Verified in
  the code, not just in their test.
- The timeout path returns the same object, so every caller's exit-2 diagnostic fires unchanged.
- The deadline check sits **after** the call, so at least one attempt always happens.
- `-LabelBuilder` is passed through only when supplied, so `Select-MatchingElement`'s default still
  applies for the three scripts that do not pass one. That is the correct handling and it is easy to
  get wrong.
- **§3 of their report is the best thing in this pass.** They instrumented three layers rather than
  reporting the acceptance-1 round numbers, and said plainly that those numbers do not measure the
  wait loop. They also reproduced the census trap on themselves and published the wrong first answer.

## 🚨 R11.3 — D1: the bounded wait can NEVER retry in a real caller. It is a no-op as shipped.

**This defect is in my spec R8, not in their code. They implemented R8 exactly.**

The proof is structural and needs no app. Stubbing `Select-MatchingElement` with the first-attempt
cost the implementer **measured** in a fresh process:

| Scenario | First attempt | Deadline | Attempts made |
|---|---|---|---|
| Cold process, measured low | 740 ms | 500 ms | **1** |
| Cold process, measured high | 950 ms | 500 ms | **1** |
| Warm / in-process | 20 ms | 500 ms | 9 |

- The first `Select-MatchingElement` call in a fresh process costs **740–950 ms** (their §3, nine
  trials).
- The deadline check runs after that call. `740 ≥ 500`, so the function returns immediately.
- **`Start-Sleep` is never reached. The 25 ms interval is never used.**
- **Every drive script is a fresh process.** So in every real invocation the loop makes exactly one
  attempt — which is what the code did before this fix.

**Consequence:** the fix cannot cover a render race that outlasts the first query, which is the only
failure mode it exists for. Acceptance 1 passed 60/60 because process-start latency alone already
exceeds the race, not because the wait engaged. That is `HANDOVER-6.md` §7 lesson 2 — an acceptance
must test the defect, not the fix's theory of it.

**Why this is my defect:** R8 set 500 ms from the probe's **in-process** 9–22 ms figures. The
implementer measured the cold-process cost, and their §3 says outright that R8's framing "describes
the probe's in-process measurement, not what a real cold-process caller experiences." **They got one
step away from this finding and stopped.** The remaining step — *therefore the loop can never loop*
— is what a review is for.

## R11.4 — The correction. Two small changes.

1. **Start the deadline clock AFTER the first attempt.** The first query's cost is fixed process
   overhead and has nothing to do with the render race. `DeadlineMs` then means what R8 intended: a
   retry budget. Keep 500 ms and 25 ms. Simulated with the same stub, a 740 ms first attempt then
   yields **2+ attempts** instead of 1.
2. **Add a retry test that proves the loop can loop** — `tools/checks/test-wait-for-matching.ps1`.
   **I verified the mechanism before specifying it, per `HANDOVER-6.md` §7 lesson 1:** a test script
   that dot-sources `harness-common.ps1` and then defines its own `Select-MatchingElement` **does**
   shadow the real one for `Wait-ForMatchingElement`'s call. Confirmed working — 3 attempts, correct
   result. Required cases:
   - Returns on attempt 1 when the first result matches.
   - **Retries and succeeds on attempt 3** when the first two return `None`.
   - **Returns `Ambiguous` on attempt 1 without sleeping** — the safety-relevant case.
   - Times out with `Kind = 'None'` after a bounded number of attempts, and does not hang.
   - A slow first attempt that exceeds the deadline **still permits at least one retry**. This is
     D1's regression test — it fails against the current code.

**Do not re-run the 60-round live acceptance.** It passed, and D1 does not invalidate it: the
shipped behaviour is *at worst* what the code did before. Re-run acceptance items 2, 3 and 4 only,
plus the new check script, the gate and the censuses.

## R11.5 — Recorded, not actioned

- **The cold-process ~740–950 ms first-query cost is now a known harness property.** Every drive
  script pays it. It is not a defect and it is out of scope, but it is the reason any future timing
  work in `tools/` must measure from a fresh process, never in-process.
- **Toggling `chkSessionPolicyOn` 20 times in acceptance 1 was riskier than it looks.** That
  checkbox is persisted gate config (`HANDOVER-6.md` §6.7). The even-count argument is sound and the
  restore was verified at the artefact — but an interrupted run would have left the harness bin's
  session policy ON. **Prefer a non-persisted control for repeat-toggle tests.**

> **Model and effort for the correction:** Sonnet, medium — same seat, continued. `tools/` only, and
> the new check script needs no app.

---

# ✅ §R12 — D1 FIXED AND VERIFIED. FEATURE CLOSED 2026-08-07.

`290af9e` (fix + check script) and `d6cac3f` (verification report). Verified at the coordinator seat:

| Check | Result |
|---|---|
| The fix itself | Attempt 1 now runs **unconditionally**, before the stopwatch starts. The 500 ms / 25 ms budget governs retries only. |
| `tools/checks/test-wait-for-matching.ps1`, run here | **5/5**, including D1's own regression case |
| **My own independent stub**, the one that exposed D1 | 740 ms first attempt → **2 attempts** · 950 ms → **2 attempts**. Both were **1** before. |
| `tools/checks/test-select-best-match.ps1`, run here | 7/7 |
| `tools/checks/verify-gate.ps1`, executed here | **GATE PASSED**, OrderCheck **268/268** |
| Nine censuses, run here | **68 occurrences across 64 lines** |
| `.vb` / `.vbproj` touched | **0** |
| `click-button.ps1` / `click-PLACES-ORDER.ps1` diff since `689a812` | **empty** — the deny check and the TESTNET/PID gate are provably untouched |

**Two things the implementer did that the review did not ask for, and both raise the bar:**

1. **They proved their regression test discriminates.** They stashed the fix, ran the check script
   against the old code, and confirmed case 5 **fails** with exactly D1's signature (`calls=1`), then
   restored and confirmed it passes. *A green test that would also be green against the bug is not a
   test.* This is `HANDOVER-6.md` §7 lesson 2 applied to a fixture rather than to an acceptance.
2. **They caught their own instrumentation error and said so** — their first attempt at measuring the
   real function accidentally re-implemented the old loop inline, which would have produced a
   confident, wrong number.

**Nothing is owed on this feature.**
