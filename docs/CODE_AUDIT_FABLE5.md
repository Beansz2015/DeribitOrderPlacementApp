# Code Audit 2 — Fable 5 full trace (2026-07-02)

**Scope:** Full re-read of every hand-written `.vb` file at HEAD `e5735ef` (branch `housekeeping-now`), plus Designer defaults, `.vbproj`, and `.gitignore`. Build verified green (0 warnings / 0 errors). This complements `CODE_AUDIT.md` (2026-06-22): items already catalogued there are only *confirmed*, not re-argued. New findings are numbered **F1–F20**.

**Context honored:** `FrmIndicators` (signals/backtest) will be **replaced by DeribitVerdictEngine** (separate project, not yet on disk). `AutoTradeSettings` will be **re-coded** as the settings/trigger surface for it. Findings inside dying code are only listed where they either (a) affect the live form, or (b) must carry forward as requirements for the replacement.

**Verdict:** The receive-loop threading and transition-race work is genuinely solid — the §5 invariants hold everywhere I traced them, and the id-guard/backoff/single-flight patterns are implemented as documented. What remains falls into four buckets: **two real correctness bugs on the live trade-close path (F1, F3)**, **a silent-failure gap in exchange error handling (F2)**, **a resilience hole around connection death (F5–F7)**, and a long tail of quality/housekeeping items. Nothing found that risks funds directly — the exchange-side OTOCO SL legs remain the backstop in every failure mode traced.

Severity legend: 🔴 Critical · 🟠 High · 🟡 Medium · ⚪ Housekeeping

---

## A. New findings — correctness

### 🟠 F1. Every completed trade is recorded with EntryPrice = 0
`frmMainPageV2.vb:1980` → `:2016`

In the position-closed branch of `HandleOrderPositionUpdates`:

1. `:1980` — `Await CancelOrderAsync()` runs first (cleans up the leftover TP/SL leg). `CancelOrderAsync` sets `placedPrice = 0D` (`:2525`) before returning, and `cancelPending = True` blocks any echo from re-seeding it.
2. `:2016` — `RecordCompletedTrade(placedPrice, ExecPrice, …)` then passes the **already-zeroed** `placedPrice` as `entryPrice` (3rd ctor arg of `TradeRecord`, confirmed → `EntryPrice` column).

`PorLAmt` is computed earlier in the orders loop, so **P/L is correct**; only the entry-price column is deterministically 0 for every DB-recorded trade. Trade-history view, CSV export, and any later analytics on entry price are garbage.

**Fix shape (2 lines):** snapshot `Dim entryPriceAtClose As Decimal = placedPrice` at the top of the `size = 0` branch (before the cancel), pass the local. Cheap enough to fold into the next fix bundle; runtime-verify by closing one test trade and checking `btnViewTrades`.

### 🟠 F2. Order rejections from Deribit are silently dropped
`ExecuteOrderAsync` (`:2476-2501`), `SendReduceOrderAsync`, all `private/edit` senders

No handler processes JSON-RPC **error** responses except `HandleRateLimitError`, which only matches code 10028. An order rejected for anything else (insufficient margin, invalid amount/contract step, bad price, order-not-found on edit) produces:

- Log says **“Buy limit order placed for X at Y”** — printed unconditionally after the *send*, before any ack.
- `placedPrice`/`placedStopLossPrice` are seeded at `:2483-2484` as if the order exists.
- No `EntryLimitOrder` echo ever arrives, so `CurrentOpenOrderId` stays `Nothing` — repositioning silently never engages, and the stale-context warning (`:1201`) doesn’t fire because it requires an order ID/SL flag.

Manual trading survives this because the trader watches the Deribit UI. **Under VerdictEngine automation nobody is watching** — a rejected entry looks identical to a working one in `txtLogs`. Same applies to failed `private/edit` repositions (e.g. editing an order that just filled — a routine race) and to `TrailingStopLossOrderAsync`'s sends.

