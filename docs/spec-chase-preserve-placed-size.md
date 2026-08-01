# Micro-spec — the entry chase must preserve the PLACED size (N2b)

**Status: OWNER-TICKED 2026-08-01 — NEXT, awaiting a fresh Opus-HIGH seat. BLOCKS N2** (`spec-risk-sized-bridge-trades.md`)
and, less visibly, the session-policy `size_mult` feature that has been silently broken since it
shipped. Origin: `review-risk-sized-bridge-trades.md` §2/§3 — acceptance 3b failed with the risk
size placed correctly and then reverted. **Implementer: Opus HIGH, fresh conversation, own pass**
(entry-chase path). Ships ON — this is a defect fix, no knob.

## §1 — The defect

`PlaceAutomatedOrder(sizeUsdOverride:=N)` places an order of N. The size is then **consumed and
forgotten**: `ExecuteOrderAsync` does `If sizeUsdOverride > 0D Then amount = sizeUsdOverride` and
stores it nowhere. Both entry-chase edits re-derive the amount from the Amount-box mirror:

```vb
' UpdateEntryOrderOnlyAsync      :4275
' UpdateLimitOrderWithOTOCOAsync :4193
Dim amount As Decimal = orderAmountVal
```

So the first reposition **rewrites the resting order to the Amount box size**. The app enters at
top-of-book, so almost every bridge entry is chased at least once.

Runtime-observed (review §2), box 10, computed 310:

```
Buy limit order placed For 310 at 63074.
Order repositioned: $63074.00 → $63076.00
Position entered: LONG 10 @ $63075.96
Reduce-only MARKET sell 10  order sent.        <- exchange-derived: the position was 10
```

**This is pre-existing, not an N2 regression.** It has applied to session-policy `size_mult` since
that shipped; it was invisible only because everything ran at live-at-min-size, where the box is 10
and `EffectiveSizeUsd` clamps every reduction back up to 10, so no divergence existed to revert.

## §2 — The fix

Retain the size actually placed, and have the chase re-send **that**, not the box.

- A single field — suggest `placedOrderSizeUsd As Decimal = 0D` — set in `ExecuteOrderAsync` at the
  same place the payload's amount is finalised. `0` keeps its established meaning: *nothing
  overridden, use the box*.
- **AMENDED 2026-08-01 (owner veto) — the set is value-conditional, and the write is not:**
  ```vb
  placedOrderSizeUsd = If(sizeUsdOverride > 0D, amount, 0D)
  ```
  **Retain only a size that came from an OVERRIDE.** A manual placement writes `0`, so the chase
  falls back to the box and **editing the Amount box while an order rests still resizes that resting
  order** — a control the owner uses deliberately, because the chase can walk the entry closer to the
  TP and they resize in that moment. An earlier unconditional `= amount` removed it and was vetoed.
  The **write stays unconditional**, which is what preserves the property that a manual placement is
  itself a clear: a stale size from a previous act can never survive into it. Only the value changes.
  A resting *bridge* entry deliberately does NOT follow a box edit — that is the fix; cancel it to
  intervene.
- Both chase edits take `If placedOrderSizeUsd > 0D Then amount = placedOrderSizeUsd` in place of
  the bare `orderAmountVal` read. **Same 0-means-the-box convention as `sizeUsdOverride`** — do not
  invent a second sentinel.
- **Lifecycle is the load-bearing part.** The field must be cleared wherever a working entry ceases
  to exist, or a later manual placement inherits a stale bridge size — which would be a *worse*
  defect than the one being fixed. At minimum: the fill/`OpenPositions` transition, both cancel
  teardowns (scoped and nuclear), and the placement seeds that already reset sibling state. Follow
  `cancelPending` / `emergencyFired` — enumerate their reset sites and match them, rather than
  guessing a set.

- **AMENDED 2026-08-01 (spec-back D2, UPHELD): the id-778 restore must seed the field.** The
  `Case "EntryLimitOrder", "EntryTrailingOrder"` branch of `HandleOpenOrdersSnapshot` adopts the
  working entry's id and price but not its **amount**, so a restart with a live risk-sized entry
  leaves the field `0` and the first post-restart reposition reintroduces this very defect. Seed it
  there from the order's `amount`, under the **same single-writer rule `placedPrice` already uses**
  (`If placedOrderSizeUsd = 0D Then …`) so a snapshot can never overwrite a live in-process value.
  Seeding a trailing entry's box-sized amount is harmless — it equals what the chase would use.

