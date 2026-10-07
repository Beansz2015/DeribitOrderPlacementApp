# Spec — reset the chase anchor when a placement dies, and make a refused bridge act say why

**Origin:** `D1` of `docs/impl-report-trade-slippage-fields.md` (a slippage-fields defect, pre-existing:
a rejected placement never resets the chase anchor). Confirmed and traced in
`docs/review-trade-slippage-fields.md` §4 and §4a. Owner: "go ahead and write the D1 spec", 2026-10-06.
**Status at writing:** open, no code written. Every fact below verified in code on 2026-10-06 at HEAD
`5f69134`.
**Scope:** `frmMainPageV2.vb` only, plus OrderCheck if a pure seam is extracted. **Order path → Opus, high.**
**Ships:** ON. No setting, no contract change, no change to any chase or abort *threshold*.

**One-line job:** when a placement never becomes a working order — the exchange rejects it, or the
position it belonged to is closed while the socket is down — clear the chase anchor so the next
placement starts fresh. And when a bridge act is refused before it is sent, report the real reason
instead of `timeout`.

---

## §0 — Decisions taken in this spec (owner may override before code)

| # | Decision |
|---|---|
| A1 | Reset on a **timely** rejection only. A rejection that arrives after the 5-s ack timeout stays log-only, exactly as today: a newer placement may already be live, and resetting would wipe *its* anchor. |
| A2 | The close-while-socket-down case is fixed in `CompletePositionClose`, **not** in `CancelOrderAsync`'s socket-down branch. `CancelOrderAsync` is also the Cancel All path; resetting there while the socket is down would wipe the anchor of a working order that still exists on the exchange. |
| A3 | The bridge fix covers **every** early return in `ExecuteOrderAsync`, not only the ATR guard. All of them leave the ack waiting today. |

## 🚫 Do-not-touch

| Thing | Why |
|---|---|
| The guard's threshold, `ChaseAbortReason`, `maxSlippageATRchecked` | This spec changes *when the anchor is cleared*, never *what trips the guard*. |
| The late-rejection branch's "LOG ONLY, no rollback" rule (`HandlePlacementResponse`, `entry.TimedOut`) | A1. |
| `CancelOrderAsync`'s socket-down early return | A2. |
| The rollback of `placedPrice` / `placedStopLossPrice` on rejection | Already correct; add beside it, do not reorder it. |
| The nine census symbols | `ResetOrderAttempt` is not one. If any census count changes, explain it. |

## §1 — The defect (verified)

- **Anchor lifecycle.** `IsATRSlippageExcessive` seeds `originalSignalPrice` (and `anchorAtr`) when it
  is 0. Only `ResetOrderAttempt` clears it. Its callers: the reduce-market echo, the guard's own trip,
  `CancelOrderAsync` (after its socket guard), `CancelWorkingEntryCoreAsync`.
