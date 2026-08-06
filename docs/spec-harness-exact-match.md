# Spec — SB3: harness element selection must prefer an EXACT id, and never pick silently

**Origin:** `spec-back-c1-acceptance-2026-08-06.md` **SB3** *(raised as "D3"; renumbered — H-6 §7bb:
`D` = a defect in code, `SB` = a spec-back finding. This one is a real defect, but in `tools/`)*.
Supersedes the `ROADMAP-2026-08.md` §5 backlog row *"`set-textbox -Exact` switch"* — see §2.3 for
why a switch is the wrong shape.

**This has already bitten, which is why it left the backlog.** On the C1 acceptance run,
`set-textbox` was asked for `Trig. P.` and set **`Trig. O.`**, then **reported success**:
`Set '' (id 'txtTriggerOffset', …) = '300'`. The stop trigger stayed at 5 instead of 300, so the SL
sat ~$6 below market for the whole of acceptance 4. **The run survived only because price moved up**
(64806.5 → 64825.62). Six dollars the other way and the position self-closes mid-test, and the
flat-trap reading is taken against a close nobody drove.

**Recommended implementer — per H-6 §7b (advisory; the owner picks): Sonnet 5, medium effort, fresh
conversation.** Reasoning in §7.

**Target:** `tools/harness-common.ps1` (the new seam + selector) · `tools/set-textbox.ps1` ·
`tools/toggle-checkbox.ps1` · `tools/select-combo-item.ps1` · `tools/click-button.ps1` ·
`tools/click-PLACES-ORDER.ps1`. **No app code. No `.vbproj`. Nothing under
`DeribitOrderPlacementApp/`.**

**Ground rules:** standing (gate per commit, never push, one commit per section, impl report).
**Escalate any spec defect BEFORE implementing** — §0 is empty, which is not a promise that it should
stay empty.

---

## 🚫 Do-not-touch — read before editing `click-button.ps1` or `click-PLACES-ORDER.ps1`

**The trade-deny check is safety-critical and its substring matching is CORRECT. Do not "fix" it.**
`click-button.ps1:32-38` tests the deny list with `IndexOf(...) -ge 0` against **both** the caption
and the designer id, so a caption change cannot sneak a trade button past the tier.

**The asymmetry is the whole point of this spec, so state it plainly:**

| | over-matching means | so the right rule is |
|---|---|---|
| **DENY** (refusing a control) | you refuse something you could have allowed — **safe** | **substring**, deliberately broad |
| **SELECT** (choosing a control to act on) | you act on the **wrong control** — this defect | **exact first**, ambiguity loud |

Whatever the selector returns must still be passed through the deny check, **before** any actuation
and with its logic unchanged. The deny check moves position in the file if you restructure; it does
not change meaning. A commit that weakens it is a review-blocking defect.

Also unchanged: `Find-MainFormElement`'s frozen title-prefix match, `close-popup.ps1`'s window
matching (both are window-level, not control-level), `Test-SameElement`, and the blur discipline in
`set-textbox.ps1` (`harness-common.ps1:129`+ — a real blur must land on a *different* focusable
control; that is a separately-earned fix and this spec does not go near it).

## §1 — The defect, precisely

`Test-ElementMatch` (`harness-common.ps1:109`) is a **per-element predicate**: case-insensitive
substring against `Name` then `AutomationId`, `$true` on the first hit. **It has no visibility of the
candidate set**, so it cannot know another element would have matched better.

"First match wins" is therefore a property of the **five call sites**, which all share one shape:

```
foreach ($w in $windows) {
    foreach ($e in (Find-ByControlType -Element $w -TypeName <Type>)) {
        $allNames.Add($label)                                    # for the not-found diagnostic
        if (-not (Test-ElementMatch -Element $e -Pattern $P)) { continue }
        ... act, report success, exit ...
```

| Script | Line | Control type |
|---|---|---|
| `set-textbox.ps1` | 38 | Edit ← **where it bit** |
| `toggle-checkbox.ps1` | 33 | CheckBox |
| `select-combo-item.ps1` | 39 | ComboBox |
| `click-button.ps1` | 51 | Button |
| `click-PLACES-ORDER.ps1` | 45 | Button (privileged) |

`txtTrigger` is a substring of `txtTriggerOffset`, and `txtTriggerOffset` enumerates first. **Both
are legitimate matches under the current rule; the loop simply takes whichever it reaches first.**

**The failure mode is not "it matched wrongly" — it is "it acted on the wrong control and REPORTED
SUCCESS."** That is the harness commit-verification class
(`memory: harness-commit-verification-trap`): it reported the work it *did* do, on the wrong target.

**Scope, measured by the reporting seat and to be re-verified by you (§Acceptance 2):** all 28 `Edit`
AutomationIds were enumerated and the `txt` prefix protects every other near-collision —
`txtStopLoss` is **not** a substring of `txtMarketStopLoss` or `txtPlacedStopLossPrice`. This is the
only collision **today**. Nothing prevents the next one, which is why the fix is structural rather
than a rename.

## §2 — The fix

### 2.1 A pure decision seam (the house pattern)

Extract the choice as a **pure function over plain data**, so it is exercisable with no running app,
no UIA and no window. This mirrors `ShouldSend`, `EffectiveSizeUsd` and `ShouldWrite` — the reason
those exist is that a decision buried in a loop cannot be tested.

