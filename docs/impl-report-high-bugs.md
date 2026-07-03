# Implementation Report — HIGH-severity bugs (#2–#7)

**For code review.** Per `docs/spec-high-bugs.md` → "Implementation report (REQUIRED — produced AFTER implementation)".
All fixes implemented, one local commit each, **not pushed**. Build green after every fix.

Line numbers are **post-edit** (final). Files: `DeribitOrderPlacementApp/frmMainPageV2.vb` (kept), `DeribitOrderPlacementApp/FrmIndicators.vb` (being replaced).

---

## 1. Per-finding summary

### #2 — Circuit breaker comparison  ·  `FrmIndicators.vb` · `ProcessAutomatedSignal` · lines 338–340
`Decimal.TryParse` returns a Boolean (`True`→`-1` under Option Strict Off), so the old line compared session PnL against `-1`. Split the parse out, compared against the negated magnitude, and guarded blank/zero.
```vb
' was: If frmMainPageV2.USDPublicSession < Decimal.TryParse(_autoTradeSettings.txtCircuitBreaker.Text, CircuitBreak) Then
Dim cbOk As Boolean = Decimal.TryParse(_autoTradeSettings.txtCircuitBreaker.Text, CircuitBreak)
If cbOk AndAlso CircuitBreak > 0D AndAlso frmMainPageV2.USDPublicSession <= -Math.Abs(CircuitBreak) Then
```
A blank field (parses to 0) now **disables** the breaker instead of tripping on any loss.

### #3 — Two concurrent `ReceiveAsync`  ·  `frmMainPageV2.vb`
- `RefreshWebSocketAuthentication` (line **384**) is now **send-only** — the inline `ReceiveAsync`/parse block is gone.
- New `HandleTokenRefreshResponse(response)` (line **418**) parses the id-3 response (`result.refresh_token` / `result.expires_in`), updates `refreshToken`, and advances `refreshTokenExpiryTime`.
- Wired into the central dispatch in `ReceiveWebSocketMessagesAsync` (line **640**), alongside the other id-keyed handlers.
- **Self-clearing single-flight guard** `refreshInFlight` (field line **34**): set on send, cleared by the handler, **re-armed after `RefreshResponseTimeoutSeconds` (30s)** if no response lands, and reset on every fresh `AuthorizeWebSocketConnection`. A lost id-3 can't permanently wedge refreshes.
```vb
' RefreshWebSocketAuthentication, send-only with self-clearing guard:
If refreshInFlight = 1 AndAlso (DateTime.UtcNow - refreshSentAt).TotalSeconds > RefreshResponseTimeoutSeconds Then
    Interlocked.Exchange(refreshInFlight, 0)
    AppendColoredText(txtLogs, "Token refresh response timed out - re-arming refresh", Color.Yellow)
End If
If DateTime.UtcNow >= refreshTokenExpiryTime Then
    If Interlocked.Exchange(refreshInFlight, 1) = 1 Then Return
    refreshSentAt = DateTime.UtcNow
    ... build id-3 payload ...
    Await SendWebSocketMessageAsync(refreshPayload.ToString())   ' SEND ONLY
End If
```
`AuthorizeWebSocketConnection`'s inline `ReceiveAsync` (initial connect, before the receive loop starts) is untouched — confirmed it is not a concurrent receive.

