# Spec-back — HIGH-severity bugs (#2–#7)

**Re:** `docs/spec-high-bugs.md` (read against `docs/CODE_AUDIT.md`).
**Status:** awaiting owner approval before any code is written.
**Build target:** `dotnet build DeribitOrderPlacementApp.sln`, 0 errors after each fix.

Line numbers below are where I actually found the code today, located by function name + snippet (not the stale numbers in the audit).

---

## 1. Restated understanding (code located + confirmed)

**#2 — Circuit breaker comparison** — `FrmIndicators.vb:333`, inside `ProcessAutomatedSignal`:
```vb
If frmMainPageV2.USDPublicSession < Decimal.TryParse(_autoTradeSettings.txtCircuitBreaker.Text, CircuitBreak) Then
```
`Decimal.TryParse` returns `Boolean`. Under `Option Strict Off` the `Boolean` is coerced to numeric for the `<` comparison: `True → -1`, `False → 0`. So the breaker actually evaluates `USDPublicSession < -1` (when the text parses) — it trips at roughly -$1 session PnL, and `CircuitBreak` (the parsed value, `Private CircuitBreak As Decimal`, line 636) is never used in the comparison. Confirmed.

`USDPublicSession` (`frmMainPageV2.vb:59`, `Public … As Decimal`) is set in `HandleBalanceUpdates` (`:1079`) as `indexPrice × (equity − balance)` — i.e. **session PnL in USD, negative when in a loss.** That sign drives decision (3a) below.

**#3 — Two concurrent `ReceiveAsync`** — `RefreshWebSocketAuthentication` (`frmMainPageV2.vb:374`) sends the id-3 refresh payload and then does its **own** `webSocketClient.ReceiveAsync` (`:392`) while `ReceiveWebSocketMessagesAsync` (`:572`) is already looping on receive (`:581`). `ClientWebSocket` forbids two outstanding receives → the inline receive throws, gets swallowed by `MonitorAuthentication`'s catch (`:654`, "Error refreshing token"), and the real id-3 response is consumed by the main loop, which has no handler for it. Net effect: the refresh token never rotates. Confirmed. The inline `ReceiveAsync` in `AuthorizeWebSocketConnection` (`:333`) is **safe** — it runs during initial connect, before the receive loop starts (`ConnectToWebSocketDirectly` starts the loop at `:567`, after auth at `:551`).

**#4 — Cross-thread UI on reconnect** — `AuthorizeWebSocketConnection` (`:367–369`) sets `lblStatus.ForeColor`, `btnConnect.Text`, `btnConnect.BackColor` directly. First connect is on the UI thread; the reconnect path (`HandleWebSocketDisconnect → ConnectToWebSocketDirectly → AuthorizeWebSocketConnection`) runs on a thread-pool thread (`Task.Run`, `:478`) → cross-thread `InvalidOperationException` that surfaces as a spurious auth failure. The rest of `ConnectToWebSocketDirectly` already marshals its own UI writes via `BeginInvoke` (`:559`), so the pattern is established. Confirmed.

**#5 — No reentrancy guard on the quote handler** — `HandleQuoteUpdates` (`:1118`, `Async Sub`) is invoked **un-awaited** once per tick from the receive loop (`:598`). On fast ticks, tick N+1 enters while tick N is awaiting `UpdateLimitOrderWithOTOCOAsync` / `UpdateStopLossForTrailingOrder`, double-firing `private/edit` reposition bursts. Confirmed. The handler has five sections I'll refer to by letter:
- **A** entry-order OTOCO reposition (`:1146–1216`)
- **B** triggered-SL emergency reposition (`:1219–1299`) — already self-throttled by `MinStopLossUpdateInterval` (333 ms)
- **C** trailing-order reposition (`:1304–1356`)
- **D** trailing-TP trigger → place trailing SL (`:1362–1388`)
- **E** PnL label update (`:1390–1423`)

