# Implementation Spec — HIGH-severity bugs (#2–#7)

**Source:** `docs/CODE_AUDIT.md` (read it first for full context).
**Project:** DeribitOrderPlacementApp — .NET 9 WinForms, VB.NET, Deribit BTC-PERPETUAL over WebSocket, Skender.Stock.Indicators, SQLite, Newtonsoft.Json.
**Run order:** This spec runs **FIRST**. The MEDIUM+Housekeeping spec (`spec-medium-housekeeping.md`) runs after this one is committed locally, because both edit `frmMainPageV2.vb`.

> ⚠️ Line numbers below are approximate and predate recent edits. **Locate code by function name and the quoted snippets, not by absolute line number.**

---

## Context you need

- `frmMainPageV2.vb` is the **live** trading form (~4,350 lines). It owns the WebSocket, auth, rate limiting, order placement/editing, stop-loss management, margin display, and the trade-history viewer. It is being **kept**.
- `FrmIndicators.vb` is the signal/auto-trade + backtest form. It is being **replaced** by a new analysis engine in a separate effort, **but the backtesting feature is being retained**. Two of the bugs below (#2, #7) live here — see the per-item notes on whether to fix now or fold into the rewrite.
- `Option Strict Off` is in effect for hand-written files (only Designer files set it On). This is what allowed bug #2 to compile. Do **not** flip Option Strict globally as part of this spec.
- API credentials were just externalized to a git-ignored `secrets.json` via `AppSecrets.vb`. Don't reintroduce hardcoded secrets.

## Ground rules (owner's workflow — follow exactly)

1. **Write a spec-back BEFORE writing any code** (see end of this file). Do not start editing until it's produced.
2. **Local commits as you go are fine and encouraged.** One commit per fix, descriptive message referencing the finding number (e.g. `Fix #3: route token-refresh response through receive loop`).
3. **Do NOT push to remote.** The owner pushes manually after testing. Remote only ever gets tested milestones.
4. **Compile gate:** the solution must build with **0 errors** after each fix (`dotnet build DeribitOrderPlacementApp.sln`). Report warnings you introduce.
5. **Test gate:** you cannot place live orders. For each fix, state exactly what the owner must do to verify it (the owner is the tester).
6. Trading-logic guardrails (from the owner's profile): do not introduce ATR-based stops (stops are structural), do not add non-directional scoring, do not double-count signals. None of these bugs should tempt you toward those, but don't "improve" beyond scope.
7. **Stay in scope.** These six findings only. Note anything else you spot in the spec-back; don't fix it here.

---

## Findings to fix

### #2 — Circuit breaker comparison is broken  *(file being replaced — stopgap)*
**Where:** `FrmIndicators.vb`, `ProcessAutomatedSignal`:
```vb
If frmMainPageV2.USDPublicSession < Decimal.TryParse(_autoTradeSettings.txtCircuitBreaker.Text, CircuitBreak) Then
```
**Problem:** `Decimal.TryParse` returns a `Boolean`; under Option Strict Off `True`→`-1`, so the breaker trips at -$1 session PnL instead of the configured limit, and the parsed `CircuitBreak` value is unused.
**Required change (decision 3a resolved — POSITIVE MAGNITUDE):** the field holds a positive loss magnitude (e.g. `50` = stop at $50 loss); `USDPublicSession` is session PnL in USD (negative in a loss). Split the expression, use the parsed value, and **guard against a blank/zero field so the breaker is disabled (not always-on) when unset** — matching how `txtATRLimit` is treated elsewhere:
```vb
Dim cbOk As Boolean = Decimal.TryParse(_autoTradeSettings.txtCircuitBreaker.Text, CircuitBreak)
If cbOk AndAlso CircuitBreak > 0D AndAlso frmMainPageV2.USDPublicSession <= -Math.Abs(CircuitBreak) Then
```
**Important:** without the `cbOk AndAlso CircuitBreak > 0` guard, a blank field parses to `0` and the breaker trips on *any* loss (`USDPublicSession <= 0`). The guard is required, not optional.
**Note:** `FrmIndicators` is slated for replacement. This is a one-line stopgap so the breaker is safe until the rewrite. **The corrected logic + sign convention must be carried into the new engine** — call this out in your spec-back.
**Acceptance:** with auto-trading on and a configured limit, the breaker trips only when session PnL crosses that limit. Owner verifies on a test sub-account.

### #3 — Two concurrent `ReceiveAsync` on the same socket
**Where:** `frmMainPageV2.vb`, `RefreshWebSocketAuthentication` does its own `webSocketClient.ReceiveAsync(...)` while `ReceiveWebSocketMessagesAsync` is already looping on receive.
**Problem:** `ClientWebSocket` forbids two outstanding receives. The refresh (fired from `MonitorAuthentication` ~every minute, acting once the token nears expiry) throws, is swallowed, and the refresh token likely never rotates; the real auth response (id 3) is consumed by the main loop, which ignores it.
**Required change:** `RefreshWebSocketAuthentication` must **only send** the refresh payload. Handle the id-3 response in the central receive path (a dedicated handler that parses `result.refresh_token` / `result.expires_in` and updates `refreshToken` + `refreshTokenExpiryTime`). Never call `ReceiveAsync` outside the single receive loop.
**Watch:** `AuthorizeWebSocketConnection` also does an inline `ReceiveAsync` during initial connect — that one is OK because it runs **before** the receive loop starts. Don't break that path; only fix the concurrent case. Confirm ordering carefully.
**In-flight guard must self-clear:** the new id-3 handler is what advances `refreshTokenExpiryTime`, so add the in-flight guard the spec-back proposes — but make it **self-clearing** (reset after a short timeout, or re-arm on the next gate check). A lost/never-arriving id-3 response must not permanently wedge the flag and stop all future refreshes. (Reconnect re-auths fresh as a backstop, but don't rely on it.)
**Acceptance:** run connected >15 min on a test account; confirm a token refresh succeeds (log it) with no "Error refreshing token" and no dropped private subscriptions.

### #4 — Cross-thread UI access on reconnect
**Where:** `frmMainPageV2.vb`, `AuthorizeWebSocketConnection`:
```vb
lblStatus.ForeColor = Color.LimeGreen
btnConnect.Text = "ONLINE"
btnConnect.BackColor = Color.Lime
```
**Problem:** Fine on first connect (UI thread via button), but the reconnect path (`HandleWebSocketDisconnect → ConnectToWebSocketDirectly → AuthorizeWebSocketConnection`) runs on a thread-pool thread → cross-thread exception that surfaces as a spurious auth failure.
**Required change:** marshal these UI writes with `Me.BeginInvoke(Sub() … End Sub)`, matching the pattern already used elsewhere in the file. Audit the whole function for any other direct control access and wrap consistently.
**Acceptance:** force a disconnect (e.g. drop network briefly) and confirm auto-reconnect restores "ONLINE" with no cross-thread exception in the log.

### #5 — No reentrancy guard on the quote handler
**Where:** `frmMainPageV2.vb`, `HandleQuoteUpdates` (`Async Sub`, invoked once per quote tick from the receive loop).
**Problem:** On fast markets, tick N+1 can enter while tick N is awaiting `UpdateLimitOrderWithOTOCOAsync` / `UpdateStopLossForTrailingOrder`, double-sending `private/edit` reposition bursts and wasting rate-limit credits.
**Required change:** add a single-flight guard around the **order-reposition** section (not the whole handler — price/PnL label updates should still run every tick). Use `Interlocked.Exchange(flag, 1)` / reset in a `Finally`, mirroring the existing `isReconnecting` pattern. If a reposition is already in flight, skip this tick's reposition.
**Care:** do not skip stop-loss emergency updates — make sure the guard scope doesn't starve the SL-repositioning logic. Keep entry-order repositioning and triggered-SL repositioning correctly separated.
**Acceptance:** owner watches the log during a volatile minute with an open limit order; reposition messages should not double-fire per tick.

### #6 — Hot-path `Decimal.Parse` on textbox text (no `TryParse`)
**Where:** `frmMainPageV2.vb`, throughout `HandleQuoteUpdates` (e.g. `Decimal.Parse(txtManualTP.Text)`, `Decimal.Parse(txtPlacedPrice.Text)`, `Decimal.Parse(txtAmount.Text)`).
**Problem:** a blank/mid-edit field throws; the tick's `Try` aborts and the **stop-loss repositioning later in the same handler is skipped** — a blank field can silently disable SL trailing.
**Required change:** at the top of the relevant block, parse the inputs you need **once** with `Decimal.TryParse`; if a required field is invalid, skip just the dependent action (with a throttled log) rather than letting the whole tick throw. Prefer reading numeric state from backing fields/parsed-once locals over re-parsing control text repeatedly.
**Scope discipline:** this is the hot path — keep it allocation-light and fast. Don't restructure the handler wholesale (that's partly #9 in Spec B); just make parsing safe and ensure SL logic always runs.
**Acceptance:** clear `txtManualTP`/`txtManualSL` while a position is open; confirm SL repositioning still runs and no parse exception spams the log.

### #7 — Backtest corrupts live-indicator state  *(file being replaced — decide timing)*
**Where:** `FrmIndicators.vb`, `BacktestSignals` calls the live `UpdateDmi/UpdateMacd/UpdateRsi/UpdateStochastic/EvaluateEmaVwapSignals` in a loop. Those subs hold `Static` locals (`prevPDI`, `lastDMISignal`, `dmiInitialized`, …) and mutate shared `score`/`startupFired`.
**Problem:** running a backtest walks that static state forward and leaves the live signal readout wrong until the next real crossover re-seeds it.
**Required change (target design):** indicator evaluation must be **pure** — `(quotes) → (signalText, color, scoreDelta)` with no `Static`, no shared mutable `score`/`startupFired`. Live display and backtest both call the pure function; neither mutates the other's state.
**Timing decision (raise in spec-back):** this is a non-trivial refactor inside a file that's being replaced. Two options — recommend one to the owner:
  - **(A) Defer to the rewrite:** make purity a hard requirement of the new engine's spec; for now add a guard so backtest can't run while live signals are being relied on (e.g. pause the poll/live update during a backtest run and re-seed after). Lower effort, no throwaway work.
  - **(B) Do it now:** extract pure indicator functions in the current `FrmIndicators` and have both paths use them. Higher effort in soon-to-be-replaced code, but de-risks the live form immediately.
**Acceptance:** run a backtest, then confirm the live signal labels are unaffected (either because they're pure, or because live updates are paused/re-seeded around the run).

---

## Implementation report (REQUIRED — produced AFTER implementation, for code review)

Implement the fixes directly. If a **blocking** decision comes up (e.g. #2 sign convention, #7 option A vs B), raise it with the **owner** — those are the owner's to answer, not part of this review. Do not wait on a pre-implementation review.

Once all fixes are implemented, committed locally, and the build is green, produce a short report and hand it to the owner to paste back **for the reviewer to check the actual code**. It must contain:

1. **Per-finding summary (#2–#7):** what you changed, the **final** function name + line numbers (post-edit), and the actual code snippet (or diff) for each fix.
2. **Decisions taken:** the choice made for #2 (circuit-breaker sign/guard) and #7 (option A vs B), and why.
3. **Deviations from this spec:** anything done differently, and the reason.
4. **Build status:** `dotnet build DeribitOrderPlacementApp.sln` result; list any new warnings.
5. **Commits:** local commit hashes + messages, one per fix.
6. **Test results / plan:** what you verified yourself, plus exact steps for the owner to verify each fix on a test sub-account.
7. **Out-of-scope observations:** anything else noticed, listed but not actioned.
8. **Confirmation:** committed locally per-fix, did **not** push, kept the build green, introduced no hardcoded secrets / ATR-based stops / non-directional or double-counted scoring.

The owner pastes this report (and may share the diff) so the reviewer can check the code against `CODE_AUDIT.md` and this spec. **Do not push to remote** — the owner pushes after review + test.
