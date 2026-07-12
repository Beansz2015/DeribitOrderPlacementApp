# Spec — Triggered-SL chase v2 (best-non-crossing target for the exit chase)

**Date:** 2026-07-08 (owner-requested: extend the chase-v2 treatment to the triggered stop-loss repositioning).
**Value:** the exit chase fills faster at better passive prices ⇒ fewer M.SL emergency **market** closes ⇒ fewer taker fees on stopped-out trades. Completes the chase-v2 program: after this, **every** repositioning loop in the app runs the same target + throttle discipline.
**Recommended implementer:** the same Fable/Opus-high conversation that implemented `spec-entry-chase-v2.md` — this is the deliberate leftover of that spec's §7 fence.
**Pre-req:** entry-chase v2 shipped + runtime-passed + **pushed** (done 2026-07-08; base ≥ `44cd51e`). Nothing else may run in `HandleQuoteUpdates` in parallel.
**Read first:** `spec-entry-chase-v2.md` (the pattern + its review section), `spec-back-session-2026-07-07.md` §1/§4 (the three-way echo classification, F1, the single-flight backlog item), `spec-back-session-2026-07-04.md` §10.
**Ground rules:** standing — build 0/0 per commit, local commits only (owner pushes), scope discipline, impl report `docs/impl-report-sl-chase-v2.md`.

> ⚠️ Locate by **symbol**; line hints are at `44cd51e`.

---

## 0. Coverage audit — every repositioning path in the app (owner question 2026-07-08)

Quote-driven repositioning **loops** in `HandleQuoteUpdates`, in block order:

| Loop | Orders it moves | v2 status |
|---|---|---|
| Entry chase (2 blocks, long/short) | manual/API entry OTOCO bracket (`btnLimit`/`btnNoSpread`/`PlaceAutomatedOrder`) | ✅ v2 (`9c3c351`/`bb77191`) |
| Reduce-limit chase | resting reduce-only limit (`btnReduceLimit`/`FlattenPositionAsync`) | ✅ v2 (`df4dfdc`) |
| **Triggered-SL chase** (`:1649–1751`) | the OTOCO SL **limit** after its trigger fired (the exit racing the market) | ❌ old mechanics — **THIS SPEC** |
| Trailing-entry chase (2 blocks) | `btnTrail`'s pre-fill 2-leg bracket | ✅ v2 (`9c3c351`/`bb77191`) |

Not loops (for completeness — no conversion needed): the trailing take-profit stop (`TrailingStopLossOrderAsync`, id 30) is placed with Deribit-native `trigger_offset` — the **exchange** trails it, the app has no reposition loop for it; the manual edit buttons (`btnEditTPPrice`/`btnEditSLPrice`) are one-shot edits; the M.SL emergency is a deliberate **market** path (the only intended taker with `btnMarket`/`btnReduceMarket`) and is untouched here.

## 1. Current triggered-SL chase mechanics (verified at `44cd51e`)

Block gate `:1649`: `IsWebSocketConnected AndAlso SLTriggered AndAlso PositionSLOrderId IsNot Nothing` — note: **no** `IsCancelPending` gate, **no** single-flight (the 07-07 session backlog item). Inside, throttled by the pre-existing `MinStopLossUpdateInterval = 333` ms on `lastStopLossUpdate` (stamped **on success** `:1728`; failures go through `BackoffStopLossRetry`):

1. Full emergency (`priceMovement ≥ emergencyThreshold` from `emgBaseline`, `marketStopLossChecked`) → `ForceStopLossUpdate(If(TradeMode, bestAsk, bestBid))` + early `Return` (`:1686-1691`).
2. Normal decision (`:1697-1709`): long → `bestAsk < placedStopLossPrice − MinPriceMovementThreshold($5)` ⇒ `newStopPrice = bestAsk`; short → `bestBid > placedStopLossPrice + $5` ⇒ `newStopPrice = bestBid`. **Distance gate + join-own-side-top — the same pre-v2 shape the entry chase had.**
3. Execute (`:1712-1740`): `priceMovement ≥ 50%` of threshold → `ForceStopLossUpdate(newStopPrice)` else `UpdateStopLossForTriggeredStopLossOrder(newStopPrice)` (the single send point — records to the commanded set); then advance `placedStopLossPrice`/`emergencyBaseline = newStopPrice`, stamp, clear backoff, orange log.

