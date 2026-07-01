# Implementation Report — Order-state transition-race fix

**Spec:** `docs/spec-transition-race-fix.md`
**Branch:** `housekeeping-now` (started at the cross-thread-fix + reworded-warning tip). **Not pushed.**
**File touched:** `DeribitOrderPlacementApp/frmMainPageV2.vb` (only).
**Build:** `dotnet build` — **0 warnings, 0 errors** (baseline green before and after).

---

## 1. Approach

### `cancelPending` lifecycle (fixes symptoms #1 + #2)
A single boolean gate marks the window between "we sent a cancel" and "the exchange confirmed it / a fresh order re-established a clean context."

- **Fields** (near `placedPrice`):
  - `Private cancelPending As Boolean`
  - `Private cancelPendingSince As DateTime`
  - `Private ReadOnly cancelPendingTimeout As TimeSpan = TimeSpan.FromSeconds(4)`
- **Set:** `CancelOrderAsync` sets `cancelPending = True`, stamps `cancelPendingSince = DateTime.UtcNow`, and **nulls `CurrentOpenOrderId/CurrentTPOrderId/CurrentSLOrderId`** (alongside the existing `placedPrice = 0D` / `placedStopLossPrice = 0D` resets).
- **Cleared** at three points:
  1. **Exchange confirm** — the `orderState = "cancelled"` branch in `HandleOrderPositionUpdates` sets `cancelPending = False` (cancel_all cancels all resting legs, so a cancelled echo always arrives).
  2. **Fresh placement** — `ExecuteOrderAsync` and `StopLossForTrailingOrderAsync` set `cancelPending = False` at the point they seed `placedPrice` (a new order *is* a clean context).
  3. **Timeout fallback** — the hot-path gates read `cancelPending` through `IsCancelPending()`, which auto-clears it once `cancelPendingTimeout` (4s) elapses, so a missed confirmation can never wedge repositioning permanently.
- **Gates (read via `IsCancelPending()`):** the three reposition/edit blocks in `HandleQuoteUpdates` (entry A, trailing D, trailing-TP trigger) and the "placed price = 0 while an order context is active" warning all get `AndAlso Not IsCancelPending()`.
- **Echo suppression (reads raw `cancelPending`):** while pending, `HandleOrderPositionUpdates` ignores lagging `open` `EntryLimitOrder`/`EntryTrailingOrder` echoes and the whole `untriggered` child-leg branch, so no stale echo re-populates `placedPrice`/order IDs and re-arms the reposition.

### Single-writer discipline for `placedPrice` / `placedStopLossPrice` (fixes symptom #3, preserves recovery)
The exchange echo now **seeds only when the engine doesn't already own the price**:
- `open EntryLimitOrder` / `open EntryTrailingOrder`: `If placedPrice = 0D Then placedPrice = If(price, 0D)` (inside the `Not cancelPending` guard).
- `open StopLossOrder` (triggered SL): `If placedStopLossPrice = 0D AndAlso Not cancelPending Then placedStopLossPrice = If(price, 0D)`.

During active management `placedPrice`/`placedStopLossPrice` are `> 0` (the quote handler owns them), so the lagging echo can't reset them backward. On reconnect they're `0`, so recovery still seeds them. The **display** textbox is written together with the seed (so it never flickers to a lagging price mid-management; the quote handler is the sole display writer during repositioning).

The `untriggered StopLossOrder` field write keeps updating (not seed-only) so `placedStopLossPrice` tracks the latest pre-trigger SL price for a correct trigger handoff — but it is now inside the `Not cancelPending` guard.

---

## 2. Per-change summary (function · before → after)

| # | Location | Before | After |
|---|----------|--------|-------|
| 1 | fields (near `placedPrice`) | — | added `cancelPending`, `cancelPendingSince`, `cancelPendingTimeout` |
| 2 | new `IsCancelPending()` helper | — | returns `cancelPending`, self-clears after timeout |
| 3 | `CancelOrderAsync` | reset `placedPrice`/`placedStopLossPrice` only | + `cancelPending = True`, stamp time, null `CurrentOpenOrderId/TP/SL` |
| 4 | `HandleQuoteUpdates` — warning | fired whenever `placedPrice=0` & context set | + `AndAlso (Not IsCancelPending())` |
| 5 | `HandleQuoteUpdates` — entry reposition gate | `IsWebSocketConnected AndAlso (IDs set) AndAlso …` | + `AndAlso (Not IsCancelPending())` |
| 6 | `HandleQuoteUpdates` — trailing reposition gate | same shape | + `AndAlso (Not IsCancelPending())` |
| 7 | `HandleQuoteUpdates` — trailing-TP trigger gate | same shape | + `AndAlso (Not IsCancelPending())` |
| 8 | `HandleOrderPositionUpdates` — `open EntryLimitOrder` | `placedPrice = If(price,0D)` unconditional; IDs set | wrapped in `If Not cancelPending`; `placedPrice`/display seed-only-when-0 |
| 9 | `HandleOrderPositionUpdates` — `open EntryTrailingOrder` | same unconditional writes (+ redundant double `CurrentOpenOrderId`) | wrapped in `If Not cancelPending`; seed-only; removed the duplicate ID assignment |
| 10 | `HandleOrderPositionUpdates` — `open StopLossOrder` | `placedStopLossPrice = If(price,0D)` unconditional | `If placedStopLossPrice = 0D AndAlso Not cancelPending` seed-only (side-effects `SLTriggered`/`PositionSLOrderId`/log unchanged) |
| 11 | `HandleOrderPositionUpdates` — `untriggered` branch | processed always | `If cancelPending Then Return` at top of the `Me.Invoke` lambda |
| 12 | `HandleOrderPositionUpdates` — `cancelled` branch | label switch only | + `cancelPending = False` (cancel confirmed) |
| 13 | `ExecuteOrderAsync` (placement) | seeds `placedPrice` | + `cancelPending = False` |
| 14 | `StopLossForTrailingOrderAsync` (placement) | seeds `placedPrice` | + `cancelPending = False` |

