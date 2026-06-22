# Code Audit — DeribitOrderPlacementApp

**Date:** 2026-06-22
**Scope:** End-to-end audit of all hand-written logic. Excluded: `frmMainPage.*` (obsolete backup form), auto-generated `*.Designer.vb`.
**Stack:** .NET 9 WinForms, VB.NET, Deribit BTC-PERPETUAL via WebSocket, Skender.Stock.Indicators, SQLite, Newtonsoft.Json.

**Verdict:** The app works and reflects real iteration, but it carries one urgent security problem and several genuine correctness bugs hiding under `Option Strict Off`. The dominant structural issue: `frmMainPageV2` is a ~4,300-line god-form, and the two forms are wired together through each other's UI controls.

Severity legend: 🔴 Critical · 🟠 High · 🟡 Medium · ⚪ Housekeeping

---

## 🔴 CRITICAL

### 1. Live Deribit API secret hardcoded and published to public GitHub
`frmMainPageV2.vb:32-33`

```vb
Private Const ClientId As String = "<redacted>"
Private Const ClientSecret As String = "<redacted>"
```

Confirmed present in git history since the first commit (`f5597fe`), on a **public** repo.

**Context (per owner):** the key points to a *throwaway test sub-account* that will be deleted once the app is production-ready, so live-fund exposure is accepted for now. History rewrite is therefore not worth it.

**Action taken (this session):** credentials externalized to a gitignored `secrets.json`, loaded at runtime via `AppSecrets.vb`; hardcoded constants removed from source. The pattern ensures the **production** account is never committed. Rotate/delete the throwaway sub-account at production cutover.

**Status:** ✅ Code fix implemented (see `AppSecrets.vb`, `secrets.example.json`). Compile + test pending.

---

## 🟠 HIGH — correctness bugs (Spec: `spec-high-bugs.md`)

### 2. Circuit breaker comparison is broken
`FrmIndicators.vb:333`
```vb
If frmMainPageV2.USDPublicSession < Decimal.TryParse(_autoTradeSettings.txtCircuitBreaker.Text, CircuitBreak) Then
```
`Decimal.TryParse` returns a **Boolean**. Under `Option Strict Off`, `True`→`-1`, so this evaluates as `USDPublicSession < -1` — the breaker trips at -$1 session PnL, not the configured limit. The parsed value (`CircuitBreak`) is never used in the comparison.

### 3. Two concurrent `ReceiveAsync` on the same socket
`frmMainPageV2.vb:385` (RefreshWebSocketAuthentication) races `:574` (ReceiveWebSocketMessagesAsync). `ClientWebSocket` forbids two outstanding receives → the refresh throws, is swallowed, and the refresh token likely never rotates. Route the id-3 auth response through the normal handler dispatch; the refresh function should only *send*.

### 4. Cross-thread UI access on reconnect
`frmMainPageV2.vb:360-362` — `AuthorizeWebSocketConnection` sets `lblStatus`/`btnConnect` directly. Fine on first connect (UI thread), but the reconnect path runs on a thread-pool thread → cross-thread exception that masquerades as auth failure. Wrap in `BeginInvoke`.

### 5. No reentrancy guard on the quote handler
`frmMainPageV2.vb:1111` — `HandleQuoteUpdates` (Async Sub, one per tick) can overlap on fast markets and double-send `private/edit` reposition bursts. Add single-flight guard around the reposition block (same pattern as `isReconnecting`).

### 6. Hot-path `Decimal.Parse` on textbox text, no `TryParse`
`HandleQuoteUpdates` (`:1361`, 1383, 1386, 1397, …). A blank/mid-edit field throws, aborting the tick's `Try` — which can skip the **stop-loss repositioning** later in the same handler. A blank field can silently disable SL trailing. Parse once with `TryParse`, bail cleanly; stop treating UI text as source of truth per tick.

### 7. Backtest corrupts live-indicator state
`FrmIndicators.vb:1752-1758` — `BacktestSignals` calls the live `Update*` subs, which hold `Static` locals and mutate shared `score`/`startupFired`. A backtest walks that state forward and leaves live signals wrong. **Backtesting is being retained**, so port indicators to pure functions (quotes in → signal out, no `Static`, no shared mutable score).

---

## 🟡 MEDIUM — structure & resilience (Spec: `spec-medium-housekeeping.md`)

### 8. Dead reconnect subsystem
`WebSocketCalls` (`:228`) and `ReconnectWebSocket` (`:654`) only call each other — unreachable from live paths (`ConnectToWebSocketDirectly` + `HandleWebSocketDisconnect`). ~80 lines of orphaned, duplicated logic. Delete both.

### 9. Every inbound message JSON-parsed 7×
`frmMainPageV2.vb:589-603` — eight handlers each call `JObject.Parse(response)`. Parse once, dispatch on `channel`/`id`/`method`. Removes silent empty-catch parsing too.