**Follow-up (review fix):** the id gate is now null-safe (line **422**). `messageId` is `Integer?` and is `Nothing` for id-less subscription messages; `Nothing <> 3` is `Nothing` (treated as `False` by `If`), so the original `If messageId <> 3 Then Return` fell through on every market tick (log flood + reset the guard each tick). Corrected:
```vb
If Not (messageId.HasValue AndAlso messageId.Value = 3) Then Return
```
Same null-safety as the positive id gates elsewhere (`HandleAccountSummaryResponse`'s `If messageId = 999`).

### #4 — Cross-thread UI on reconnect  ·  `frmMainPageV2.vb` · `AuthorizeWebSocketConnection` · line **375**
The three control writes are now marshaled, matching `ConnectToWebSocketDirectly`:
```vb
Me.BeginInvoke(Sub()
                   lblStatus.ForeColor = Color.LimeGreen
                   btnConnect.Text = "ONLINE"
                   btnConnect.BackColor = Color.Lime
               End Sub)
```
Other control access in the function goes through `AppendColoredText`, which self-marshals — no other direct writes found.

### #5 — Reentrancy guard on the quote handler  ·  `frmMainPageV2.vb` · `HandleQuoteUpdates`
New `Private isRepositioning As Integer` (field line **1156**, mirrors `isReconnecting`). Single-flight guard wraps the **entry-order reposition** (line **1210**), the **trailing-order reposition** (line **1376**), and the **trailing-TP trigger** (line **1440**) — all sharing the one flag:
```vb
If (<order-context condition>) _
   AndAlso Interlocked.Exchange(isRepositioning, 1) = 0 Then
    Try
        ... existing reposition body ...
    Finally
        Interlocked.Exchange(isRepositioning, 0)
    End Try
End If
```
**Not guarded** (run every tick): the triggered-SL emergency block (keeps its own 333 ms throttle, sits between the entry and trailing guards) and the price/PnL label updates.

### #6 — Hot-path `Decimal.Parse`  ·  `frmMainPageV2.vb` · `HandleQuoteUpdates`
`txtPlacedPrice` / `txtAmount` parsed **once** with `TryParse` at the top of the channel block (line **1197–1199**); every downstream use reads the locals.
- Entry + trailing repositions gate on `placedPriceValid`.
- Emergency-SL block `TryParse`s `txtMarketStopLoss` → a blank field disables only the emergency path; **normal SL trailing still runs**.
- Trailing-TP trigger `TryParse`s `txtManualTP`/`txtTPOffset`/`txtComms` (manual-override-else-offset logic preserved); skips just that trigger when invalid.
- PnL block uses the parsed-once locals; shows `0` instead of throwing.
- `WarnParseThrottled` helper (field/method line **1159–1166**, 5 s throttle) so a held-down blank field can't spam the log.

Result: **no parse throws before the SL block** — verified no `Decimal.Parse(txt…)` remains inside `HandleQuoteUpdates` (remaining occurrences are all in other handlers, line 1638+, out of scope).

### #7 — Backtest corrupts live-indicator state  ·  `FrmIndicators.vb`  (option A)
New `isBacktesting` latch (field line **31**).
- `UpdateSignals` (the single live aggregator) returns early while a backtest runs (line **650**), so the 5 s poll can't interleave with the walk.
- `BacktestSignals` sets the latch around its loop (line **1748**) and, in a `Finally` (line **1841**), clears it and re-runs `UpdateSignals` (marshaled to the UI thread) to re-seed the `Static` state + labels from current live data.
```vb
' BacktestSignals: latch around the walk, re-seed in Finally
isBacktesting = True
Try
    For i ... : Me.Invoke(Sub() UpdateDmi(window) ...) : Next
    ... results logging ...
Finally
    isBacktesting = False
    Try : Me.Invoke(Sub() UpdateSignals()) : Catch : End Try   ' re-seed live state + labels
End Try
```
Indicator math unchanged. `BacktestSignals` runs on a background thread (`Task.Run(AddressOf BacktestSignals)`), so the re-seed is marshaled like the existing `Update*` calls.

---

## 2. Decisions taken

- **#2 sign/guard → positive magnitude + blank-disables-breaker.** Owner-confirmed: field holds a positive loss magnitude (`50` = $50). `USDPublicSession` is session PnL in USD, negative in a loss (`indexPrice × (equity − balance)`), so the test is `<= -Math.Abs(CircuitBreak)`. Added the `cbOk AndAlso CircuitBreak > 0` guard per owner refinement 1.
- **#7 → option A** (owner-confirmed): pause-and-re-seed now, defer the pure-function refactor to the new engine (purity carried there as a hard requirement). Owner is holding off on running the backtest until then.

## 3. Deviations from the spec

- **#3:** added one extra safety beyond the self-clearing timeout — `refreshInFlight` is also reset inside `AuthorizeWebSocketConnection` on every fresh auth, so a guard left set by a failed send before a disconnect clears immediately on reconnect rather than waiting out the 30 s. In-spirit with "must self-clear".
- **#5:** the spec named the "order-reposition section"; I also placed the shared guard on the **trailing-TP trigger** (Block D, which *places* a trailing SL order), not just the two reposition paths, to close a cross-block race where D could fire while a trailing reposition was in flight. D also keeps its own `isTrailingStopLossPlaced` latch.
- **#5 (cosmetic):** guards were added by appending `AndAlso Interlocked.Exchange(...)` to each block's condition and bracketing the body with `Try/Finally` **without re-indenting** the existing body (VB is whitespace-insensitive). This keeps the diff tight and avoids transcription risk in live-order code; the body indentation therefore looks one level shallow inside the `Try`. Flagging so it's not mistaken for a paste error.
- No other deviations. No changes to order sizing, stop placement, or scoring weights.

## 4. Build status

`dotnet build DeribitOrderPlacementApp.sln` — **Build succeeded, 0 Warning(s), 0 Error(s)** after each fix and on a final `--no-incremental` clean rebuild. No new warnings introduced.

## 5. Commits (local, not pushed — one per fix)

```
d06f919  Fix #3 follow-up: null-safe id guard in HandleTokenRefreshResponse
1b3c169  Fix #7: guard live indicator state during backtest, re-seed after (option A)
e76a3f3  Fix #5: single-flight guard on the order-reposition section of HandleQuoteUpdates
3fb1743  Fix #6: TryParse hot-path inputs in HandleQuoteUpdates so a blank field can't disable SL
02abecf  Fix #3: route token-refresh response through the receive loop
6d30538  Fix #2: correct circuit-breaker comparison and guard unset field
bb8e6e7  Fix #4: marshal reconnect UI writes in AuthorizeWebSocketConnection via BeginInvoke
```
(Prior local commit `09d96d6` externalized the API credentials — critical #1, not part of this spec.)

## 6. Test results / plan

**Verified here:** clean compile (0/0) after every fix; static confirmation that no unguarded `Decimal.Parse(txt…)` remains in `HandleQuoteUpdates`; Try/Finally balance confirmed by the compiler. I cannot place live orders or exercise the WinForms runtime — owner is the tester.

**Owner steps (test sub-account):**
- **#2** — auto-trading on, set `txtCircuitBreaker = 5`: confirm no trip at small profit/small loss, trips only when session PnL ≤ −$5; then blank the field and confirm the breaker is disabled (no trip on a loss).
- **#3** — stay connected >15 min: confirm a "WebSocket re-authenticated successfully (token rotated)" log with **no** "Error refreshing token" and no dropped private subscriptions.
- **#4** — drop the network briefly to force reconnect: confirm "ONLINE" is restored green with no cross-thread exception in the log.
- **#5** — open a limit order during a volatile minute: confirm reposition log lines don't double-fire within a tick.
- **#6** — with a position open and SL trailing active, clear `txtManualTP`/`txtManualSL`/`txtPlacedPrice`: confirm SL repositioning still runs and no parse-exception spam (at most a throttled gray warn).
- **#7** — note the DMI/MACD/RSI/Stoch/EMA-VWAP labels, run a backtest, confirm the labels are restored to live values afterward and auto-trading didn't fire off backtest bars during the run.

## 7. Out-of-scope observations (noted, not actioned)

- `ProcessAutomatedSignal` reads `frmMainPageV2.USDPublicSession` via the **VB default instance** while using `CType(_host, frmMainPageV2)` for everything else (audit #10). Left as-is.
- The `throw`-then-dead-`AppendColoredText` lines remain in `AuthorizeWebSocketConnection`/`RefreshWebSocketAuthentication` (audit housekeeping → Spec B).
- Indicator scoring still includes MACD(6,13,5), Stoch(8,3,3), and the RSI↔Stoch / EMA↔VWAP double-counts — rejected patterns, to be dropped with the new engine, not here.
- Block A keeps a redundant inner `Decimal.TryParse(txtPlacedPrice.Text, currentPlacedPrice)` purely for the reposition log line — already safe, left to minimize churn.

## 8. Confirmation

Committed locally per-fix; **did not push**; build kept green (0/0) throughout; no hardcoded secrets reintroduced (credentials still load via `AppSecrets`/`secrets.json`); no ATR-based stops, no non-directional scoring, no double-counted signals introduced. `FrmIndicators` purity (#7) and the corrected circuit-breaker logic/sign (#2) are flagged to carry into the new analysis engine.
