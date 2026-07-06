# Spec — Entry-chase v2 (best-non-crossing target, time throttle, entry-only chase)

**Date:** 2026-07-06 (Fable coordinator; design ratified by the owner in conversation — including the "default ON" ruling in §4).
**Value:** better fills on every manual (and later bridge) entry, at a *lower* request rate than today. No market orders anywhere — maker-only by construction + `post_only` safety net.
**Recommended implementer:** Opus at **high**. This rewrites the two entry-chase blocks in `HandleQuoteUpdates` (receive hot path) and splits the bracket-edit function. The §9-fix conversation already has this region hot — handing it this spec next is ideal.
**Pre-req:** the SL-reconciliation (`968b26d` + the in-flight §9 runtime-test fixes) landed, runtime-passed, and **pushed**. Do not run in parallel with other work in `HandleQuoteUpdates`.
**Read first:** `docs/spec-back-session-2026-07-04.md` §8/§10 (binding invariants; the triggered-SL chase/echo/discriminator region is OUT OF SCOPE here and must not change), `docs/HANDOVER-2.md` §4.
**Ground rules:** standing — build 0/0 per commit, one commit per numbered change, local commits only (owner pushes), scope discipline, impl report `docs/impl-report-entry-chase-v2.md`.

> ⚠️ Locate by **symbol**; line numbers below are hints at `fc7bb6c` and will have drifted after the §9 fixes.

---

## 0. Current behavior (verified 2026-07-06) and why it changes

- **Entry chase** (`HandleQuoteUpdates`, gate `bestBid > (placedPrice + 3)` long / `bestAsk < (placedPrice - 3)` short, `:1478/:1519`): distance-throttled. The order can rest up to $2.99 behind top of book indefinitely (never fills unless price returns), yet in a fast move the gate fires every $3 step anyway — a distance gate neither pins us to the top nor bounds the request rate.
- **Each reposition costs 3 matching-engine edits** — `UpdateLimitOrderWithOTOCOAsync` (`:3115`) moves the whole bracket (main + TP + SL re-derived from the new price). The trailing-entry variant (`UpdateStopLossForTrailingOrder`, `:3375`) costs 2 (main + SL; no TP pre-fill).
- **Trailing-entry chase** (`:1705/:1732`): same $3 distance gate.
- **Placements** already send `post_only: True` + `reject_post_only: False` (`:2841` etc.) — entries can never take. **Edits:** as of `45a8da5` (2026-07-06, landed after this spec was first written) ALL 8 `private/edit` payloads carry the same pair, including the trigger-branch (SL) edits. Rejection there is unlikely — the OTOCO **stop_limit** leg has carried `post_only` at placement since the original code (`:2841`), so Deribit accepts the flag on trigger orders — but the owner's runtime test #1 in `spec-back-post-only-edits.md` §4 confirms it before this spec is implemented.
- The **NoSpread** placement branches already price one tick inside the opposite side (`BestAskPrice - 0.5` / `BestBidPrice + 0.5`, `:2664/:2703`) — but the chase re-anchors to own-side top (`bestBid`/`bestAsk`), so that edge survives only until the first reposition.
- Deribit constraint: matching-engine requests are credit-limited per sub-account, tiered by 7-day volume (base tier ≈ 5/s sustained, burst ≈ 20). The app fetches the account's real numbers at connect (`InitializeRateLimits`) — **tune the constants below against the connect-time limiter values, not folklore.**
- Deribit matching rule: an edit that changes price forfeits queue priority. Chasing faster than ~200–300 ms pays credits to repeatedly stand at the back of the queue — the throttle floor below is deliberate.

**v2 in one line:** chase to the *most aggressive non-crossing price* on a *time* throttle, move only the entry leg per tick, re-anchor the OTOCO legs on a drift bound and once at fill.

## 1. New engine fields / constants (one place, top of `frmMainPageV2.vb` near the other engine fields)