### 10. Forms coupled through each other's UI controls (key for the rewrite)
- Auto-trade fires via `mainForm.btnBuy.PerformClick()` + `btnLimit.PerformClick()` (`FrmIndicators.vb:541-546`) while `frmMainPageV2.ExecuteAutomatedOrder` (`:153`) exists for exactly this and is **unused**.
- Writes into `frmMainPageV2.txtTakeProfit/.txtTrigger` (`FrmIndicators.vb:1841`), reads `txtPlacedPrice`, `lblATR.Text`, `USDPublicSession`.
- Mixes the passed instance (`CType(_host, frmMainPageV2)`) with the **VB default instance** (`frmMainPageV2.USDPublicSession`) — same object only by luck of startup.

Define a clean interface on `frmMainPageV2` (`PlaceAutomatedOrder(side, tp, sl, size)`, `ReadOnly ATR`, `ReadOnly SessionPnLUSD`, `IsConnected`, `CanTrade`). New analysis form talks to that — never controls, never the default instance. Highest-leverage cleanup for the FrmIndicators/AutoTradeSettings rewrite.

### 11. Reconnect race in the indicators stream
`FrmIndicators.vb:166-197` + `:84` — `OnPollElapsed` (5s) can spawn another `ConnectAndStream` during the connect window, leaking the old socket and starting a second receive loop. Add a connecting/connected guard. Also: the 5s poll re-fetches chart data even while the live subscription is healthy — redundant.

### 12. `Option Strict Off` project-wide
Only Designer files carry `Option Strict On`. Off is what let #2 compile. Do **not** flip globally now (hundreds of errors). Turn it on for all *new* files (rewritten forms) and treat as the long-term target for legacy files.

> **Convention adopted (housekeeping H7, 2026-06-22):** every *new* `.vb` file begins with `Option Strict On` / `Option Explicit On` (as `AppSecrets.vb` does). Existing files are intentionally left unflipped — turning Option Strict on across legacy code surfaces hundreds of conversion errors and is out of scope.

---

## ⚪ HOUSEKEEPING (fold into Spec B)

- **`throw` followed by dead code**, repeated: `:297-300`, `:333-335`, `:345-347`, `:390-392`, `:399-401`. Pick log *or* throw; delete the rest.
- **Live button opens obsolete form:** `btnChangeForm_Click → frmMainPage.Show()` (`:3472`). Remove with the dead form.
- **Dead `Else` branches:** `btnReduceLimit_Click`/`btnReduceMarket_Click` `Else` is unreachable for a Boolean (`:3698`, `:3736`).
- **Empty stub:** `ProcessEstimationData` (`:3378`); id-890 margin branch is vestigial.
- **Duplicate functions:** `GetAccountSummaryLimits` (`:3036`) vs `GetAccountSummaryLimitsWithTimeout` (`:856`).
- **Heartbeat enabled twice** per connect (`:258` and `:339`).
- **Dead field** `keepAliveTimer` (`:25`).
- **`.Wait()` on UI thread** in `FrmIndicators_FormClosing` (`:1646`) — deadlock risk on close.
- **Magic numbers** that belong in config: taker fee `0.0005` (`:1007`), ±3/±5/0.5 leeways, 4320-bar cap, score divisor 21.
- **`Async Sub` handlers** — convert to `Async Function … As Task` and await in the dispatcher (with #9).

---

## Notes on parts being replaced

- **FrmIndicators scoring validates the replacement plan:** it scores `GetMacd(6,13,5)` (`:843`) and `GetStoch(8,3,3)` (`:1231`) — both on the owner's *rejected list* — and **double-counts** (RSI↔Stoch and EMA↔VWAP mutual confirmation bumps), another rejected pattern. New engine should drop MACD/Stoch and make confirmations one-directional.
- **Preserve from FrmIndicators:** backtest harness (after de-`Static`), UTC+8 restricted-time window, ATR→TP/SL paste, cooldown/circuit-breaker settings contract, OHLC stream/dedupe loop.
- **TradeDatabase / TradeRecord / TradeAnalytics** are the cleanest code here. Notes: `TradeRecord` slippage fields (`RequoteCount`, `SlippageATR`, …) are never persisted (add columns or drop fields); `TradeAnalytics.GetTradeStatistics` is never called (history view recomputes inline).

---

## Recommended order

1. Rotate/externalize the secret — **done in code**; rotate account at production.
2. Spec A (HIGH #2-#7) — live-trading safety. Run first.
3. Spec B (MEDIUM #8-#12 + Housekeeping) — cleanup. Run after Spec A is committed (shares `frmMainPageV2.vb`).
4. Clean form interface (#10) before the FrmIndicators/AutoTradeSettings rewrite.
5. Port indicators as pure functions (#7) with the new engine; drop rejected indicators + double-counting.
