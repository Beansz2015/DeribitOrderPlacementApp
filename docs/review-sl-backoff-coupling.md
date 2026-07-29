# Coordinator review — N1b SL-backoff coupling (2026-07-28)

> **ADDENDUM 2026-07-30 (escalation-defect ruling; see the spec's amended Acceptance section):**
> §4.1's premise — "acceptance 2/3 owner-run PASSED per the handoff" — is **WITHDRAWN as to
> acceptance 2**: its observable was structurally unreachable from the chase path (the optimistic
> success-reset pins the counter at 0↔1 and the backoff at 666 ms), so whatever run occurred, it
> did not observe acceptance 2 as then written. The runtime record was correctly HELD and now
> follows the corrected recipe. **The approval of the coupling itself STANDS** — commit 2 does
> exactly what was ruled; the defect is the pre-existing optimistic reset it interacts with
> (fix: `spec-sl-backoff-confirmed-reset.md`). Reviewer's self-assessment, on the record: this
> review CONFIRMED the dead chase `Catch` (§2 ask 2) but did not chase its corollary — a dead
> Catch means the success path always executes, making the reset unconditional. The implementer
> found the corollary while writing the runtime recipe. Third instance of the era's standing
> lesson: verify what is IN FORCE — including whether an acceptance's observable can exist.

> **CLOSURE 2026-07-30 — 3b(i) REVIEWED + APPROVED; N1b is CLOSED code-side.** `d3c9a04`
> (+ report addendum `e8d2f2c`) reviewed against Acceptance 5: gate EXECUTED by the reviewer —
> **GATE PASSED, OrderCheck 140/140**, all 16 SL-backoff fixtures passing under the reviewer's
> own run. The `NextSlBackoff` extraction verified **behaviour-identical** (`n` reproduces the
> assign-first/use-new-value order; the caller writes the same two fields in the same order;
> the read-modify-write window on the field is the same pre-existing class; `Private Const`s
> readable from `Shared` — nothing moved). All censuses re-run independently at the stated
> values, incl. `slUpdateFailures = 0` still exactly 1 (the site N1c moves) and `NextSlBackoff`
> = 2 (decl + caller). The four fixture groups do what the ruling asked: arithmetic pinned from
> the stamp, the acceptance-2 threshold now a fixture, the chase-path oscillation proven
> deterministically, and the no-reset escalation proven — shaped so N1c extends them.
> **Remaining on this mechanism: the owner's corrected-recipe runtime record (or wait for N1c)
> — everything else is closed. Next: fee-comms repoint → N1c, per the owner's sequencing.**

**Verdict: APPROVED. All three open asks RULED — none changes code.** Commits `afb4bbc` (commit 1,
comment-only) · `6d23bf0` (commit 2, the coupling per ruling `a973c3f`) · `65ef548` (impl report),
against `spec-sl-backoff-coupling.md` as amended. Review request = the implementer's handoff +
`spec-back-sl-backoff-coupling-id-scope.md`. Acceptance 1 + 4 PASS (verified below); acceptance
2 + 3 owner-run PASSED per the handoff — **the runtime record is the one outstanding document.**

## 1. What this reviewer executed and verified

- **Gate EXECUTED at HEAD `65ef548`: GATE PASSED, OrderCheck 124/124.**
- **`git show 6d23bf0` read in full.** The IL delta is exactly the 4-line guarded call at
  `:1645–:1648`; the id condition names exactly `223346 / 223348 / 223350`; every other changed
  line is comment (including the commit-1 prose fix restoring `emergencyFired` to raw 10).
- **Control flow walked at the hook:** the `10028` ownership return (`:1555`), the
  `already_closed` downgrade `Return` (`:1568`), and the id-31 abort-race `Return` (`:1617`) all
  precede the red emission (`:1622`); the coupling sits directly after it, inside the handler's
  swallowing Try. The benign exclusions hold by construction, as ruled.
- **A hazard the spec-back did not name, checked and clear:** the `:1552` placement-ownership
  skip (`messageId >= PlacementIdBase`) — `PlacementIdBase = 600000`, no overlap with the edit
  ids; SL-edit errors cannot be swallowed before the hook.
- **Censuses re-run independently, all at handoff values:** `BackoffStopLossRetry` raw 7 =
  3 call sites (`:1647` new, `:2384`, `:4380`) + 1 decl + 3 comments · `emergencyFired` raw 10 =
  9 code sites (1+2+3+3) + the 1 N1-era comment · `IsATRSlippageExcessive` 8 ·
  `RecordCommandedSLPrice` 3 · `isSLRepositioning` 3.

## 2. Rulings on the three asks

### Ask 2 (taken first — it is load-bearing): the unreachability derivation is CONFIRMED

Verified at the sites, not from the docs:

1. `SendWebSocketMessageAsync` (`:1123`) catches `WebSocketException` /
   `OperationCanceledException` / `Exception` and rethrows none — no send propagates.
2. The file contains exactly three `Throw`s (`:1017`, `:1026` auth; `:4001` ArgumentException
   "Price must be specified") — none can carry `too_many_requests`/`10028`.
3. Therefore the `:4380` arm (guarded by `ex.Message.Contains` on those tokens, its only await
   being the all-swallowing send) is dead, and the chase `Catch` (`:2379`) is dead for edit
   failures — its awaited edit routes through `UpdateStopLossForTriggeredStopLossOrder`, whose
   own `:4373` `Catch ex As Exception` swallows everything.

**The "first reachable trigger" claim stands.** The `:2300` commit-1 comment block reads
correctly in context (the "today" clause is immediately qualified by "N1b makes it reachable
for the first time") — no rewording needed.

### Ask 1 — the 10028 non-coupling is RATIFIED as correct design, not a gap

The rate-limit class already has an owner with the right scope: `HandleRateLimitError` (`:1510`)
responds to a 10028 by shrinking the rate-limiter credit pool 30% — a **global** brake on every
sender that consults the limiter. Coupling 10028 into the SL-specific backoff would (a) conflate
a global transport condition with an SL-edit rejection — the same wrong-mechanism coupling the
id-scope ruling just excised for non-SL ids, and (b) double-penalize the SL chase, which is
already subject to the shrunken credit pool. The classical "backoff on rate limit" role is
filled, by the correct mechanism, at the correct scope. The dead `:4380` arm stays as-is (its
guard is unreachable; removing it is hygiene-backlog material, not this pass).

### Ask 3 — acceptance 4 accepted as argued

The structural argument is complete: the only new code executes on a red emission of the three
SL ids; a session without such an emission cannot move `slUpdateFailures` off 0, and a zero
counter makes the `:2310` gate the same flat 333 ms as before commit 2. "Argued, not observed"
is the correct epistemic label for a byte-identity claim — observation would add nothing a
structural proof doesn't already give, and the owner's acceptance-2/3 runtime pass exercised the
non-parity side (the coupling firing) on the real bin.

## 3. On the record

- **The commit-1 tripwire self-correction is commended and is now the era's second occurrence**
  of the prose-near-tripwire trap (EV pass had the first). The census-moving comment was caught
  by *running* the grep rather than assuming a comment-only commit is census-neutral. Standing
  lesson reaffirmed: comment-only commits still get their censuses run.
- Cross-position counter persistence: already ruled an accepted residual in `a973c3f`; nothing
  in the implementation changes that assessment.
- Thread discipline as ruled: the new writer is the receive-thread call inside the handler's
  Try; no new synchronisation, matching the amended spec.

## 4. What remains

1. **The acceptance-2/3 runtime record** (owner-run, PASSED per the handoff) — land it as the
   pass's closing doc; it should capture the first-ever genuine `SL update rate limited` line
   (acceptance 2's evidence) and the benign-race counter log (acceptance 3).
2. Owner push (5 commits at review time; this review makes 6).
3. Queue after N1b closes: **N2 risk-sized bridge trades** (spec ready, Opus HIGH) → **C1**
   (proposal still awaiting owner relay to the engine seat).
