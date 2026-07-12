# Decision + forward spec — M.SL emergency baseline: hold the loss-cap anchor

**Answers:** `spec-back-emergency-baseline.md` (2026-07-08). **Decision:** coordinator, 2026-07-08 — **the fix is APPROVED as proposed** (delete the chase-advance of `emergencyBaseline`), with the doc-churn list, one new named invariant, one documented latency property, one backlog item, and the §6 triage below.
**Implementer:** same conversation. **Sequencing:** lands on the current local stack (with the §6(b) mini-fix), then ONE consolidated coordinator review of the whole unpushed stack, then the combined runtime session (this doc's §5 + the pending sl-chase/fill-reanchor items), then push.
**Ground rules:** standing. Impl report may be a short addendum to this doc's commit message + the doc edits — the change is one line; the doc churn is the bulk.

> **IMPLEMENTED 2026-07-08 (build 0/0).** §2 core fix: deleted `emergencyBaseline = newStopPrice` at the chase execute; rewrote the comment block with the new invariant. §3 doc churn: all six locations done (`spec-back-session-2026-07-07.md` §1 + F1, `spec-back-session-2026-07-04.md` §6 + §8 bullet 5, `HANDOVER-2.md` new 07-08 delta, `spec-back-reconcile-manual-sl-edits.md` + `spec-reconcile-manual-sl-edits.md` 4a-revert, the adopt-block code comment). §6(b) manual-TP mini-fix: the fill-reanchor now decides at the *filled* echo (`manualTPval` still intact, before the position-entry clear) and skips re-anchoring a manual (absolute) TP — placement already honors it; only auto-offset TPs re-anchor. §6(a) documented as a known property; §6(c) left. Latency-hoist backlog item NOT folded in. Owner runtime (§5) + consolidated stack review pending.

---

## 1. Ratification — the spec-back is correct on every point I could verify

- Write-site inventory confirmed at `49601a4`: non-zero writers are exactly `:1753` (chase — the bug), `:2272` (adopt), `:2281` (manual), `:4501` (restore seed-if-zero). Readers: the two emergency computations (`:1688` quote handler, `:3527` inside the send function) — both benefit identically. The discriminator's condition does not read `emergencyBaseline` — deleting the chase-advance is invisible to it. The 7-site reset inventory is intact.
- The #55 arithmetic and the "gap detector, not loss cap" characterization are right, and the spec-back's §2 is accepted: **my F1 "accepted as-is" ruling understated the structural effect** — F1 reasoned about one failed edit; the per-tick advance disables the cap on smooth moves entirely. Re-ruling below.
- §4's reconciliation stands: the rejected trade-#44 clamp was about *suppressing* manual-move consequences (still rejected); this fix is about the *app chase* (a different writer). The owner's three-rule model (§3) is adopted as the authoritative spec of the emergency reference.
- History note for the record: the Jul-4 "follow the live SL" ruling (`f42a6a7`) conflated two followers — manual moves (wanted, kept) and the app's own repositioning (unwanted, now removed). The old $5-leeway chase made the difference invisible; sl-chase-v2's 1-tick tracking exposed it. No prior owner decision is overturned; one is sharpened.

## 2. The fix (one line) + the new invariant

Delete the `emergencyBaseline = newStopPrice` half at the chase execute (`:1753`); `placedStopLossPrice = newStopPrice` stays (runaway protection). Rewrite the comment block above it (`:1747-1752`) to state the NEW contract:

> **Invariant (2026-07-08, replaces "references move together post-trigger"; REFINED by the hybrid, `spec-back-emergency-baseline-hybrid.md` + impl `25cac5f`):** post-trigger, the two references serve different masters and deliberately diverge — `placedStopLossPrice` is the CHASE reference and tracks the app's own repositioning; `emergencyBaseline` is the LOSS-CAP anchor. The anchor is seeded at the trigger adopt with the flip price, then **latches onto the actual top-of-book SL at the FIRST post-trigger reposition** (`emergencyBaselineSettled`) and is **FROZEN** thereafter — moved only by a detected manual SL edit (which re-anchors + re-freezes) and the restore seed, **NEVER** by the ongoing chase. Emergency = market ran `M.SL` beyond the anchor ⇒ taker close. Writers: adopt (`emergencyBaseline` + latch False), first-settle chase (`emergencyBaseline` + latch True), manual path (`emergencyBaseline` + latch True), restore (both), plus `placedStopLossPrice` on every chase.

> **Why the latch (owner #67):** the flip echo price is the SL's trailed/live limit, which can sit *below* top-of-book (in #67, 64013.5 vs the 64018 the SL settled at). Freezing on the flip price fired the cap ~one chase-step too early. The latch grabs the first reposition (the settle at top-of-book) instead. See the hybrid spec-back for the #55/#67/example reconciliation.

Consequences verified in advance: F1's baseline half becomes structurally impossible (re-closed as *resolved by removal*; the `placedStopLossPrice` half stays accepted); the adopt's `OrElse emergencyBaseline = 0D` arm still heals a null-price flip; the `StopLossTriggerOriginal` fallback still anchors a missed adopt; trade-#44 behavior (manual move arms the emergency) is preserved via the manual path.

**Conscious behavior change (owner-stated intent, said plainly):** the cap now FIRES. In a fast adverse run the emergency will market-close at anchor + M.SL even while the maker chase is keeping up — more taker closes than today (which never caps), bounded loss in exchange. That is the point.

**Documented latency property (now that the cap is live):** the emergency check sits inside the 333 ms throttled block, and `BackoffStopLossRetry` can push detection out to ~5 s under persistent edit failures (the housekeeping item-16 property). Normal granularity ≤ 333 ms is fine against a 20-point threshold; the 5 s worst case only occurs during edit-failure storms. **Backlog (not this fix):** hoist the emergency check above the throttle gate — requires a single-fire latch first (without the throttle, consecutive ticks could double-fire the market reduce before the flat echo lands). Design note recorded; do not fold in.

## 3. Doc churn (same commit — the "together" contract is stated in several places)

- `spec-back-session-2026-07-07.md` §1: replace the "References move together post-trigger" sentence with the §2 invariant (keep the orchestrator's 4th-writer note); §4 F1: append "baseline half resolved by removal 2026-07-08 (`spec-emergency-baseline-fix.md`); placedStopLossPrice half unchanged-accepted".
- `spec-back-session-2026-07-04.md` §6: append a supersession line to the `f42a6a7`/`968b26d` narrative ("chase-follow removed 2026-07-08 — baseline = loss-cap anchor, manual-moves-only"); §8 bullet 5 similarly.
- `HANDOVER-2.md`: one sentence in the newest state-delta block stating the §2 invariant.
- Code comments: grep `emergency baseline`/`follows the LIVE SL` — the chase-execute block, the adopt block header (`:2263`-area), and the reconcile spec-back get the one-line correction.
- `spec-reconcile-manual-sl-edits.md` / its spec-back: amendment noting rationale 4a's "follows the app's own chase" half is reverted by owner ruling.

## 4. §6 triage (rulings)

- **(a) Intermittent `Manual SL edit` cyan — ACCEPTED AS DESIGNED, document, no code.** Near-book manual moves can match a fresh commanded price (±0.25) and be classified as ours: not followed, no cyan. The moves that matter to the loss-cap are wide re-sets, which can never match a book-hugging commanded price and are always detected. Near-book moves are overwritten by the chase within ~333 ms regardless — following them would move the cap ≤ a tick, transiently. Add one sentence to the reconcile spec-back's known-properties.
- **(b) Manual-TP clears to 0 on placement — FIX, small, this stack.** Separate pre-existing defect; it also blocks the fill-reanchor manual-TP runtime path from ever being exercised. Bounded task for the implementer: locate the clear (there is a `manualTPval = 0D` write in the echo handler — suspect a one-shot-consume that fires before/at placement read, or a TextChanged side effect), report intended vs actual semantics, fix if it is a clear misfire, spec-back instead if the semantics are genuinely ambiguous. Do not redesign the manual-TP lifecycle.
- **(c) Post-cancel `Placed price = 0…` warning ×5 while in position — LEAVE.** After a nuclear cancel with an open position, the state it warns about (position open, no SL resting) is genuinely hazardous; the repetition is a feature, not spam. No change.

## 5. Owner runtime tests

The spec-back §7 plan is adopted verbatim (1–5), plus:

6. **Post-emergency cleanup regression:** after the cap fires, the position is flat, the resting SL/TP are cleaned up (existing #44-proven machinery), and no second market reduce fires.
7. **(b)-fix check:** enter a Manual TP, place — the placed TP uses the manual value and `txtManualTP` behaves per the fixed semantics; then a chased entry re-anchors TP per the manual override rules (this finally exercises the fill-reanchor manual path).

## 6. Bookkeeping

F1 status: baseline half **resolved by removal**; `placedStopLossPrice` half unchanged (accepted runaway trade-off). **Owner rulings 2026-07-08 (both prior-open items CLOSED):** the sl-chase-v2 §4 P1-pullback tightening is **ACCEPTED** (1-tick pullback is fine; the P3 hybrid chase is **DECLINED** — not needed); the implementer-flagged `IsCancelPending`-gates-emergency-during-cancel is **LEFT as the spec wrote it** (a nuclear cancel already resets `SLTriggered`/`emergencyBaseline`/latch — code-verified at `:3175`/`:3177`/`:3178` — so the gate is belt-and-suspenders, never a suppression of a cap you'd want). Consolidated coordinator review of the full unpushed stack is **DONE** (`541f185`, all 7 code commits APPROVED). **Still remaining before push: the hybrid (`25cac5f`) runtime session — NOT yet run** (the latch post-dates trade #67, so the #47–67 runs didn't exercise it); §5 items 1/3/4 are the critical confirms. Then push, then tie-in.