```vb
' --- Entry-chase v2 (spec-entry-chase-v2.md) ---
Private Const ChaseTickUSD As Decimal = 0.5D            ' BTC-PERPETUAL tick (matches the NoSpread branches)
Private Const EntryChaseMinIntervalMs As Integer = 350  ' floor between chase edits (entry-only mode default);
                                                        ' use 700-1000 if EntryOnlyChase is reverted to False
Private Const EntryOnlyChase As Boolean = True          ' OWNER RULING: default ON. One-line revert switch.
Private lastEntryChaseUtc As DateTime = DateTime.MinValue ' UTC stamp of the last chase edit (entry + trailing share it)
Private legAnchorPrice As Decimal = 0D                  ' entry price the OTOCO legs' geometry is currently based on
Private legReanchorDriftMax As Decimal = 0D             ' per-placement bound, computed at placement (see §4)
```

`lastEntryChaseUtc`/`legAnchorPrice`/`legReanchorDriftMax` are receive-thread-owned engine fields (same discipline as `placedPrice`). Reset `legAnchorPrice = 0D` at the same places `placedPrice` context dies (nuclear cancel, scoped cancel, close completion, new placement seeds it) — **do NOT add new SL-context resets; these are order-context fields, not SL context.**

## 2. Commit 1 — target price + time throttle + `post_only` on the main edit

**Target (both entry-chase blocks, long/short):**

```vb
' LONG:  most aggressive non-crossing bid. While a buy rests at placedPrice, ask > placedPrice
'        always (a crossing ask would have filled us), so chaseTarget >= placedPrice; equality = already best.
Dim chaseTarget As Decimal = bestAsk - ChaseTickUSD
' SHORT: chaseTarget = bestBid + ChaseTickUSD, condition inverted.
```

