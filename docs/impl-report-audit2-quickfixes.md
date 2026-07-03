# Implementation report — Audit-2 quick fixes

**Spec:** `docs/spec-audit2-quickfixes.md`
**Branch:** `housekeeping-now`, base `e5735ef`, commits `39d7e82..0e57f2a` (fixes 1–6) + `638eb46` (fix 7)
**Implementer:** Fable 5 (medium effort)
**Date:** 2026-07-02
**Build gate:** `dotnet build DeribitOrderPlacementApp.sln` run after every fix, before every commit — **0 errors / 0 warnings all six times.**
**Push status:** local only, nothing pushed (per workflow).

Line numbers below are post-change positions in `DeribitOrderPlacementApp\frmMainPageV2.vb` at `0e57f2a` unless marked "pre".

---

## Fix 1 — real entry price to DB (`39d7e82`)

- Added at the top of the `If size = 0 Then` position-closed branch (now :2026-2028), before any flag resets and before `Await CancelOrderAsync()`:
  ```vb
  ' Audit2 F1: snapshot the entry price BEFORE CancelOrderAsync zeroes
  ' placedPrice, so the DB record gets the real entry, not 0.
  Dim entryPriceAtClose As Decimal = placedPrice
  ```
- `RecordCompletedTrade(` first argument: `placedPrice` → `entryPriceAtClose` (now :2079). No other argument changed.
- `CancelOrderAsync()` not moved; no writes to `placedPrice` added. Read-only snapshot exactly as specified.

## Fix 2 — checkbox gates internal emergency market-stop (`48ae3ec`)

- In `UpdateStopLossForTriggeredStopLossOrder`, prepended `marketStopLossChecked AndAlso ` to both emergency conditions (now :2822 `If`, :2828 `ElseIf`). Used the backing field, not `chkMarketStopLoss.Checked` (receive thread).
- Extended the comment block above with the two spec-provided F3 comment lines.
- The existing `marketStopLossChecked` gate in `HandleQuoteUpdates` was not touched (verified: both new occurrences of `marketStopLossChecked AndAlso marketStopThreshold` are in this function only).

## Fix 3 — reconnect on graceful server close (`8f79894`)

- Close-frame branch in `ReceiveWebSocketMessagesAsync` (now :645-651): message reworded to `"Server closed connection - scheduling reconnect"`, added `reconnectNeeded = True`, added the two spec comment lines. `Exit While` unchanged.
- Verified before committing that the bottom-of-function reconnect block does check `If Not isClosing` before firing `HandleWebSocketDisconnect()` (:702-709), as the new comment claims.

## Fix 4 — edit buttons: SL id condition + missing Returns (`53789e8`)

- `btnEditSLPrice_Click` (now :4023-4030): condition `ElseIf PositionTPOrderId IsNot Nothing` → `ElseIf PositionSLOrderId IsNot Nothing`; added `Return ' Audit2 F4: don't send an edit with a null order_id` after the not-found log.
- `btnEditTPPrice_Click` (now :4073-4080): id fields already correct; added the same `Return` line after the not-found log.

## Fix 5 — generic JSON-RPC error logger (`56248d2`)

- Added `HandleUnhandledJsonRpcError` and `RequestNameForId` verbatim from the spec, directly after `HandleRateLimitError` (:877-926). Includes the `"""error"""` string fast-path, the null-safe `messageId.HasValue AndAlso` skip for ids 3/999/777/890, the 10028 skip, and the fully-qualified `Newtonsoft.Json.Formatting.None`.
- Wired into `ReceiveWebSocketMessagesAsync` immediately after `HandleMarginEstimationResponse(response)` (:676-677). Grep-verified it appears exactly once in the dispatch.
- No engine state touched — logging only, as scoped.

## Fix 6 — market-reduce fill price (`0e57f2a`)

- `Case "ReduceMarketOrder"` in the `filled` branch (now :1922-1928): replaced the two `AppendColoredText` lines with the spec's version — reads `order.SelectToken("average_price")` from the echo (the `For Each order In orders` loop variable at :1676 is in scope), falls back to `newPricePublic` when > 0, else `"?"`; second line neutralized to "P/L not tracked for market reduces - check order history."
- `LogTradeDecision(...)`, `OpenPositions = True`, and `ResetOrderAttempt()` untouched. No control access added.

## Fix 7 — scratch closes log and record (`638eb46`)

Line numbers in this section are post-change positions at `638eb46`; sections above remain stamped at `0e57f2a` (fix 7 shifts everything after :2071 down by +10).