```
Select-BestMatchIndex -Candidates <array of @{Name=…; AutomationId=…}> -Pattern <string>
    -> @{ Index = <int, -1 when none>; Kind = 'ExactId'|'ExactName'|'Substring'|'None'|'Ambiguous'
          Tied = <int[] indices, when Ambiguous> }
```

**Precedence, first non-empty tier wins:**

1. **Exact `AutomationId`** (`OrdinalIgnoreCase`)
2. **Exact `Name`** (`OrdinalIgnoreCase`)
3. **Substring** on either — the current rule, preserved as the fallback

**Within the winning tier: exactly one candidate ⇒ that one. More than one ⇒ `Ambiguous`, and the
caller FAILS LOUDLY listing every tie.** Never pick arbitrarily. Arbitrary selection is the defect,
and it is no less a defect for being narrower.

*A tie at tier 1 means two controls sharing an AutomationId — impossible within one WinForms form,
possible across the main form and the settings window. If a real call site turns out to tie, do not
paper over it: report it and we will add a window disambiguator.*

### 2.2 The selector

`Select-MatchingElement -Windows <…> -TypeName <…> -Pattern <…>` gathers **all** candidates across
all windows **first**, calls `Select-BestMatchIndex`, and returns the chosen element plus the label
list the call sites already build for their not-found diagnostic. Gathering before deciding is the
structural change; everything else follows from it.

### 2.3 Why NOT the backlog's `-Exact` switch

The backlog asked for a `set-textbox -Exact` switch. **Rejected, and the reason generalises:** a
switch is only correct when the caller remembers to pass it, so it turns a silent wrong-control into
a silent wrong-control-unless-you-remembered. **Exact-first-by-default needs no caller to remember
anything**, and it is behaviour-preserving wherever the pattern is unambiguous — which is every
existing call site. Fold this note into the ROADMAP row when you close it.

## §3 — Converting the call sites

All five become: build the candidate list → select once → act. Keep each script's existing
not-found message and its `$allNames` diagnostic (they are good, and the failure output is most of
the harness's value). **Keep every script's exit codes and stdout shape** — `place-and-verify.ps1`
and the drive scripts parse these, and a changed contract there is a silent breakage of a different
kind.

**On `Ambiguous`, exit non-zero with a message naming the pattern and every tied candidate.** That
message is the deliverable: the next person to hit this must see *why* rather than a wrong success.

## §Acceptance

1. **Gate green** (`tools/checks/verify-gate.ps1` → `GATE PASSED`, OrderCheck **268/268**) and the
   nine censuses re-run and reported. Both should be untouched by a `tools/`-only change — **say so
   explicitly rather than omitting them**, because "unchanged" and "not checked" look identical in a
   report.
2. **Re-verify the collision scope yourself.** Enumerate the `Edit` AutomationIds and confirm
   `txtTrigger` ⊂ `txtTriggerOffset` is still the only one. Report the number you found; if it is
   not 28, say so.
3. **🚨 The regression test — this is the one that matters.** With the app up, run
   `set-textbox` for `txtTrigger` with a distinctive value and confirm **`Trig. P.` changed and
   `Trig. O.` did not**. Read **both** boxes back. *The old code passed its own success check while
   setting the wrong box, so a success message is not evidence — the discriminator is the untouched
   neighbour.*
4. **Ambiguity is loud.** Drive a pattern that matches two controls at the same tier (e.g. a bare
   `Trig`) and confirm a non-zero exit naming both, **not** a silent pick.
5. **No behaviour change anywhere else.** Exercise each of the five scripts once against a control
   it already addresses successfully today, and record the command and output for each.
6. **The deny check still fires.** Confirm `click-button.ps1` still refuses a `trade-buttons.txt`
   entry, and that `click-PLACES-ORDER.ps1` still refuses on a non-TESTNET title. **Do not place an
   order to prove this** — the refusal is the observable.
7. **`Select-BestMatchIndex` exercised directly** on a table of synthetic candidates covering: exact
   id · exact name · substring-only · the `txtTrigger`/`txtTriggerOffset` pair in **both**
   enumeration orders · no match · a tie. Order matters: the bug was order-dependent, so a test that
   only runs the lucky order proves nothing.

## §Commits

1. §2.1 the pure seam + its direct exercise (acceptance 7).
2. §2.2 the selector.
3. §3 the five call sites.
4. Impl report — **§3 = what was NOT established**, and the ROADMAP row closed with §2.3's note.

## §7 — Model and effort (H-6 §7b)

**Sonnet 5, medium effort, fresh conversation.**

*Why, tied to what it touches:* `tools/` only, no app code, no trading path, and **nothing here can
reach the order/SL/receive/bridge/act paths that force the Opus tier** (H-6 §7a). The logic is a
three-tier precedence rule over plain data, and the spec names the precedence, the tie rule, the
call sites and the acceptance. The risk is concentrated in *restructuring* five scripts, which is
careful mechanical work rather than deep reasoning.

*Where the thinking should go:* (1) **not weakening the deny check** while moving code around it —
the one safety-critical thing in these files; (2) **preserving exit codes and stdout shape**, which
other scripts parse; (3) **the tie rule** — resisting the temptation to "just pick the first" when a
tie shows up in testing, because that is the defect wearing a smaller hat.

*What would change the answer:* if converting the call sites turns out to require touching the blur
discipline in `set-textbox.ps1` or the deny logic itself, **stop and escalate** — that is Opus-tier
work and a different spec. It should not, and the do-not-touch section exists to keep it that way.
