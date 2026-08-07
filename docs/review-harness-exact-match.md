# Coordinator review — SB3: harness exact-match element selection

**Reviewed at HEAD `9ecf40d`** (`188bdab` · `3611d49` · `9ecf40d`).
**Spec:** `spec-harness-exact-match.md` · **Report:** `impl-report-harness-exact-match.md`.

## VERDICT: **APPROVED.** No defects. Two follow-ups, neither blocking, both filed.

Nothing below was taken on the report's word.

## 1. Executed / verified

| Check | Result |
|---|---|
| `tools/checks/verify-gate.ps1` | **GATE PASSED, OrderCheck 268/268** — run by this seat |
| App code touched? | **None.** `git diff --stat 68893c5..HEAD -- DeribitOrderPlacementApp/` is empty, so the censuses *cannot* have moved — the claim holds structurally, not just by assertion |
| `tools/checks/test-select-best-match.ps1` | **7/7 OK**, run by this seat, no app open — including the real collision in **both** enumeration orders (acceptance 7's discriminating requirement) |
| `Test-ElementMatch` fully removed? | Yes — **zero remaining `.ps1` callers**; surviving hits are historical docs only |

## 2. The code

**`Select-BestMatchIndex` is correct.** Tier order is `ExactId → ExactName → Substring`; the first
non-empty tier decides; one candidate wins outright and **more than one returns `Ambiguous` with
every tied index** — never an arbitrary pick. Empty candidate set falls through to `None`. An empty
`$Pattern` degrades to "everything matches at the substring tier" ⇒ `Ambiguous`, which is the safe
direction. The `return ,$idx` comma-wrap is correct and the comment gives the real reason
(PowerShell unrolls a `List` on the pipeline, so a 0-or-1 element result would arrive as `$null` or
a bare int and `.Count` would break silently).

**`Select-MatchingElement` is correct, and one detail in it is better than the spec asked for.**
`$elements`, `$ownerWins`, `$candidates` and `$fullLabels` are appended in lockstep and so stay
index-aligned with `Result.Tied`. `$allLabels` deliberately is **not** aligned — it preserves each
call site's pre-existing habit of skipping blank-caption candidates. **Using `AllLabels` to name
ties would therefore have printed the wrong candidates**; the separate `FullLabels` list exists
precisely to prevent that. The spec did not anticipate this; the seat found it and solved it.

## 3. 🚫 The do-not-touch — verified, not assumed

The spec called a weakened deny check **review-blocking**, so it was checked directly rather than
read about. `git diff -w` on `click-button.ps1` shows the deny **logic does not appear in the diff at
all** — only its comment re-indents as the surrounding loop is removed. In the current file the
check sits at `:72–79`, operating on the selected element, and `Invoke()` is at `:82`. **Deny still
runs, still tests both caption and designer id, still before actuation.**

`click-PLACES-ORDER.ps1`'s TESTNET/harness-PID gate and `set-textbox.ps1`'s blur discipline are
likewise untouched.

## 4. Acceptance judgements

- **Acceptance 3 (the regression test) is sound, and the seat corrected the spec's own worked
  example.** The spec said to drive `Trig. P.`; both boxes carry an **empty UIA `Name`** (their
  captions belong to separate `Label` controls), so that literal matches nothing and exits 2 —
  correctly. The pattern that reproduces the incident is `txtTrigger`, which is what §1's table
  actually names. The discriminator was applied as specified: **`txtTriggerOffset` read back
  unchanged**, not merely a success message.
- **Acceptance 6's non-TESTNET refusal was verified by inspection, not live.** Correct call —
  manufacturing a non-TESTNET session to prove a refusal is exactly the kind of thing this project
  does not do casually, and the diff evidence is adequate.
- **The 4-commit plan became 3, disclosed.** Combining the seam with its only caller is right;
  a commit containing an uncalled function is worse. Accepted.

## 5. Follow-ups — filed, not blocking

1. **The PS 5.1 quirk is REAL but was filed one generalisation too wide.** Reproduced here in a
   fresh process on `5.1.26100.8875`:

   | shape | `@($list)` |
   |---|---|
   | `List[object]` of strings · of hashtables · **empty** | **throws `ArgumentException: Argument types do not match`** |
   | **`List[int]`** | **works** |

   So it is **`List[object]`**, not `List[T]`. The memory entry's closing advice said "any new tools
   script that builds a `List[T]`", which would have future sessions avoiding a construct that is
   fine — and the seat's own `Get-TierIndices` returns a `List[int]` whose comma-wrap is for an
   unrelated (correct) reason. **Memory narrowed to `List[object]`.** *(Same shape as §7 lesson 7,
   inverted: a summary that broadens past its evidence rather than dropping a caveat.)*
2. ⚠ **CORRECTED 2026-08-07 — the word "intermittently" below is WRONG, and it sent a whole spec
   the wrong way.** A 39-round timing probe (`impl-report-harness-owned-window-probe.md`) found the
   settings window is **NEVER** a root child — 0 successes in 39 rounds. `Get-ProcessWindows` has
   therefore never returned it, on any run. That miss is **total, permanent and harmless**, because
   control search reaches the window through the main form's descendants, which succeeds in 9–22 ms.
   **The flakiness this row blames on enumeration was render latency in the descendants search.**
   Ruling and the fix: `spec-harness-owned-window.md` §Probe verdict, R7. The original text is kept
   below exactly as written.

   **`Get-ProcessWindows` intermittently misses the owned `AutoTradeSettings` window** — it is
   `Show(Me)`-owned, and `RootElement.FindAll(TreeScope.Children, …)` sometimes does not enumerate
   it even while Win32 reports it visible; found under `TreeScope.Descendants` of the main form.
   Pre-existing, outside this spec's target list, and **it would equally have blocked the old code**
   — so not a regression. **Added to `ROADMAP-2026-08.md` §5** so it does not live only in an impl
   report.

## 6. One refinement to the record

Acceptance 4 surfaced a **third** substring collision — a bare `Trig` also matches
`txtPlacedTrigStopPrice`. That is not a new defect (the fix refuses loudly, which is the point), but
it narrows a claim: "`txtTrigger` ⊂ `txtTriggerOffset` is the only collision" is true **of the exact
id pair**, not of substring matching generally, where looser patterns collide more widely. The fix
is indifferent to this — which is the argument for having fixed it structurally rather than by
renaming controls.