**Explicitly out of scope:** `ExecuteOrderAsync`'s own override semantics, the six pre-placement
gates, `ApplyRiskBasedSize`, the N2 seam, and every SL/TP/reduce path. This is the entry-chase
amount source and the field's lifecycle, nothing else.

**Explicitly RULED OUT (spec-back D1, REJECTED):** do not touch `ReanchorTPToFillAsync`. It cannot
run on a bridge entry — the re-anchor staging gate requires `manualTPval <= 0D` and a bridge act
always sets a manual TP — and on the manual path where it does run, the position size IS the Amount
box, so its `orderAmountVal` read is already correct. See `review-chase-preserve-placed-size.md` D1.

## §3 — Also fix: `Position entered:` prints the wrong number

`:3245` (and its trail sibling `:3287`) print `orderAmountVal` — the box mirror — not the size
filled. Even with §2 fixed, that line lies whenever the placed size differs from the box, and it is
the line a runtime acceptance reads. Print the size actually in force. **Say in the impl report
which value you chose** (retained placed size vs the position model's own size) and why.

## §4 — Acceptance

1. Gate per commit; fixtures counted.
2. **THE acceptance — a re-run of N2's failed 3b.** Harness, TESTNET, bridge Live, N2 checkbox ON,
   session policy OFF, **Amount box 10**, `risk_per_trade_usd = 1`, payload
   `entry 63000 / stop 62800` ⇒ computed **310**. Let the entry be chased at least once (confirm an
   `Order repositioned:` line appears), then flatten. **The reduce must report 310, not 10.** The
   reduce is exchange-derived and is the authority — do not accept the log's placement line as proof.
3. **Regression — manual placements are unaffected:** a manual Limit BUY with the box at 10, chased
   at least once, still results in a position of 10. Here `placedOrderSizeUsd` must be **0** (the
   amended value-conditional set).
3b. **AMENDED — the vetoed behaviour must still work.** Place a manual Limit BUY with the box at 10,
   and **while it rests, type a different Amount** (e.g. 20) and let it be chased at least once. The
   resting order must **resize to 20**, and the resulting position must be 20. This is the control
   the owner uses when the chase shortens the entry→TP distance, and it is the leg that proves the
   set is value-conditional rather than unconditional.
4. **Regression — the stale-size case, which is the risk this fix introduces:** a bridge act at a
   non-box size, then flatten, then a MANUAL placement at the box size. The manual order must be the
   box size. This is what proves the lifecycle clears the field.
5. **Session-policy path:** with a `0.5` policy line and a box above the clamp, a chased bridge entry
   must hold the reduced size, not the box. This is the pre-existing half of the defect and it wants
   its own observation.
6. Censuses unchanged: `emergencyFired` 10 · `IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 ·
   `RecordCommandedSLPrice` 3 · `slUpdateFailures = 0` 1 · `isPlacingOrder` 13 ·
   `lastPlacementAdmittedUtc` 13. New (AMENDED): `placedOrderSizeUsd` = 1 decl + 1 set +
   **2** chase reads (not 3 — the third working-entry edit site, `UpdateStopLossForTrailingOrder`,
   provably never sees a non-zero value; spec-back D3) + **1 restore seed** (D2) + the clear sites +
   2 display fallback reads from §3.
7. **Acceptance 5 needs the Amount box ABOVE the clamp** (e.g. 40). At box 10 a `0.5` mult clamps
   back up to 10 and the divergence vanishes — that clamp is exactly what hid this defect for weeks.

## §5 — Note for the reviewer

The latch-style question here is **the clear set, not the set site**. A missed clear means a stale
bridge size leaks onto a later manual order — real money, wrong size, silent. Enumerate the reset
sites of `cancelPending` and `emergencyFired` and prove the new field matches them, the way the SF
review had to prove the `Finally` could not leak. And do not accept a placement log line as
evidence of position size: N2's 3b failed precisely because those two disagreed.