- **Close-message block** (`HandleOrderPositionUpdates`, `size = 0` branch): added the spec's `ElseIf label4DB IsNot Nothing` scratch branch (now :2073-2080) between the existing loss branch and its `End If` — two yellow `AppendColoredText` lines (`Position executed at {ExecPrice}.` / `Scratch close: P/L ≈ $0.00.`) plus the four spec comment lines. No `LogTradeDecision` call, per spec (it has no scratch case and would append an empty line to `AutoTradeLog.txt`).
- **DB gate** (now :2084-2090): `If PorLAmt > 0 Then` → `If label4DB IsNot Nothing Then`, with the three spec comment lines above it. `RecordCompletedTrade` argument list unchanged (still `entryPriceAtClose` first, from fix 1).
- Verified before editing that the spec's design assumption holds: `label4DB` is a per-message local, declared `Dim label4DB As String = Nothing` at :1667 inside the `user.changes` handler, and assigned only in the P/L-computing fill cases (`TakeLimitProfit`, `StopLossOrder`, `TrailingStopLoss`, `ReduceLimitOrder` — :1852-1910). `ReduceMarketOrder` never sets it, so market reduces stay unlogged-here and unrecorded, as designed. The new gate is a strict superset of `PorLAmt > 0` (every branch that sets `PorLAmt` also sets `label4DB` in the same block).
- `ReduceMarketOrder` case, `LogTradeDecision`, `CancelOrderAsync`, flags: untouched. New lines touch no controls (receive thread; `AppendColoredText` self-marshals).
- Build after change: **0 errors / 0 warnings.** Deviations: none — spec text applied verbatim.

---

## Deviations from spec

None. All six changes are the spec text applied verbatim; the only judgment calls were anchor matching, and every anchor matched the file exactly at the stated locations.

---

## Grep assertions (spec §8)

All pass at `0e57f2a`:

- `marketStopLossChecked AndAlso` present in both emergency branches (:2822, :2828) and nowhere else new.
- Both edit-button `Else` branches end in `Return` (:4029, :4079).
- `RecordCompletedTrade(` in `HandleOrderPositionUpdates` no longer passes `placedPrice` (passes `entryPriceAtClose`, :2079). The other `RecordCompletedTrade` hits (:3388, :3410) are the function definition and the DB-layer call — untouched.
- `HandleUnhandledJsonRpcError(response)` appears exactly once in the receive dispatch (:677).

Owner runtime tests (spec §8 items 1-5) remain outstanding — nothing pushed until those pass.

---

## Suspicious things noticed nearby, deliberately NOT touched (scope discipline)

1. **`HandleRateLimitError` uses `If errorCode = 10028`** (:861) — a nullable compared with `=`. Under VB three-valued logic `Nothing = 10028` evaluates to `Nothing` → treated as False, so behavior happens to be correct here, but it's the exact pattern the id-guard invariant bans (`HasValue AndAlso` is the house style). Cosmetic risk only today.
2. **`btnEditSLPrice_Click`/`btnEditTPPrice_Click` take `amount` from `txtAmount`** (:4064, :4013) — if the user changed the amount textbox mid-trade, an edit resizes the TP/SL order away from the position size. Same stale-input family as the F1 known limitation (`orderAmountVal`/`TradeMode`), which the spec already declares out of scope.
3. **`Decimal.Parse` on raw textbox text** in both edit handlers (:4010-4012, :4060-4062) — blank/garbage input throws; it's caught by the handler's outer `Try` and logged, but as an exception message rather than a friendly validation line.
4. **Fix 6's `LogTradeDecision("Exit Position - Market Order Loss", 0, 0)`** still hardcodes "Loss" in the autotrade file log (:1924) — spec explicitly leaves this as-is; noting it so the wording asymmetry with the new neutral UI log line is a known, deliberate leftover.
5. **Parse-per-handler dispatch** — every received message is now offered to 10 handlers, most of which `JObject.Parse` independently (audit #9). Fix 5's fast-path keeps its own cost off the hot path, but the underlying pattern stands, deferred to the parse-once dispatcher work as planned.
6. **(Fix 7) Split-message closes still record nothing** — `label4DB`, `PorLAmt`, `PorL`, and `ExecPrice` are all per-message locals. If the computing fill echo and the `size = 0` position update ever arrive in *separate* `user.changes` messages, `label4DB` is `Nothing` in the closing message and the trade is neither logged as a close nor recorded — exactly the limitation the old `PorLAmt > 0` gate had. Fix 7 doesn't widen this; noting it because the new gate inherits it.
7. **(Fix 7) `_indicators.lastAutoTradeTime = DateTime.Now`** (:2093) runs on *every* `size = 0` pass — including market reduces and scratch closes — regardless of whether anything was recorded. Pre-existing, untouched.
8. **(Fix 7) Scratch rows show WIN/LOSS with $0.00** — `PorL` carries whatever the computing branch chose (initialized `True`), so a scratch records `IsProfit` semi-arbitrarily. Known cosmetic, declared acceptable in spec §6c; no logic added.
