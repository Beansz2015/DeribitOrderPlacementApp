# Impl report — Triggered-SL chase v2 (`spec-sl-chase-v2.md`)

**Date:** 2026-07-08. **Implementer:** Fable (same seat as entry-chase v2 — this is that spec's §7-fenced leftover).
**Commits:** `b1b37e7` (commit 1 — target + 1-tick gate), `f0a2110` (commit 2 — gate hygiene + single-flight).
**Build:** 0 errors / 0 warnings after each commit (`MSBuild /t:Rebuild`, net9.0-windows).
**Pre-req verified before starting:** `origin/master = 44cd51e`, which contains all four entry-chase v2 commits (`9c3c351`/`bb77191`/`df4dfdc`/`7ac728d`) — i.e. entry-chase v2 is shipped, runtime-passed (owner trade #46), and **pushed**. Only `1e71249` (this spec) was local. Nothing else running in `HandleQuoteUpdates`. Working tree clean.

## Commit 1 — `b1b37e7`: best-non-crossing target + 1-tick gate (§2)

Decision block only (the `'Normal conditions operation` branch); everything else in the triggered-SL block unchanged.

| | Before | After |
|---|---|---|
| Long exit (resting SELL limit) | `bestAsk < placedStopLossPrice − MinPriceMovementThreshold($5)` ⇒ `newStopPrice = bestAsk` | `chaseTarget = bestBid + ChaseTickUSD`; fires when `chaseTarget < currentStopPrice` ⇒ `newStopPrice = chaseTarget` |
| Short exit (resting BUY limit) | `bestBid > placedStopLossPrice + $5` ⇒ `newStopPrice = bestBid` | `chaseTarget = bestAsk − ChaseTickUSD`; `chaseTarget > currentStopPrice` ⇒ `newStopPrice = chaseTarget` |
| Gate shape | $5 distance + join-own-side-top | 1-tick best-non-crossing (mirror of the entry chase) |

- `chaseTarget` is `Decimal?`; a missing quote side makes the lifted comparison false ⇒ no fire, no throw. `newStopPrice = chaseTarget` compiles under this project's **Option Strict Off** (implicit `Decimal?`→`Decimal`, same as the entry-chase call sites); the assignment is only reached when the comparison held, so the value is present.
- `newStopPrice` flows unchanged into **both** the normal path and the ≥50%-threshold `ForceStopLossUpdate` escalation — no edit there.
- **Byte-untouched (verified by diff grep):** the full-emergency block's deliberately-crossing `ForceStopLossUpdate(If(TradeMode, bestAsk, bestBid))` argument (`:1698`), and every internal of `UpdateStopLossForTriggeredStopLossOrder` (still the single send point, still `RecordCommandedSLPrice` at the send). No `HasHeadroom` added to this path — the entry/trailing chases reserve 4 precisely so this exit path spends to the floor; its existing `CanMakeRequest`/wait logic stays the only credit gate.
- Throttle unchanged: `MinStopLossUpdateInterval` (333 ms) on `lastStopLossUpdate`, `BackoffStopLossRetry` on failure. `lastEntryChaseUtc` deliberately NOT used here (the two chases must not starve each other).
- `MinPriceMovementThreshold` const **deleted**; grep confirms **zero** live references remain (only a one-line removal-note comment + the historical spec docs, which are accurate as-of-date and out of scope).
- **The `+/- 5 leeway` block header comment** (`:1647`) was corrected to drop the stale "+/- 5" phrasing — cosmetic, same commit.

**Why the owner saw ~1s reposition lag pre-v2:** the $5 leeway, not the 333 ms throttle. The SL didn't move until the tape ran a full $5 past the resting price. This commit replaces that with a 1-tick gate, so the exit now steps with the book at the throttle floor.

## Commit 2 — `f0a2110`: gate hygiene + dedicated single-flight (§3)

- **Block gate** (`:1659`): added `AndAlso (Not IsCancelPending())` — matches the entry/reduce block ordering (cancel check before any reposition work), so a chase edit can't race a nuclear cancel and burn a gray `already_closed` edit.
- **New field `isSLRepositioning As Integer`** placed beside `isRepositioning`, with a comment on why it is *separate* (the SL chase must never wedge against the entry/reduce chases through a shared flag).
- **Single-flight around the EXECUTE section only:** `If shouldUpdate AndAlso Interlocked.Exchange(isSLRepositioning, 1) = 0 Then`, flag released in a **`Finally`**. Rationale (spec §3): `lastStopLossUpdate` advances only on *success* (`:1750`), so while a send's `Await` is in flight a second quote tick can pass the 333 ms gate and dispatch a duplicate edit; the commanded set absorbs the echo but the edit is a wasted credit. The **full-emergency** check sits above this `If` and is **outside** the flag — the market-stop path is never blocked by an in-flight chase edit.
- **Structure deviation from the spec pseudocode (equivalent, flagged):** §3 sketches an outer `Try/Finally` wrapping the existing inner `Try/Catch` verbatim (a nested `Try`). I merged into a **single `Try/Catch/Finally`** — the execute *statements* and the `Catch`/backoff are byte-for-byte unchanged; only a `Finally` clause is added to the existing `Try`. Semantically identical (flag released on success, on caught exception, and on any non-caught throw), smaller and clearer diff for review of this hot block. If the coordinator prefers the literal nested form, it's a trivial re-wrap.

## §4 invariants — confirmed untouched

- **No new SL-edit path** ⇒ no new `RecordCommandedSLPrice`/reset call. **The SL-context reset sites are unchanged by this spec; no canonical-count edits here** (explicitly, as §6 requested). (Count note: it was 8 at this commit; the later TP-only `spec-fill-reanchor-fix.md` — landing on top before the combined runtime session — removes entry-chase v2's `ReanchorLegsAsync` SL edit and reverts the canonical count to 7. That reversal is that fix's doc change, not this one's.)
- Single send point (`UpdateStopLossForTriggeredStopLossOrder`) with its record-at-send: untouched.
- Synchronous reference advance (`placedStopLossPrice`/`emergencyBaseline = newStopPrice`): untouched (accepted F1 phantom-advance class, self-heals).
- Position-model sizing inside the send function, `reduce_only` placement-only, the three-way echo classification: all untouched (not in the diff).

## Consequence to flag for the owner (spec §4 + a new interaction)

1. **P1 pullback TIGHTENED in degree (spec §4, owner accepts at runtime test):** a manual post-trigger SL edit more than one tick behind the best non-crossing price is now pulled back on the next tick (≤ 333 ms), where the old $5 leeway left it alone within $5. This is the named P3-hybrid trigger condition — the runtime test §5.3 is the decision point; P3 is NOT implemented here.
2. **New interaction (mine to surface):** because `Not IsCancelPending()` was added to the *block* gate (per §3), it now also gates the **full-emergency market-stop**, which lives inside that gate. In practice this is benign and arguably correct — the emergency itself calls `CancelOrderAsync` + market-reduce, and a nuclear cancel already resets `SLTriggered = False`, so during a real cancel the block is skipped anyway. But it means "emergency during a pending cancel" is suppressed for the ≤4 s `cancelPending` window. If the owner wants the emergency to fire even mid-cancel, move the `Not IsCancelPending()` to guard only the normal-chase `If` instead of the block gate. Left as the spec wrote it; runtime tests §5.4/§5.5 exercise the neighborhood.

## Runtime test plan

Spec §5 as written (7 items). Watch especially: §5.1/§5.2 maker fill at `bid+0.5`/`ask−0.5` stepping with the book; §5.3 the tightened manual-edit pullback (the P3 decision); §5.4 emergency still fires (not blocked by the single-flight); §5.6 cadence ≈ 3 edits/s max, no `SL update rate limited` spam. No push until these pass.