**#6 — Hot-path `Decimal.Parse` on textbox text** — same handler. Hard `Decimal.Parse` calls that throw on blank/mid-edit fields: `txtPlacedPrice` (`:1148, :1186, :1306, :1332, :1371, :1381, :1390, :1393, :1404`), `txtMarketStopLoss` (`:1230`), `txtManualTP` (`:1368, :1378`), `txtTPOffset`/`txtComms` (`:1371, :1381`), `txtAmount` (`:1393, :1404`). The dangerous one: `txtPlacedPrice` at `:1148` is in **Block A, which runs before Block B**. A blank `txtPlacedPrice` throws there, the outer `Try` aborts (`:1425`), and **Block B (stop-loss repositioning) never runs** — a blank/mid-edit field silently disables SL trailing. Confirmed; this is the live-safety crux.

**#7 — Backtest corrupts live-indicator state** — `BacktestSignals` (`FrmIndicators.vb:1678`) loops over history and, per bar, calls the live `UpdateDmi/UpdateMacd/UpdateRsi/UpdateStochastic/EvaluateEmaVwapSignals` inside `Me.Invoke` (`:1752–1758`). I confirmed those subs (a) hold `Static` crossover state (`prevPDI`, `lastDMISignal`, `dmiInitialized`, `dmiPeriodsAfterCrossover`, and the equivalents in each sub — `:689–693, :837–841, :1011–1015, :1219–1223, :1399–1401`), (b) mutate the shared `score` field (`score += / -=`, e.g. `:818–823`), (c) toggle the shared `startupFired` flag, and (d) **write the live signal labels** (`lblDMI.Text`/`.ForeColor` `:810`, `lblMACD` `:972`, `lblRSI` `:1175`, etc.). So a backtest walks all that `Static` state forward over historical bars and leaves both the labels and the crossover memory seeded from the last historical bar — the live readout stays wrong until a real crossover re-seeds it. Confirmed.

---

## 2. Planned change per finding

### #2 (FrmIndicators — stopgap, 1 line of logic)
**Decision 3a resolved: positive magnitude.** Split the bug-causing expression, use the parsed value, and compare against the negated magnitude:
```vb
Decimal.TryParse(_autoTradeSettings.txtCircuitBreaker.Text, CircuitBreak)
If frmMainPageV2.USDPublicSession <= -Math.Abs(CircuitBreak) Then
```
So `50` in the field means "stop at $50 loss", robust to a stray sign. No other behavior touched. I'll keep the existing `frmMainPageV2.USDPublicSession` default-instance reference as-is (the default-instance smell is audit #10, out of scope here). **Carry-forward:** the corrected logic + sign convention becomes a hard requirement in the new analysis engine's spec — flagged here so it isn't lost when `FrmIndicators` is replaced.

### #3 (frmMainPageV2 — route id-3 through the receive loop)
1. Strip the inline `ReceiveAsync` block out of `RefreshWebSocketAuthentication` (`:391–418`). It becomes **send-only**: build payload, `SendWebSocketMessageAsync`, return.
2. Add a new handler `HandleTokenRefreshResponse(response)` modeled on `HandleAccountSummaryResponse` (parse once, `If id = 3`), that reads `result.refresh_token` / `result.expires_in`, updates `refreshToken`, and sets `refreshTokenExpiryTime = DateTime.UtcNow.AddSeconds(expiresIn - 240)`. Wire it into the dispatch list in `ReceiveWebSocketMessagesAsync` (`:596–610`).
3. **Critical detail:** today `RefreshWebSocketAuthentication` is gated by `If DateTime.UtcNow >= refreshTokenExpiryTime`. Because the new handler is what advances `refreshTokenExpiryTime`, I'll also set a short in-flight guard (a `refreshInFlight` flag or a provisional `refreshTokenExpiryTime` bump on send) so `MonitorAuthentication`'s once-a-minute loop can't re-send id-3 every minute in the (sub-second) window before the response lands. Without that the breaker condition stays true and spams refreshes.

`AuthorizeWebSocketConnection`'s inline receive is left untouched (it's the safe initial-connect path).

### #4 (frmMainPageV2 — marshal UI writes)
Wrap the three control writes in `AuthorizeWebSocketConnection` (`:367–369`) in `Me.BeginInvoke(Sub() … End Sub)`, matching `ConnectToWebSocketDirectly` (`:559`). I'll also audit the rest of that function for any other direct control access (the `AppendColoredText` calls already self-marshal via `Me.Invoke` inside the helper). First-connect path is unaffected; reconnect path stops throwing.