**Fix shape:** one generic error logger in the receive dispatch — `If error IsNot Nothing AndAlso id.HasValue Then log(id, code, message)` — plus, properly (fold into the #10 decouple or the dispatcher), match ids to pending actions and roll back optimistic state (`placedPrice = 0` on entry rejection). The first half is ~10 lines and worth doing soon; the rollback half can wait for #10.

### 🟠 F3. Emergency market-stop fires even when `chkMarketStopLoss` is unchecked
`UpdateStopLossForTriggeredStopLossOrder` `:2758-2770` vs. the gate at `:1323`

Two emergency paths exist:

- Quote handler (`:1322-1326`): checks `marketStopLossChecked` before calling `ForceStopLossUpdate`. ✅
- Inside `UpdateStopLossForTriggeredStopLossOrder` (`:2758`): re-checks only `marketStopThreshold > 0D` — **not the checkbox**.

The normal SL-trail path (`:1354`) calls `UpdateStopLossForTriggeredStopLossOrder` on every qualifying tick. So with the checkbox **off** and a non-zero `txtMarketStopLoss` (Designer default is **"70"**), an adverse move ≥ threshold during triggered-SL trailing still cancels everything and market-closes the position. Protective direction, but it directly contradicts the toggle — the operator believes the feature is off. Fix: add `marketStopLossChecked AndAlso` to both branches at `:2758`/`:2764`.

### 🟡 F4. `btnEditSLPrice` checks the TP order id but assigns the SL id; both edit buttons send after “ID not found”
`:4010-4011`, `:3963-3965`

- `ElseIf PositionTPOrderId IsNot Nothing Then SLOrderID = PositionSLOrderId` — wrong field in the condition. If `PositionTPOrderId` is set while `PositionSLOrderId` is `Nothing`, it sends `private/edit` with `order_id = Nothing`; in the reverse case it wrongly reports “not found”.
- In **both** `btnEditTPPrice` and `btnEditSLPrice`, the `Else … "not found"` branch has **no `Return`** — the edit payload is sent anyway with a null id. Combined with F2, the failure is invisible.

Manual-path only, but these are the buttons you reach for mid-trade. Three-line fix.

### 🟡 F5. Receive loop ignores `EndOfMessage` — fragmented frames become silent parse noise
`ReceiveWebSocketMessagesAsync` `:632-650`; also the auth read at `:363-365`

One `ReceiveAsync` per message into a 64 KB buffer, `result.EndOfMessage` never checked. Any message fragmented by the server/intermediary — or larger than 64 KB — is decoded as two invalid JSON halves, each swallowed by every handler’s catch. A lost `user.changes` echo means a missed fill/cancel confirmation (state machine wedges until the cancel-pending timeout or manual action). Bursty `user.changes.BTC-PERPETUAL.raw` messages (multi-order events) are the realistic risk.

**`FrmIndicators.ConnectAndStream` already does this correctly** (`FrmIndicators.vb:135-153`: StringBuilder + `EndOfMessage` gate) — port that pattern. Carry the same requirement into VerdictEngine’s socket loop.

---

## B. New findings — connection resilience

These three compound into one operational risk: **the app can sit “ONLINE” with a dead feed.** Exchange-side SL legs cap the fund risk, but stale prices + a frozen SL-trail is not a state you want to discover by eye.

### 🟠 F6. Graceful server close never triggers reconnect
`:645-647` — the `Close` message branch does `Exit While` with `reconnectNeeded = False`, so `HandleWebSocketDisconnect` is never called. Deribit closes gracefully during maintenance/rebalances, and also closes when heartbeat replies go missing. After that, recovery only happens if some later *send* fails. One-line fix: `reconnectNeeded = True` in that branch.

### 🟡 F7. Dead-silence detection is structurally ineffective
- The 75-second timeout check (`:690`) sits **after** a successful blocking `ReceiveAsync` — `lastMessageTime` was updated three lines earlier, so the condition can never be true. If the socket goes silent without erroring, the loop blocks in `ReceiveAsync` forever and the check never runs. Dead code in its current position.
- `MonitorConnectionHealth` (`:743`) — the periodic `public/ping` loop that would generate traffic and surface a dead link — is **defined but never started** (verified: zero call sites).
- `ClientWebSocket.Options.KeepAliveTimeout` (available on .NET 9) is not set, so protocol pings don’t abort on missing pongs either.

Cheapest robust fix: set `KeepAliveInterval` + `KeepAliveTimeout` on the socket and delete the unreachable check + unused monitor. That makes `ReceiveAsync` itself throw on a dead link, which lands in the existing reconnect path.

### 🟡 F8. Rate limiter is never initialized on connect; placements never consume credits
- `InitializeRateLimitsAfterAuth`’s only call site is the **manual re-click of the ONLINE button** (`:3603`). `ConnectToWebSocketDirectly` never initializes limits, so `rateLimiter` is `Nothing` after every connect until some reposition path lazily installs an “emergency” 1000-credit limiter.
- `ExecuteOrderAsync` — the most credit-expensive call class (matching engine) — never consults the limiter at all. Only edits/repositions do. Auto-trade bursts under VerdictEngine would be unthrottled at the placement level (mitigated today by the cooldown + one-position-at-a-time gate).
- Cosmetic but confusing: `DeribitRateLimiter` hardcodes refill 15 credits/ms while comments and `RateLimitInfo.RefillRate` claim 10; `RateLimitInfo.RefillRate` is never read anywhere.

Fix shape: call `InitializeRateLimits()` at the end of `ConnectToWebSocketDirectly`, and have `ExecuteOrderAsync` consume credits like the edit paths do.

### 🟡 F9. `MonitorAuthentication` loops accumulate across reconnects
`:711-721` — each `ConnectToWebSocketDirectly` starts a new loop; old loops re-read the *current* `webSocketClient` field, see it Open, and keep polling. After N reconnects, N loops each call `RefreshWebSocketAuthentication` every 60 s. Harmless today (single-flight guard + 1-year token makes the gate almost never fire), but it’s the kind of leak that becomes a mystery later. Fix: capture the socket instance at loop start and exit when the field no longer matches, or gate with an `Interlocked` flag like `isReconnecting`.

---

## C. New findings — threading & hot-path quality

### 🟡 F10. Blocking `Me.Invoke` throughout the receive hot path
The cross-thread fix correctly moved *decisions* to fields, but display writes still use synchronous `Invoke` in the hottest spots:

- `HandleQuoteUpdates`: `txtTopBid`/`txtTopAsk` (`:1181`, `:1188`) + the PnL label block (`:1503-1530`) — **3–4 blocking UI round-trips per quote tick**, executed on the receive thread before the tick’s decisions.
- `AppendColoredText` (`:3291`) — blocking `Invoke` on **every log line** from the receive thread.
- `HandleOrderPositionUpdates` open/untriggered branches (`:1632`, `:1747`), `HandleHeartbeat` (`:1011`, `:1020`), `CancelOrderAsync` (`:2539`, `:2562`), `ProcessPositionData` (`:3467`).

Two consequences: receive-loop throughput is serialized behind the UI message pump (quote bursts queue up), and every blocking `Invoke` is a deadlock arm whenever the UI thread ever waits on the receive side (today’s only sync-wait — `FrmIndicators_FormClosing` — is bounded at 2 s, so it’s exposure rather than a live deadlock). The non-blocking `UiInvoke` already exists (`:117`) and is the right tool for every one of these display-only writes. Note the ordering caveat: `HandleOrderPositionUpdates`’ open/untriggered lambdas *mutate engine state and loop locals*, so those two need their state writes hoisted out of the lambda before switching to `BeginInvoke` — don’t do a mechanical find/replace there.

### 🟡 F11. Raw `Decimal.Parse` survivors in the trailing-order echo path
`:1696-1702` (`EntryTrailingOrder` open-echo, inside the `Invoke` lambda): `Decimal.Parse(txtManualTP.Text)`, `txtTPOffset`, `txtComms`. A cleared/blank field mid-trailing-flow throws inside the lambda; `Invoke` marshals the exception back to the receive thread, aborting the **rest of the orders loop for that message** (later fills/cancels in the same echo are skipped). All Designer defaults are numeric ("0"), so this only fires when the user clears a field — exactly the scenario the #6 fix targeted on the quote path. Same treatment: read the mirror fields (`manualTPval`, `tpOffsetVal`, `commsVal`) which are already maintained.

### 🟡 F12. No global exception backstop
`ApplicationEvents.vb` is an empty stub — no `UnhandledException` handler, no `TaskScheduler.UnobservedTaskException` hook. The app leans heavily on fire-and-forget `Async Sub`s and `Task.Run`s; each body is Try-wrapped, but the wrappers themselves call `AppendColoredText → Me.Invoke`, which throws `InvalidOperationException` if the handle is gone (shutdown races) — and an exception escaping an `Async Sub` on a thread-pool context **kills the process**. A 5-line `UnhandledException` handler that writes to a crash log turns mystery vanishes into diagnosable ones. Cheap insurance for unattended auto-trading.

### ⚪ F13. Minor threading notes
- `USDPublicSession` (`:65`): public `Decimal` field written on the receive thread, read cross-form. 128-bit → torn reads are possible in principle. The #10 API property is the natural fix; until then it’s an accepted micro-risk.
- `isRequestingLiveData`/`cancelPending`/flag booleans are unsynchronized but single-writer-ish and self-healing (timeouts) — consistent with the documented design; no change needed.
- The untriggered-`StopLossOrder` echo writes `placedStopLossPrice` unconditionally (`:1763`) while the triggered/open echo is single-writer-gated (`:1679`). Harmless today (trailing only runs when `SLTriggered`), but it’s in the same family as the two known deferred edge cases — fold into #10’s cleanup list.

---

## D. New findings — housekeeping (adds to `spec-medium-housekeeping.md`)

⚪ **F14. Dead code (verified zero call sites):** `HandleOrderUpdates` (`:2057`), `ExportTradesToCSV` (`:4350` — would also fail on .NET 9: `Process.Start(fileName)` needs `UseShellExecute` for documents), `LogFailedEntry` (`:2115` — and it dereferences `_autotradesettings`, which is **never assigned** in this form → NRE landmine if ever un-commented; the field itself is dead), `MonitorConnectionHealth` (delete *or* actually start it — see F7), `TradeAnalytics.GetTradeStatistics` (known), `UpdateEmaVwapLabels` (`FrmIndicators.vb:1617`).

⚪ **F15. JSON-RPC id space is ad-hoc.** id 2 = auth **and** entry orders **and** trailing entries; id 4 = portfolio subscribe **and** heartbeat test; id 30 = cancel-all **and** trailing-stop placement; id 1 = index subscribe **and** reduce orders. Harmless only because responses are ignored (F2). Any future response-matching (F2’s rollback half, the #9 dispatcher, VerdictEngine) needs unique ids per request class first — do this as the first step of whichever lands first.

⚪ **F16. 500 ms built-in entry latency.** `btnLimit/btnNoSpread/btnMarket/btnTrail` all do `btnEstimateMargins_Click` + `Await Task.Delay(500)` before placing (`:3785` etc.). The margin estimate is display-only; for a scalper clicking top-of-book this is half a second of pure slippage window per manual entry. The auto path (`ExecuteAutomatedOrder`) skips it — keep it that way in the VerdictEngine contract, and consider dropping the delay to fire-and-forget the estimate on the manual path too.

⚪ **F17. Unused package references:** `EntityFramework 6.5.1` and `System.IO.FileSystem.DriveInfo 4.3.1` — zero usages in source (SQLite is used directly). Remove from the `.vbproj`; smaller publish, fewer CVE-surface deps.

⚪ **F18. Small cosmetic/logic nits.**
- `DeleteMultipleTrades` raises its **success** message through the `DatabaseError` event (`TradeDatabase.vb:208`) → logs red “Database Error: Successfully deleted N trades”.
- `AutoTradeSettings` calls `Me.Hide()` inside its own `Load` (`AutoTradeSettings.vb:20`), which fires on first `Show()` → **first click of the settings button does nothing**; second click works.
- Non-short-circuit `And` on the slippage gates (`:1220`, `:1259`, `:1400`, `:1426`): `IsATRSlippageExcessive` runs (and mutates `originalSignalPrice`, logs, calls `ResetOrderAttempt`) even with the checkbox off. `AndAlso` fixes the side effects; also the trailing LONG branch passes `bestAsk` where the entry branch uses `bestBid` (`:1400`) — copy/paste drift.
- PnL display marks longs to **ask** (`:1501`); closing a long lifts the bid — cosmetic optimism.
- `TrailingStopLossOrderAsync` sends its own inline `cancel_all` (`:3168`) instead of `CancelOrderAsync`, bypassing `cancelPending`. Protected today only because the caller holds `isRepositioning` across the await chain — an undocumented invariant. Route it through `CancelOrderAsync` or set `cancelPending` explicitly.
- `HandleAccountSummaryResponse` uses `SetResult` (`:782`, `:800`) — a late response after the timeout throws into the swallow-catch; `TrySetResult` is the correct call.
- `AppendColoredText`’s `txtLogs` grows unbounded — long unattended sessions will degrade the UI thread (which, per F10, the receive loop waits on). Trim to a max length.
- Duplicate TP computation in `ExecuteOrderAsync` BuyLimit (`:2187-2193` — If/Else then the same ternary again).
- Behavioral note to document, not change: a failed SL edit backs off up to 5 s (`BackoffStopLossRetry`), and the **emergency-threshold check lives inside the throttled block** (`:1299` → `:1322`), so a persistent edit failure also delays emergency market-stop detection by up to 5 s. Acceptable trade-off vs. the #4 edit-storm, but it should be a known property, not a surprise.

---

## E. Known items confirmed still present (deferred by design)

| Item | Where | Status |
|---|---|---|
| #9 parse-per-handler (8× `JObject.Parse` per message) | `:656-672` | Confirmed; dispatcher spec exists, deferral recommended stands |
| #10 PerformClick coupling + default instance | `FrmIndicators.vb:555-562`, `:1878-1879` (`frmMainPageV2.txtTakeProfit` via default instance), `:349` (`frmMainPageV2.USDPublicSession`), `:400/:547` (reads `mainForm.txtPlacedPrice`) | Confirmed; `ExecuteAutomatedOrder` still has zero callers |
| #11 unguarded `ConnectAndStream` reconnect + 5s redundant re-fetch + no client dispose | `FrmIndicators.vb:178-209` | Confirmed; carried as VerdictEngine requirement |
| Heartbeat enabled twice per connect | `:377` + `:612` | Confirmed (in medium-housekeeping spec) |
| Throw-then-dead-code | `:371-373`, `:383-385` | Confirmed (spec’d) |
| Empty `ProcessEstimationData` / vestigial id-890 branch | `:3572`, `:3407` | Confirmed (spec’d; keep id-777) |
| Duplicate account-summary functions | `:899` vs `:3222` | Confirmed (spec’d) |
| `Option Strict Off` legacy files; new files On | project-wide | Confirmed; `AppSecrets.vb` follows the convention |
| Secrets externalization | `AppSecrets.vb`, `.gitignore:366`, conditional copy in `.vbproj` | Verified clean; `secrets.json` never committed |
| TradeRecord slippage fields never persisted | `TradeRecord.vb:15-19` | Confirmed |

Also verified as **fixed and holding**: null-safe id-3 guard (`:448`), refresh single-flight + send-only refresh (`:408-439`), reposition single-flight incl. gate ordering (`:1210-1213`), `BackoffStopLossRetry` (`:1144`), cancel-pending lifecycle (set `:2531`, echo-clear `:1875`, placement-clear `:2485`, 4s self-clear `:106`), seed-only-when-zero echo writers (`:1641`, `:1679`), checkbox/input mirrors (`:128-166`).

---

## F. VerdictEngine tie-in — what this audit means for the incoming project

The `spec-medium-decouple.md` API surface is validated by this trace as the right contract. Three additions from the audit:

1. **Thread contract (the big one).** `ExecuteOrderAsync` reads controls directly (`txtAmount:2167`, `txtManualTP:2187`, etc.). Today’s auto path is only thread-legal because `PerformClick` happens on the UI thread. VerdictEngine will call from its own socket/timer threads — so `PlaceAutomatedOrder(...)` must **marshal to the UI thread internally** (or `ExecuteOrderAsync` must be converted to the mirror fields first). If this isn’t in the #10 spec explicitly, add it; otherwise the first automated order from the new engine is a cross-thread bug under the debugger and UB without it.
2. **Order-ack semantics.** Give `PlaceAutomatedOrder` a way to know the order was *accepted* (F2). Minimum viable: unique request id + surfacing the error/result for that id. Without it the engine can’t distinguish “resting at top of book” from “rejected”.
3. **Requirements to carry over** (from the dying module + this audit): single guarded connect/reconnect loop (old #11); fragmentation-safe receive (F5 — copy `FrmIndicators`’ StringBuilder pattern); settings from config file per the owner’s config philosophy — note today’s `AutoTradeSettings` values are consumed as *textboxes-as-database* and `txtCooloff` is read **once** at `FrmIndicators` load (`FrmIndicators.vb:77`), so hot-reload is already broken in the current design; don’t replicate that.

Current consumed settings inventory (the re-code’s minimum surface): `txtCooloff`, `txtCircuitBreaker`, `txtATRLimit`, `txtLScore`/`txtSScore`, `txtTrendStrength`, `txtATR` (period), `txtTP`/`txtSL` (ATR multiples for the paste), `txtStartTime`/`txtEndTime` (UTC+8 restriction window).

---

## G. Recommended sequencing

1. **Finish the in-progress runtime tests** of the current bundle (unchanged).
2. **Micro-bundle the cheap high-value fixes** — F1 (entry-price snapshot), F3 (checkbox gate), F6 (`reconnectNeeded = True` on Close), F4 (edit-button ids/returns), F2’s generic error *logger* (not the rollback). All are small, isolated, and testable in one debugger session. Opus or Fable, high effort, one spec.
3. **#10 decouple** as planned, with §F’s thread-contract and ack-semantics additions folded into the spec. This is still the highest-value item and the VerdictEngine prerequisite.
4. **Resilience pass** (F7 keep-alive timeout, F8 limiter init, F12 crash handler, F9 monitor leak) — one medium spec, anytime after 2.
5. Housekeeping (D items + existing medium-housekeeping spec) — Sonnet-grade, whenever idle.
6. #9 dispatcher: unchanged recommendation — defer/skip until after VerdictEngine lands; F15 (unique ids) is its precondition anyway.

---

## H. Working with Fable 5 on this repo — effort guidance

For the owner’s spec-header convention (model/effort per task):

| Task class | Effort | Why |
|---|---|---|
| Anything touching `frmMainPageV2` order/SL/receive paths (specs 2–4 above, #10 review) | **high** | The traps here are exactly the kind extra reasoning catches: VB nullable semantics, invariant ordering, Invoke-vs-BeginInvoke state races |
| Day-to-day: impl-report review, log diagnosis, spec writing | **medium** (default) | Fable at medium covers what previously needed Opus-high on this codebase |
| Mechanical housekeeping, doc edits, memory updates | **low** | No reasoning depth needed; the compile gate catches slips |
| #9 dispatcher rewrite (if ever) | **xhigh** | Rewrites the hot path all §5 invariants protect |

Token economics: the effort knob changes thinking depth, not file-reading cost — and on this repo **context is the bigger spend** (`frmMainPageV2.vb` alone is ~67k tokens). This audit was the deliberate full read; routine sessions shouldn’t repeat it. Use the section banners + this doc + `CODE_AUDIT.md` as the map, read regions on demand, and review via `git show` + targeted greps rather than re-reads. Keep specs self-contained (as already practiced) so implementer conversations never need the full file.