- **Rejection path.** `HandlePlacementResponse` (`frmMainPageV2.vb:1990`), error arm: restores
  `placedPrice` and `placedStopLossPrice`, publishes feedback, logs `ORDER REJECTED`, completes the ack.
  **It never calls `ResetOrderAttempt`.** The guard already seeded the anchor before the send
  (`ExecuteOrderAsync`'s `BuyLimit` / `SellLimit` / `BuyNoSpread` / `SellNoSpread` arms).
- **Effect.** The next placement compares its price with the rejected attempt's anchor. If they differ
  by more than ATR × ATRSlip, in either direction, it is refused once ("slippage exceeds limit", plus an
  `AbortedEntries` row that looks like real slippage). The refusal resets the anchor; the attempt after
  goes through. Within the limit, the new order's chase budget is measured from the old price.
- **Close while the socket is down.** `CompletePositionClose` calls `CancelOrderAsync`, which returns
  before its reset when the socket is down. The closed trade's anchor survives into the next entry.
- **Bridge act.** `PlaceAutomatedOrder` (`:953`) pre-registers an ack, marshals `ExecuteOrderAsync`,
  then waits up to 5 s. Every early `Return` in `ExecuteOrderAsync` (socket down, bad amount, bad quote,
  the ATR guard, unsupported type) returns **before** `RegisterPendingPlacement` (`:3155`), which is
  called just before the send. Nothing completes the ack, so the act waits 5 s and returns `timeout`.
  The disposition reads `rejected: timeout` — a misleading reason — and the entry stays registered
  until the 60-s sweep.

## §2 — The change

### 2.1 Reset on a timely rejection

In `HandlePlacementResponse`'s error arm, after the `placedPrice` / `placedStopLossPrice` rollback and
before the ack completes, call `ResetOrderAttempt()`. Receive thread; `ResetOrderAttempt` writes plain
fields only. Not in the `entry.TimedOut` branch (A1).

### 2.2 Reset at position close

In `CompletePositionClose`, call `ResetOrderAttempt()` **unconditionally**, beside the existing
position-field clears (where `currentTradeSignalPrice` etc. are zeroed), so it runs whether or not
`CancelOrderAsync` reached its own reset. A double reset on the normal path is harmless.

### 2.3 A refused bridge act reports its real reason

- Add `Sent As Boolean` to `PendingPlacement`. `RegisterPendingPlacement` sets it — that is the one
  point every real send passes.
- Record the refusal reason where `ExecuteOrderAsync` returns early. At minimum the ATR guard
  (`"ATR slippage at placement"`, the same literal its `AbortedEntries` row uses). The other early
  returns may share one generic reason (`"not sent: <the log line's reason>"`) — implementer's choice,
  but every one must produce a reason, never an empty string.
- In `PlaceAutomatedOrder`, after the marshalled `ExecuteOrderAsync` completes: if the entry exists and
  is not `Sent`, remove it and return `Accepted = False` with the recorded reason. Do not wait the 5 s.
- The disposition then reads `rejected: ATR slippage at placement` (or the other reason). **Do not**
  change the disposition format or token set — only the reason text after `rejected: `.
- Threading: `ExecuteOrderAsync` runs on the UI thread (marshalled); `PlaceAutomatedOrder` resumes on
  its caller's thread. Use a field on the `PendingPlacement` entry (already shared across these threads
  via the `ConcurrentDictionary`), not a control.

### 2.4 Clear the staged signal tag on a never-sent act (added 2026-10-07)

From M9 F11 of the order-path audit (`docs/triage-adversarial-audit-2026-10.md` §2): when a bridge act is
never sent, `pendingSignalId` / `pendingSignalConfidence` stay staged and can attach to the next manual
trade. In the not-sent branch of §2.3, clear both, exactly as `CancelWorkingEntryCoreAsync` does.

## §3 — Acceptance

1. Gate passes; OrderCheck all pass; the nine censuses re-run and any change explained.
2. **OrderCheck, if a pure seam is worth extracting** (e.g. "what does `PlaceAutomatedOrder` return for
   a not-sent entry with reason X"): a fixture that **fails against today's code** (today it returns
   `timeout`). If no seam is clean, say so in the report rather than adding a fixture that cannot fail.
3. **Runtime, testnet, owner-driven** (a seat never places a trade). The implementer finds a reliable
   testnet rejection recipe (for example an amount the exchange refuses) and writes it in the report.
   Then, with ATRSlip ticked and set low (0.05):
   a. Place → `ORDER REJECTED` in the log.
   b. Wait for the price to move more than ATR × 0.05 from the rejected price, then place again →
      **the order is placed**, with no "slippage exceeds limit" line and no new `AbortedEntries` row.
      (Today step b is refused.)
4. **Bridge reason, owner-driven, optional:** only if the owner chooses to arm Live on testnet. Force an
   ATR refusal at placement → disposition `rejected: ATR slippage at placement`, logged within ~1 s,
   not after 5 s. If not run, the report says the bridge half is verified by code reading only.

## §4 — Not in scope

- An exact re-quote count (`SB3` of `docs/spec-trade-slippage-fields.md` §0b, an accepted residual).
- Any change to how a late (> 5 s) rejection is handled (A1).

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **Model:** Opus. **Effort:** high. Order path, receive thread, bridge act.
- Coordinator review: Opus, regardless of implementer.