### #5 (frmMainPageV2 — single-flight reposition guard)
Add `Private isRepositioning As Integer = 0` (mirrors `isReconnecting`, `:446`). Wrap **Block A** and **Blocks C+D** each in:
```vb
If Interlocked.Exchange(isRepositioning, 1) = 0 Then
    Try
        ' … existing reposition body …
    Finally
        Interlocked.Exchange(isRepositioning, 0)
    End Try
End If
```
**Deliberately left OUTSIDE the guard:** Block B (triggered-SL emergency — must never be starved; keeps its own 333 ms throttle) and Block E (price/PnL labels — must update every tick). Because A and C/D are mutually-exclusive order states, one shared flag single-flights the entry-vs-trailing reposition without blocking SL. If a reposition is already in flight, the tick skips its reposition and falls through to B/E normally.

### #6 (frmMainPageV2 — parse once, skip the dependent action only)
At the top of the `quote.BTC-PERPETUAL` block, `Decimal.TryParse` the inputs into locals: `placedPrice` (+ `placedPriceValid`) and `amount`. Then:
- **A / C:** gate on `placedPriceValid` — no valid placed price means no reposition anyway; skip with a throttled log.
- **B:** replace `Decimal.Parse(txtMarketStopLoss.Text)` with `TryParse`; if invalid, skip only the **emergency-threshold** path and still run normal trailing (which doesn't need it). `txtPlacedStopLossPrice` already uses `TryParse` (`:1227`) — keep.
- **D:** `TryParse` `txtManualTP`/`txtTPOffset`/`txtComms`/`placedPrice`; if a needed field is invalid, skip the trailing-trigger this tick (throttled log).
- **E:** gate PnL math on `placedPriceValid AndAlso amountValid`; otherwise show `0` (current "else" branch behavior).

Add one throttled warn helper (e.g. gate to once / few seconds via a `lastParseWarn` timestamp) so a held-down blank field can't spam the log. No wholesale restructure of the handler (that's Spec B #9). Net guarantee: **no parse throws before Block B**, so SL trailing always runs.

### #7 (FrmIndicators — option A, guard now, purity deferred to the new engine)
**Decision 3b resolved: option A.** You'll hold off running the backtest until the new engine arrives, so the full pure-function extraction would be throwaway work on a file that's leaving. The new engine's spec carries **indicator purity as a hard requirement** (carry-forward note below). For the current form, a minimal guard so a backtest can't corrupt the live readout:

1. Add an `isBacktesting As Boolean` latch (instance field).
2. **Pause live evaluation during a run.** `UpdateSignals()` (`:641`) is the single live aggregator — it calls all five `Update*` subs, fires `ProcessAutomatedSignal`, and writes `lblScore`. Gate it at the top: `If isBacktesting Then Return`. So the 5 s poll can't interleave with the backtest and fight over `score` / the `Static` state / the labels.
3. **Latch around the backtest.** In `BacktestSignals` (`:1678`) set `isBacktesting = True` before the loop and reset it in a `Finally`.
4. **Re-seed after the run.** In that same `Finally`, after clearing the latch, call `UpdateSignals()` once. Because it re-runs every `Update*` on the current live `ohlcList`, it overwrites the `Static` `prev*` memory and the labels with live-derived values — so the live readout is restored regardless of how far the backtest walked the state.

This keeps the backtest runnable and self-cleaning, with zero change to the live signal math. If you'd rather **fully disable** the backtest trigger until the new engine instead of re-seeding (even less code — a single early-return in `BacktestSignals` with a log), say so and I'll do that; both honor option A.

**Carry-forward (must not be lost in the rewrite):** the new analysis engine evaluates indicators as pure functions — `(quotes, state) → (signalText, color, scoreDelta)`, no `Static`, no shared mutable `score`/`startupFired`, live and backtest on isolated state. Also drop MACD(6,13,5) / Stoch(8,3,3) and the RSI↔Stoch / EMA↔VWAP double-counts (rejected patterns) at that point — not here.

---

## 3. Decisions — RESOLVED

**3a — #2 circuit-breaker sign convention → POSITIVE MAGNITUDE.** Field holds e.g. `50` for "$50 loss limit". Comparison: `If USDPublicSession <= -Math.Abs(CircuitBreak)`.

**3b — #7 timing → OPTION A (guard now, purity deferred).** Owner is holding off on running the backtest until the new engine. Add an `isBacktesting` latch that pauses live evaluation during a run and re-seeds the live state/labels afterward (or fully disable the backtest trigger — owner's pick). Indicator purity is carried into the new engine's spec as a hard requirement. Design in §2 (#7).

---

## 4. Risk / blast-radius notes

- **#3 (high):** touches the auth/refresh path on a live socket. Risk if the in-flight guard is wrong: either repeated id-3 sends (rate-limit waste) or a missed rotation (token expiry → disconnect). I'll log every rotation so the test run is observable. Initial-connect path is explicitly not touched.
- **#5 (medium):** if the guard scope is drawn wrong it could starve SL repositioning — exactly what the spec warns against. Mitigation: Block B is outside the guard, full stop. I'll re-read the final block boundaries before committing.
- **#6 (medium):** changing parse → skip semantics could, if mis-scoped, skip a reposition that *should* fire. Mitigation: only skip the action whose own input is invalid; SL path made strictly more available, not less.
- **#4 (low):** UI marshaling only; `BeginInvoke` is fire-and-forget so ordering vs. the subsequent `EnableDeribitHeartbeatEnhanced` is unchanged.
- **#2 (low):** one comparison; only affects auto-trading, which is off by default.
- **#7 (low — option A):** no change to indicator math; the latch only suppresses the live poll during a run and re-seeds after. Risk is minimal — worst case a backtest run shows stale labels for one poll cycle before re-seed. Live auto-trading is unaffected (it goes through the same gated `UpdateSignals`).

No fix changes order *sizing*, stop *placement* logic, or scoring weights. No ATR-based stops, no non-directional or double-counted scoring introduced.

---

## 5. Test plan per finding (owner = tester, test sub-account)

- **#2:** Turn auto-trading on with a small configured limit. Confirm the breaker does **not** trip at small profit/loss, and **does** trip only when session PnL crosses the configured limit. Watch the "Circuit breaker triggered! Loss limit reached" log shows the configured number.
- **#3:** Stay connected >15 min on the test account. Confirm a token refresh logs success (I'll add a "token refreshed" log) with **no** "Error refreshing token" and no dropped private subscriptions (orders/portfolio keep updating).
- **#4:** Drop the network briefly to force a disconnect. Confirm auto-reconnect restores "ONLINE" (green) with **no** cross-thread exception in the log.
- **#5:** Open a limit order during a volatile minute and watch the log — reposition messages should not double-fire within a single tick.
- **#6:** With a position open and SL trailing active, clear `txtManualTP`/`txtManualSL`/`txtPlacedPrice` mid-trade. Confirm SL repositioning **still runs** and no parse-exception spam appears.
- **#7:** Note the live signal labels (DMI/MACD/RSI/Stoch/EMA-VWAP). Run a backtest. Confirm the labels are restored to live values afterward (re-seed) and that live auto-trading wasn't firing off backtest bars during the run.

---

## 6. Out-of-scope observations (noted, not actioned)

- `ProcessAutomatedSignal` reads `frmMainPageV2.USDPublicSession` via the **VB default instance** while using `CType(_host, frmMainPageV2)` for everything else — same object only by startup luck (audit #10).
- `RefreshWebSocketAuthentication` today never advanced `refreshTokenExpiryTime` even on the (rare) success path — moot once #3 routes the response through the handler, but worth noting it was a latent second bug.
- `throw` immediately followed by dead `AppendColoredText` lines in `AuthorizeWebSocketConnection`/`RefreshWebSocketAuthentication` (audit housekeeping). I'll leave them; Spec B owns that.
- Indicator scoring still includes MACD(6,13,5) and Stoch(8,3,3) and double-counts (RSI↔Stoch, EMA↔VWAP) — rejected patterns, to be dropped in the rewrite, not here.

---

## 7. Confirmation

I will: write a spec-back first (this doc), commit locally per fix with messages referencing the finding number, **not** push to remote (you push tested milestones), keep the build green (0 errors) after each fix and report any warnings I introduce, and introduce no hardcoded secrets, no ATR-based stops, and no non-directional or double-counted scoring.

Waiting on your approval of this spec-back — and answers to 3a / 3b — before writing any code.