The exit semantics: for a long the SL is a resting **sell limit** — while it rests, `bestBid < placedStopLossPrice` (a bid at our price would have filled us). For a short it's a resting **buy limit** — while it rests, `bestAsk > placedStopLossPrice`. Same one-directional adverse-chase structure as the entry.

## 2. Commit 1 — best-non-crossing target, 1-tick gate

Swap the decision block (`:1697-1709`) to the chase-v2 pattern; **everything else in the block stays** (throttle, backoff, ≥50% escalation, reference advance, logs):

```vb
Dim shouldUpdate As Boolean = False
Dim newStopPrice As Decimal = 0D
If TradeMode Then
    ' Long exit = resting SELL limit: most aggressive non-crossing ask = one tick above the bid.
    ' While the sell rests, bestBid < placedStopLossPrice, so chaseTarget <= placedStopLossPrice;
    ' "<" is inherent one-tick hysteresis (same argument as the entry chase, mirrored).
    Dim chaseTarget As Decimal? = bestBid + ChaseTickUSD
    If chaseTarget < currentStopPrice Then
        newStopPrice = chaseTarget
        shouldUpdate = True
    End If
Else
    ' Short exit = resting BUY limit: most aggressive non-crossing bid = one tick below the ask.
    Dim chaseTarget As Decimal? = bestAsk - ChaseTickUSD
    If chaseTarget > currentStopPrice Then
        newStopPrice = chaseTarget
        shouldUpdate = True
    End If
End If
```

- The lifted `Decimal?` comparison is null-safe (missing quote side ⇒ no fire), same as the entry blocks.
- `newStopPrice = chaseTarget` feeds **both** the normal path and the ≥50% `ForceStopLossUpdate` escalation automatically — no other change there. The **full**-emergency block (`:1686-1691`) keeps its deliberately crossing `If(TradeMode, bestAsk, bestBid)` argument — do not touch it.
- The throttle stays the existing `MinStopLossUpdateInterval` (333 ms) on `lastStopLossUpdate` with its `BackoffStopLossRetry` machinery — do NOT introduce `lastEntryChaseUtc` here; the SL chase must never be starved by entry-chase stamps (and vice versa).
- **Do NOT add `HasHeadroom` to this path.** The entry/trailing chases reserve 4 requests precisely so this exit path (and the cancels/emergency) can spend down to the floor. The existing `CanMakeRequest`/wait logic inside `UpdateStopLossForTriggeredStopLossOrder` stays as the only credit gate.
- `MinPriceMovementThreshold` becomes unreferenced after this rewrite (its only two uses are `:1699/:1705`) — delete the const, grep-confirm zero references.
- Payload flags: the SL edit already carries `post_only`/`reject_post_only` (`45a8da5`, runtime-proven). Note the known cosmetic interaction gets slightly more likely with a 1-tick target: a stale-quote reprice returns an uncommanded price ⇒ one benign `Manual SL edit` line + self-correct (documented in `spec-back-post-only-edits.md` §1). Accepted.

## 3. Commit 2 — gate hygiene + single-flight (closes the 07-07 backlog item)

- Add `AndAlso (Not IsCancelPending())` to the block gate (`:1649`) — the documented gate-ordering invariant (cancel check BEFORE any reposition work); today a chase edit can race a nuclear cancel and burn a wasted (gray `already_closed`) edit.
- New `Private isSLRepositioning As Integer` + single-flight around the **execute** section only:

```vb
If shouldUpdate AndAlso Interlocked.Exchange(isSLRepositioning, 1) = 0 Then
    Try
        ' ...existing execute section verbatim (escalation choice, send, reference advance, stamp, log, Catch/backoff)...
    Finally
        Interlocked.Exchange(isSLRepositioning, 0)
    End Try
End If
```

Why here and not around the whole block: `lastStopLossUpdate` advances only on **success** (`:1728`), so while a send's `Await` is in flight a second quote tick can pass the 333 ms gate and dispatch a duplicate edit (the receive loop fires `HandleQuoteUpdates` fire-and-forget). The commanded set absorbs the duplicate's echo, but the edit itself is a wasted credit. The **full-emergency** check stays OUTSIDE the flag — the market-stop path must never be blocked by an in-flight chase edit. A dedicated flag (NOT `isRepositioning`) so the SL chase never wedges against the entry/reduce chases.

## 4. What must NOT change (invariants)

- `UpdateStopLossForTriggeredStopLossOrder` stays the single send point with `RecordCommandedSLPrice` at the send — this spec adds **no** new SL-edit path, so no new record/reset sites; the 8 SL-context reset sites are untouched.
- The synchronous reference advance (`placedStopLossPrice`/`emergencyBaseline = newStopPrice`) stays — accepted F1 class (phantom-advance on a swallowed rejection, self-heals).
- Sizing stays position-model-based inside the send function (`c6a893c`); `reduce_only` stays placement-only (preserved across edits — trade-#45 probe).
- The three-way echo classification (flip adopt / manual / ignore) is untouched.
- **P1 semantics are unchanged in kind but TIGHTENED in degree — flag for the owner:** today a manual SL edit within $5 of top-of-book is left alone; after this spec, any manual placement more than one tick behind the best non-crossing price is pulled back on the next tick (≤ 333 ms). This is exactly the "P1 pullback annoying in live use" condition previously named as the P3-hybrid trigger. The owner accepts this consciously at the runtime test, or the deferred P3 policy becomes the remedy — do NOT implement P3 here. **[Owner ruling 2026-07-08: ACCEPTED — the 1-tick pullback is fine; P3 hybrid DECLINED. §5.3 is now a behavior-confirm, not a decision.]**

## 5. Owner runtime test plan (test sub-account)

1. **Long exit chase:** small long, let the SL trigger; on the exchange UI the SL limit rests at `bid + 0.5` and steps DOWN with the market at ≥ 333 ms spacing; maker fill on exit (check fees); no spurious `Manual SL edit` during an app-only chase; no backward `placedStopLossPrice` blips.
2. **Short mirror:** SL rests at `ask − 0.5`, chases up, maker fill.
3. **Manual-edit pullback (the §4 flag):** post-trigger, manually move the SL away from the book → `Manual SL edit` (cyan, followed) → next tick pulls it back to best-non-crossing. **Confirm this tightened pullback behaves as expected live** — the accept/reject call is already made (owner 2026-07-08: ACCEPTED; P3 DECLINED).
4. **Emergency regression:** with M.SL armed and a small threshold, let the market run — the ≥50% escalation still logs `CRITICAL SL repositioned`, and the full-threshold market close still fires (it must never be blocked by the new single-flight).
5. **Cancel regression:** nuclear cancel mid-chase — no SL edit fires after `cancelPending` is set; at most one gray `already_closed`.
6. **Cadence/limits:** fast move produces ~3 edits/s max, no `SL update rate limited` spam, no credit exhaustion (entry chase idle — position is open, so only this chase + possibly the reduce chase run).
7. **Untouched-loop regression:** one entry chase + one reduce chase pass unchanged (they were not modified here).

## 6. Impl report

`docs/impl-report-sl-chase-v2.md`: per-commit before→after; grep evidence `MinPriceMovementThreshold` is gone; confirmation the full-emergency block and `UpdateStopLossForTriggeredStopLossOrder` internals are byte-untouched; the single-flight placement rationale; any deviation (invariant docs win). No canonical-count changes are expected — say so explicitly if true.