---

## 3. How each symptom is closed / recovery preserved

- **#1 Misleading "placed price = 0…" warning** — gated on `Not IsCancelPending()`; during the cancel window (when `placedPrice = 0` is expected) it no longer fires.
- **#2 Repositions after "Cancelled all open orders"** — `CancelOrderAsync` nulls the IDs *and* sets `cancelPending`; the reposition gates require all IDs **and** `Not IsCancelPending()`; lagging `open`/`untriggered` echoes are ignored while pending, so they cannot re-populate `CurrentOpenOrderId` (the linchpin the gates require) or the child IDs. No `private/edit` is sent for the cancelled order.
- **#3 Duplicate `Order repositioned: X → Y`** — single-writer seed-only-when-0 means the lagging echo of the previous edit can no longer reset `placedPrice` back to `X` while the quote handler holds `Y`; the next tick sees the monotonic value and doesn't re-issue the same move. Same rule applied to the triggered-SL `placedStopLossPrice`.
- **Recovery preserved** — seed-only still seeds when the field is `0` (reconnect state), and the order-ID writes run whenever `Not cancelPending` (which is the case on a normal reconnect). So a fresh connect with an existing resting order still learns `placedPrice` + IDs from the exchange echo.

---

## 4. Build status / commits / deviations

- **Build:** 0 errors / 0 warnings after the change.
- **Commit:** single local commit on `housekeeping-now` (tightly-coupled state-machine change — a flag is only meaningful with all its set/clear/gate sites present; partial commits wouldn't function independently). Hash recorded at commit time. **Not pushed.**
- **Deviations from the spec's recommended approach:**
  - Cleared `cancelPending` at **placement** (`ExecuteOrderAsync`/`StopLossForTrailingOrderAsync`) in addition to the cancelled-echo/timeout the spec named. Rationale: the position-flat `size = 0` branch itself calls `CancelOrderAsync` (re-setting the flag), so "clean context re-established" is most reliably signalled by a fresh order; the cancelled-echo and timeout still cover the pure-cancel case.
  - Tied the echo's **display** write to the seed (rather than always mirroring the exchange) so the textbox can't flicker to a lagging price during active management. The spec explicitly allowed either; this avoids a display/field divergence.
  - `untriggered StopLossOrder` field write left as a live update (not seed-only) — the pre-trigger SL price legitimately changes as the entry repositions, and it must be current at the untriggered→triggered handoff. It is still gated by `Not cancelPending`.
  - Incidentally removed a redundant duplicate `CurrentOpenOrderId = orderId` in the `EntryTrailingOrder` case and one trailing-whitespace char.

---

## 5. Test results / steps (per §4)

> **Owner to run under the VS debugger on the test sub-account.** These are code-path expectations; not yet exercised live by the implementer.

- **Cancel mid-reposition** — after `Cancelled all open orders`: expect **no further `Order repositioned`**, **no `private/edit`** for the cancelled order, **no "placed price = 0…" warning** (gates + echo suppression hold until the cancelled echo / 4s timeout).
- **Rapid repositioning** — expect **no duplicate** `Order repositioned: X → Y`; each from-price equals the previous to-price (seed-only keeps `placedPrice` monotonic).
- **Reconnect with an open order** — expect the engine to recover `placedPrice`/IDs and reposition again (seed-only seeds from `0`; IDs set while `Not cancelPending`).
- **No cross-thread exceptions**; SL/emergency behaviour unchanged.

---

## 6. Confirmation

- **No cross-thread access reintroduced** — decisions still read engine fields; every control access stays inside `Me.Invoke`/`UiInvoke`. New reads are of the plain `cancelPending` boolean (atomic).
- **Not pushed** (owner pushes after live testing).
- **Build green** (0/0).
- **Order payloads/semantics unchanged** — no change to any `private/*` request; only decision-gating and echo-seeding logic changed.

### Noted for audit #10 (not fixed here — out of scope)
- After `cancelPending` clears, a very late `open` echo of the *old* cancelled order could still set `CurrentOpenOrderId` to a stale id for one tick before the new order's echoes arrive; a `private/edit` on a stale id is rejected by Deribit. Proper fix is single-source-of-truth order-context ownership (audit #10).
- The `open StopLossOrder` triggered case still sets `SLTriggered`/`PositionSLOrderId` unconditionally (not gated on `cancelPending`) — deliberately, to avoid changing position-context handling; revisit under #10.
