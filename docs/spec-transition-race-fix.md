# Implementation Spec — Order-state transition races (post-cancel repositions + duplicate repositions)

**Severity:** 🟡 Medium (correctness/hygiene; no direct safety impact — redundant edits are rejected by Deribit). Pre-existing; became visible once the cross-thread fix cleared the storm.
**Source:** live testing 2026-06-23 of `docs/spec-cross-thread-fix.md`. This is a focused precursor to audit **#10** (engine ⇄ controls decoupling / single source of truth).
**Project:** DeribitOrderPlacementApp — .NET 9 WinForms, VB.NET, Deribit BTC-PERPETUAL. `Option Strict Off` on legacy files (don't flip).
**Start point:** current `housekeeping-now` tip (has the cross-thread fix + the reworded warning). Build green.

> ⚠️ Locate code by function name + the quoted snippets; line numbers drift.

---

## 1. The bug (root cause)

After the cross-thread fix, the engine decides on backing fields. But **two writers** update the decision state for the *entry* order, and one of them lags:

- **Quote handler (authoritative, forward):** `HandleQuoteUpdates` reposition sets `placedPrice = bestBid/bestAsk` (Blocks A/D) and placement sets it in `ExecuteOrderAsync`/`StopLossForTrailingOrderAsync`.
- **Exchange echo (lagging):** `HandleOrderPositionUpdates`, on an `open` `EntryLimitOrder`/`EntryTrailingOrder` `user.changes` message, does `placedPrice = If(price, 0D)` and `CurrentOpenOrderId = orderId` (the `open`-branch cases). These echoes are Deribit confirming an edit you've *already moved past*.

`CancelOrderAsync` resets `placedPrice = 0` but does **not** null `CurrentOpenOrderId/CurrentTPOrderId/CurrentSLOrderId` (those are only nulled later, in `HandleOrderPositionUpdates`' position branch). Even nulling them wouldn't be enough on its own, because a lagging `open` echo re-sets them.

### The three observed symptoms
1. **Misleading warning** — already reworded (stopgap). After a slippage/manual cancel, `placedPrice = 0` while `CurrentOpenOrderId` is still set → the "placed price = 0 while an order context is active" line fires. It's expected/transient today; this spec removes the underlying cause so it stops firing.
2. **Repositions after "Cancelled all open orders"** — lagging `open` echoes re-populate `placedPrice` (and `CurrentOpenOrderId`) after the cancel, so the reposition block fires `private/edit` on the just-cancelled order. Deribit rejects them (the code logs "Order repositioned" without checking the response). Bounded (~1–2s until the cancel confirms) but wrong.
3. **Duplicate `Order repositioned: X → Y`** — the quote handler advances `placedPrice` to `Y`; the lagging echo of the *previous* edit resets it back to `X`; the next tick re-repositions the same move.

## 2. Required outcome

1. **No reposition/edit is sent for a cancelled order.** After `CancelOrderAsync`, the reposition blocks must not fire until a clean order context is re-established.
2. **The lagging exchange echo must not move `placedPrice` backward** while the quote handler is actively managing the order (no duplicate repositions to the same target).
3. **Recovery preserved:** on a fresh connect/reconnect with an *existing* open order, the engine must still learn `placedPrice` + order IDs from the exchange echo (that's the legitimate recovery path — don't break it).
4. **No cross-thread regression:** keep the field/marshal discipline from the cross-thread fix — decisions read fields, all control access stays marshalled. Same order payloads/semantics otherwise.

## 3. Recommended approach

**(a) A `cancelPending` flag (fixes #1 + #2).**
- Add `Private cancelPending As Boolean`. Set it `True` in `CancelOrderAsync` (alongside the existing `placedPrice = 0` reset), and **null `CurrentOpenOrderId/CurrentTPOrderId/CurrentSLOrderId`** there too.
- Gate the reposition/edit blocks in `HandleQuoteUpdates` (entry A, trailing D, trailing-TP trigger) on `AndAlso Not cancelPending` — so nothing edits a cancelling order.
- While `cancelPending`, `HandleOrderPositionUpdates` must **ignore stale `open` echoes** (don't re-populate `placedPrice`/IDs from them).
- Clear `cancelPending` when the cancel is confirmed — the position-flat / `size = 0` branch and/or the `cancelled` order-state branch. Add a **timeout fallback** (e.g. clear after ~3–5s) so a missed confirmation can't wedge repositioning permanently.
- Also gate the "placed price = 0 while an order context is active" warning on `AndAlso Not cancelPending` (it's expected during a cancel).

**(b) Single-writer discipline for `placedPrice` (fixes #3, preserves recovery).**
- Change the echo writes at the `open` `EntryLimitOrder`/`EntryTrailingOrder` cases to **seed only when the engine doesn't already own the price**: `If placedPrice = 0D AndAlso Not cancelPending Then placedPrice = If(price, 0D)`. During active management `placedPrice > 0`, so the echo won't overwrite it (no backward reset); on reconnect `placedPrice = 0`, so recovery still works.
- The echo may still update the **display** (`txtPlacedPrice` via `UiInvoke`) if you want the textbox to mirror the exchange — but the **decision field** must follow the single-writer rule above.
- **Audit `placedStopLossPrice` for the same pattern** — if `HandleOrderPositionUpdates` echoes the SL price into the field on `open`/`untriggered` `StopLossOrder` cases, apply the same "seed-only-when-0 / respect cancelPending" rule so triggered-SL repositioning can't get the same backward-reset.

## 4. Acceptance / test plan (owner, test sub-account, under the VS debugger)

- **Cancel mid-reposition** (let a limit order chase until the ATR-slippage cancel fires, or hit Cancel manually): after `Cancelled all open orders`, **no further `Order repositioned` lines**, **no `private/edit` sent** for the cancelled order, and **no "placed price = 0…" warning**.
- **Rapid repositioning** in a moving market: **no duplicate** `Order repositioned: X → Y` lines — each from-price equals the previous to-price (monotonic).
- **Reconnect with an open order** (drop the socket while a limit order rests): after reconnect the engine recovers `placedPrice`/IDs and can reposition again (recovery not broken).
- **No cross-thread exceptions**; SL/emergency behaviour unchanged; build green (0 errors) per commit.

## 5. Ground rules

1. Implement directly; local commits per logical change referencing this fix. **Do NOT push** (owner pushes after testing).
2. Build gate: 0 errors after each commit.
3. Keep the cross-thread discipline: no control reads/writes on the receive thread outside `UiInvoke`/`Me.Invoke`; decisions read fields.
4. Don't broaden into the full #10 rework — this is the focused transition-race fix. Note anything else for #10.

## 6. Implementation report (REQUIRED — after implementation, for review)

1. **Approach:** the `cancelPending` lifecycle (set/clear points + timeout) and the single-writer change for `placedPrice` (and `placedStopLossPrice` if applicable).
2. **Per-change summary:** function + before/after for each edit (reposition gates, echo writes, CancelOrderAsync, cancel-confirm clear point).
3. **How each symptom is closed** (#1/#2/#3) and **how recovery is preserved** (the reconnect path).
4. **Build status, commits (local hashes), deviations.**
5. **Test results/steps** per §4.
6. **Confirmation:** no cross-thread access reintroduced; not pushed; build green; order payloads/semantics unchanged.

---

**Suggested model/effort for the implementer:** **Opus 4.8, high effort.** It's live order-state-machine logic — the cancel-pending lifecycle and echo suppression are subtle, and the recovery path must not break. More contained than the cross-thread fix, but same "money-adjacent, get it right" character.