When the spread is 1 tick this equals joining own-side top (today's behavior, minus the $3 lag); when the spread is ≥ 2 ticks it improves to a new level = **front of queue** — exactly the fast-tape moments that matter. `chaseTarget > placedPrice` (long) replaces the `+ 3` gate and is inherently one-tick hysteresis (prices move in ticks).

**Throttle (added to the same condition):**

```vb
AndAlso (DateTime.UtcNow - lastEntryChaseUtc).TotalMilliseconds >= EntryChaseMinIntervalMs
```

Stamp `lastEntryChaseUtc = DateTime.UtcNow` immediately after a successful edit send (same place the code advances `placedPrice`). The entry block and the trailing-entry block share the stamp (they share `isRepositioning` single-flight already — only one can fire per context anyway).

**Gate rewrite details (both blocks):**
- Keep everything else in the gate chain as-is: `IsWebSocketConnected`, `Not IsCancelPending()`, the order-context null checks, the `Interlocked` single-flight, the limiter checks, the ATR-slippage cancel branch.
- While rewriting these exact lines, fix the non-short-circuit `And` on the four slippage sub-gates (`maxSlippageATRchecked And IsATRSlippageExcessive(...)` → `AndAlso`) — this supersedes the four-gate half of `spec-medium-housekeeping.md` addendum item 8 (its six pre-placement gates remain there). **Leave the trailing-LONG `bestAsk` argument as-is** (`:1708` passes `bestAsk` where the entry block uses `bestBid`) — flagged for an owner decision, do not silently change.
- Slippage guard input stays the raw own-side quote (`bestBid`/`bestAsk`) exactly as today — the guard measures market drift, not our limit price.

**`post_only` on edits — SUPERSEDED by `45a8da5` (landed 2026-07-06, before this spec's implementation):** all 8 edit payloads already carry `post_only: True` + `reject_post_only: False`, so commit 1 adds no flags. Two obligations remain:
- **Preserve the flags** through any restructuring of `SendRateLimitedUpdate`/the chase blocks. The self-heal property holds: the chase target is non-crossing against our own quote snapshot; if a stale quote makes Deribit re-price, the next tick re-compares `placedPrice` against the *live* book and trues up.
- **Contingency (low probability — see §0):** if the owner's runtime probe shows Deribit rejecting `post_only` on the trigger-branch edit payloads (loud red API ERROR per edit id — one bracket reposition reveals it), strip the two flags from the trigger-branch payloads only; the maker guarantee only matters for book-resting limit edits. Also coordinate with the possible `reduce_only` micro-commit (`spec-back-post-only-edits.md` §5) — if it's needed, it lands BEFORE this spec and touches the same payload lines.

## 3. The OTOCO mechanics (answering the design question; read before commit 2)

The bracket is a real Deribit OTOCO: the TP/SL legs are created **at placement** (`otoco_config` is placement-time only — legs cannot be attached to an already-resting order, and never need to be). The legs exist from the start as linked orders with their own `order_id`s, and they are **independently editable pre-fill** — that is precisely what today's 3-edit chase does. Cancel-the-primary still cancels the children (Deribit-verified 2026-07-03); fill-the-primary still activates the OCO. Commit 2 does not change the bracket's structure at all — it only stops *moving* the legs on every tick.

## 4. Commit 2 — entry-only chase + deferred leg re-anchor (**default ON**, owner ruling)

**Per-placement setup:** wherever the bracket placement seeds order context, also set `legAnchorPrice = <placement entry price>` and

```vb
' Bound the geometry error so a chased entry can never overrun its own TP:
' fill <= anchor + driftMax <= anchor + tpOffset/2 < anchor + tpOffset = TP. Same logic for the trigger side.
legReanchorDriftMax = Math.Min(takeProfitOffset, triggerDistance) / 2D
If legReanchorDriftMax <= 0D Then legReanchorDriftMax = 10D  ' offsets unset (manual-targets mode) - modest default
```

**Chase-time decision (entry block):** when the chase fires,

- if `EntryOnlyChase AndAlso Math.Abs(chaseTarget - legAnchorPrice) < legReanchorDriftMax` → **new slim path** `UpdateEntryOrderOnlyAsync(chaseTarget)`: ONE `SendRateLimitedUpdate("main", CurrentOpenOrderId, chaseTarget, orderAmountVal)` with the same limiter checks/amount guard as the bracket path. **It must NOT touch `StopLossTriggerOriginal`, `emergencyBaseline`, or the commanded-SL set** — the SL leg did not move, so the recorded trigger stays truthful to the actually-resting order. (This is why the emergency bookkeeping stays consistent throughout: wider-than-ideal geometry until re-anchor, but never a false record.)
- else → today's full `UpdateLimitOrderWithOTOCOAsync(chaseTarget)` (3 edits; keeps its existing `StopLossTriggerOriginal`/`emergencyBaseline`/`ResetCommandedSLPrices` writes — the SL genuinely moves here), then `legAnchorPrice = chaseTarget`.
- Note: when `manualTPval > 0`/`manualSLval > 0` the bracket path already re-sends the *same* absolute prices (wasted edits today) — the entry-only path is a pure win in manual-targets mode; no special-casing needed, the drift bound just never forces a pointless re-anchor for the TP (trigger distance still counts).

**Fill re-anchor:** in the filled-`EntryLimitOrder` echo case (where `Position entered:` logs), if `legAnchorPrice <> 0D AndAlso fillPrice <> legAnchorPrice`, call a new `ReanchorLegsAsync(fillPrice)`:

- Recompute `newTPprice`/`newTrigSLprice`/`newSLprice` from `fillPrice` with the same formulas as `UpdateLimitOrderWithOTOCOAsync` (honoring `manualTPval`/`manualSLval` overrides).
- Two edits: `SendRateLimitedUpdate("takeprofit", CurrentTPOrderId, ...)` + `SendRateLimitedUpdate("stoploss", CurrentSLOrderId, ..., newTrigSLprice)`, with limiter checks.
- Then, in this order: `StopLossTriggerOriginal = newTrigSLprice` · `emergencyBaseline = 0D` · `ResetCommandedSLPrices()` · **`RecordCommandedSLPrice(newSLprice)`**. The record is mandatory (HANDOVER-2 §3 invariant: every new programmatic SL edit records) — it covers the rare race where the SL triggers in the moments between fill and re-anchor, so this edit's echo on the triggered path is recognized as ours and not misread as a manual move. Reset-then-record, never the reverse.
- `legAnchorPrice = fillPrice`.
- **Docs consequence (do in the same commit):** this makes `ReanchorLegsAsync` the **8th** SL-context reset site. Update the two canonical counts: `spec-back-session-2026-07-04.md` §10 bullet 4 and `HANDOVER-2.md` §3 state-delta line ("7 SL-context reset sites" → 8, naming this one).

**Trailing-entry chase** gets the same treatment with its 2-edit path: entry-only edit within the drift bound, full `UpdateStopLossForTrailingOrder` (which moves the SL and already updates the trigger bookkeeping) beyond it or at fill. Implementer verifies the trailing fill-echo anchor point and reports it.

## 5. Commit 3 (optional — cut if the owner wants minimum scope) — reduce-chase target + throttle

The reduce-limit chase (`spec-reduce-reposition.md` block, directly below the entry block) keeps a reduce-only exit at own-side top with 1 edit per reposition (`SendReduceRepositionEdit`, id 223349). Apply the same two upgrades: target = `bestBid + ChaseTickUSD` when `reduceOrderIsBuy = False` (sell exit; inverted for buy exits — direction from `reduceOrderIsBuy`, NEVER `TradeMode`), and the `EntryChaseMinIntervalMs` throttle (separate stamp field `lastReduceChaseUtc` — the reduce chase must not be starved by entry-chase stamps). No leg logic (no bracket). `post_only` on its edit payload same as the main leg.

## 6. Credit-floor hygiene (part of commit 1)

Add to `DeribitRateLimiter` a small `Public Function HasHeadroom(requests As Integer) As Boolean` (credits ≥ `requests × _costPerRequest`, under the usual lock with refill). Chase gates require `rateLimiter.HasHeadroom(4)` (entry-only mode: 1 for the edit + 3 reserve so a nuclear cancel/emergency path is never starved; bracket mode effectively needs 3 + reserve — 4 is a floor, not exact accounting). The existing `CanMakeRequest` checks stay where they are.

## 7. What this spec must NOT touch

- The **triggered-SL chase/emergency block** and the **`StopLossOrder` echo discriminator** (`UpdateStopLossForTriggeredStopLossOrder`, the commanded-price set except the two mandated calls in §4) — the §9 machinery is out of scope.
- The scoped/nuclear cancel asymmetry, `pendingPlacements`/`HandlePlacementResponse`, the reduce-reposition context rules (beyond §5's price/throttle), the placement payloads.
- Placement-time pricing (NoSpread etc.) — unchanged; v2 makes the chase *preserve* the NoSpread edge instead of eroding it.

## 8. Acceptance / owner runtime test plan (test sub-account, VS debugger)

1. **Quiet tape, BuyLimit:** after the first chase the order rests at `ask − 0.5` (exchange UI); it holds join-or-improve as the book drifts; the fill (when it comes) is **maker** (check fees on the trade). No "Skipping order update" spam.
2. **Fast move:** "Order repositioned" lines are spaced ≥ `EntryChaseMinIntervalMs`; credits never exhaust; the ATR-slippage cancel still fires at the cap with `chkMaxSlippageATR` ON.
3. **Entry-only mechanics:** during a chase within the drift bound, exchange UI shows TP/SL legs NOT moving while the entry moves; drift past the bound → one bracket re-anchor (all three move); on fill → TP/SL re-anchor to fill-price geometry (verify TP = fill+offset, trigger = fill−trigger on the exchange).
4. **Manual-targets mode** (`txtManualTP`/`txtManualSL` set): TP/SL stay at the absolute prices throughout; entry chases alone.
5. **§9 regression:** after a fill, trigger the SL and confirm the reconciliation behaviors are unchanged (chase-before-emergency, `Manual SL edit` only on genuine manual edits — including none from the fill re-anchor).
6. **Cancel regression:** scoped cancel (slippage) and nuclear cancel behave as before mid-chase.
7. (If commit 3) reduce-limit rests at improved price, throttled, maker fill.

## 9. Impl report

`docs/impl-report-entry-chase-v2.md`: per-commit before→after, the trailing fill-anchor point chosen (§4), confirmation the two doc-count updates landed (§4), `HasHeadroom` semantics, and any deviation (contract: this spec loses to the invariants docs where they conflict).
