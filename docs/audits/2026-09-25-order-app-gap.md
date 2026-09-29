# Order-app gap audit: stop lifecycle, chase, stand-down, restore (2026-09-25)

**Audited at:** `8232e9e`, read from a detached worktree at `/tmp/app-8232e9e`. Nothing newer is in scope.
**Follows:** `docs/audits/2026-09-24-signal-to-exchange-order-path.md` on branch `claude/gracious-fermat-w8sacs` (called "the first audit" below). Its findings 1–12 are not re-reported. Where a finding here touches one of them, the overlap is named.
**Proofs:** `docs/audits/proofs/order-app-gap/`. It is an isolated .NET 8 harness. It compiles 37 verbatim line ranges of `frmMainPageV2.vb` (4,177 lines) plus six whole app files (`SignalBridge.vb`, `SessionPolicy.vb`, `ExecutorFeedback.vb`, `RemoteNotifier.vb`, `AppSecrets.vb`, `AppUserSettings.vb`), and stubs out only WinForms, the socket and the log, alert and DB sinks. Its README has the run command and the full output. "Proven by run" below means that harness, on this machine, on 2026-09-29.
**Line references** are `frmMainPageV2.vb:N` unless another file is named.

## Not read in full

Every member the brief listed was read in full: `UpdateLimitOrderWithOTOCOAsync`, `UpdateEntryOrderOnlyAsync`, `UpdateStopLossForTriggeredStopLossOrder`, `ForceStopLossUpdate`, `SendRateLimitedUpdate`, the whole of `HandleOrderPositionUpdates` (3071–3678), the startup restore (`HandleOpenOrdersSnapshot` 5721–5860 and `ProcessPositionData` 5862–5974), `ApplyCloseFill`, `CompletePositionClose`, and all of `SessionPolicy.vb`, `AutoTradeSettings.vb` and `AppUserSettings.vb`.

Not read in full:

| File / member | What I did read |
|---|---|
| `FrmIndicators.vb` 371–1235: `UpdateDmi`, `UpdateMacd`, `UpdateRsi`, `UpdateStochastic`, `EvaluateEmaVwapSignals` | Nothing. **They run before `UpdateATR` inside `UpdateSignals` (`FrmIndicators.vb:331-336`), so if any of them throws, the fallback ATR silently stops updating (`UiInvokeSafe` swallows, `:193-199`). I did not check whether any of them can throw.** |
| `FrmIndicators.Designer.vb` | Nothing |
| `frmMainPageV2.Designer.vb`, `AutoTradeSettings.Designer.vb` | Grep of the `.Text` / `.Checked` defaults only |
| `frmMainPageV2.vb` 6083–6164, 6176–6209, 6223–6262, 6271–6392, 6441–7115 (manual buttons, trade grid, margin estimate, `InitializeRateLimits`) | Nothing. Everything else from 1–6082, plus 6165–6175, 6210–6222, 6263–6270 and 6393–6440, was read in full |
| `ExecutorFeedback.vb` | 180–340 only (`BuildSnapshot`, `Serialize`) |
| `RemoteNotifier.vb`, `AppSecrets.vb` | Grep only (`IsConfigured` / `NtfyUrl`, `WsUrl`) |
| `TradeDatabase.vb`, `TradeRecord.vb`, `WsEdgeLog.vb`, `ApplicationEvents.vb`, `tools/OrderCheck/Program.vb` (past line 80) | Nothing |
| Every `docs/*.md` in this repo except the first audit | Nothing. That includes `docs/HANDOVER-6.md` and the specs the code comments cite |

## --- LOGIC TRACE ---

**Inputs.** A STRONG LONG arrives during a −1.5 % flush: engine entry 59,003.5, stop 59,001.5, target 59,079.15, ATR 120.2. Everything else is shipped config: Amount 10 USD, `txtStopLoss` 30, `txtTriggerOffset` 30, `txtTakeProfit`/`txtTrigger` 60, M.SL ticked at 70, ATR slippage guard ticked at 0.6, risk sizing off (`frmMainPageV2.Designer.vb:186-410`, `:1052`; `AutoTradeSettings.vb:32-46`). The app's best bid when the payload lands is 58,998.0, which is where the fill happens. Fees: maker 1.5 bp, taker 3.5 bp. Every number below is harness output (scenario `trace`) unless marked *derived*.

| t | What happens | Code | State / numbers |
|---|---|---|---|
| act | The bridge derives `manualTP = RoundToTick(59079.15) = 59079.0` and `manualSL = RoundToTick(59001.5 − 30) = 58971.5`. The gates pass and the disposition is `acted (id ORD-E1)` | `SignalBridge.vb:809-822`, `:975-977` | size 10 USD (Amount box) |
| place | Buy limit, post-only, at the **app's bid 58,998.0**, with OTOCO `first_hit`. TP: sell limit 59,079.0, post-only, **not reduce-only**. SL: `stop_limit`, `trigger_price` 59,001.5 (= 58,971.5 + `txtStopLoss` 30), `price` 58,971.5, `trigger_offset` 30, `trigger:"last_price"`, reduce-only, post-only | `:3821-3860`, `:4059-4108` | Slippage anchor 58,998.0, cap 0.6 × 120.2 = **72.12**. `legReanchorDriftMax = min(60,60)/2 = 30`. **The stop trigger is 3.5 ABOVE the entry order.** Nothing checks this (first audit finding 2) |
| 0 s | Fill at 58,998.0. The position is +10, avg 58,998. `plannedStopAtEntry` = 59,001.5. `manualTP`/`manualSL` are zeroed, the entry ids are cleared, and the status reads "In Position" | `:3089-3124`, `:3435-3475`, `:3572-3610` | feedback: `working.target=0`, `working.stop=58971.5` (G12) |
| 0 s | The SL leg activates with last ≤ trigger. What the exchange does next is unverified. **(i)** It rejects the whole OTOCO at placement → `HandlePlacementResponse` rolls back, disposition `rejected: <code>`. **(ii)** It triggers at once → the post-only sell at 58,971.5 would cross the 58,997.5 bid, so it is repriced one tick above the bid = 58,998.0. **(iii)** It rejects or cancels the leg → nothing happens in the app (Q1, G2). **(iv)** `trigger_offset` re-bases the trigger to about peak − 30 ≈ 58,968, so the stop runs 33.5 below the engine's (first audit finding 6). The harness scripts (ii) | `:3217-3310` | (ii): `SLTriggered=True`, `placedStopLossPrice=58998`, baseline 58,998. **The TP at 59,079 is now unreachable: the stop sits at the ask, and any uptick fills it first** |
| 0–3 s | The flush continues at 5 USD/s. **8 chase edits** (`private/edit` id 223350) move the stop 58,997.5 → 58,983.5, one about every 0.40 s (333 ms throttle on 100 ms ticks). Each is post-only at bid + 0.5. The M.SL anchor latches at the **first** chase price, 58,997.5 | `:2523-2714`, `:4744-4765`, anchor latch `:2665-2668` | Emergency cap armed at ask ≤ 58,927.5 |
| 3–23 s | The socket drops. **0 frames are sent.** The chase and the M.SL check only run when a quote message arrives (`:1533` → `:2308`). The resting sell at 58,983.5 hangs above a market falling to 58,882.5 | `:1507-1603`, `:2523` | *Derived:* with the socket up, the cap would have fired at about t = 14.1 s (bid 58,927.0) |
| reconnect | Handling depends on how the drop shows up. **Detected close:** retries start at +2 s and then back off 2/4/6/8 s (`:1340`, `:1370`). With fast failures, the first attempt that can succeed after the network returns at t = 23 s is at t ≈ 25 s. With hanging connects (30 s timeout, `:1417`), it is t ≈ 37 s. **Silent stall of ≤ 20 s:** the keepalive never trips (`:1409-1412`), and the backlog arrives in one burst. The harness reconnects at 23 s | `:1322-1490` | |
| 23 s | id-777 logs "Open position detected". id-778 logs "Restored order context … SL=ORD-SL2@58983.50 (triggered)". The single-writer rules keep the pre-drop state | `:5862-5901`, `:5721-5860` | |
| 23 s | First quote, bid 58,882.5. The hoisted check computes 58,997.5 − 58,883.0 = 114.5 ≥ 70 → **`cancel_all_by_instrument` + reduce-only market sell 10** → log line "Emergency Sell Market Order Executed." | `:2585-2590`, `:4675-4687` | |
| close | The reduce fills at 58,880.0 (scripted). The close runs through `ApplyCloseFill` → `CompletePositionClose` → DB row `exit=58880 pl=-0.02 plannedStop=59001.5 R=-33.71` | `:3527-3538`, `:5432-5591` | **A 118 USD adverse price move = −20.0 bp gross, −25.0 bp net, against a planned 2 USD (0.34 bp) stop.** *Derived counterfactual without the drop:* exit ≈ 58,924.5, −12.5 bp gross / −17.5 bp net. The drop cost about 7.5 bp |
| variant | **M.SL unticked:** the chase follows the market down to an SL at 58,833.0 after 10 more seconds (25 edits). Nothing caps it | `:2586`, `:4675` | `trace-msl-off` |
| variant | **The emergency's first send fails:** the app still logs "Emergency Sell Market Order Executed." and sends an urgent notification. `SLTriggered` is cleared and `emergencyFired` latches. The 2.5-s reconnect's restore is **skipped** because the raw `cancelPending` is still set. The next 300 USD of fall produce 0 frames | G1 | `trace-emergency-send-fails` |
| variant | **The reduce is rejected (10028):** the long has no orders at all, and the next −380 USD produce 0 frames | G1 | `emergency-rejected` |

Branch (ii) has no winning path. The best case is an uptick in the first second: the stop fills as maker at 58,998.0, for 0 bp gross and −3.0 bp net. Otherwise the loss runs to the 70 USD cap plus the market-order slippage, or further if the socket drops.

## Answers

**Q1. If Deribit rejects or cancels the stop leg after the entry fills, what notices, how fast, and what does the app do?** *(proven by run `q1`)*

Nothing notices, ever.

A post-fill leg failure can only arrive as a `user.changes` notification. The placement ack was already consumed at `:1910`, and `HandleUnhandledJsonRpcError` only sees JSON-RPC error responses (`:1759-1763`).

- **`rejected`:** `HandleOrderPositionUpdates` has no branch for it. Its states are `open` (`:3155`), `untriggered` (`:3386`), `filled` (`:3430`) and `cancelled` (`:3541`), so the echo falls through. The run shows 0 log lines and 0 alerts.
- **`cancelled`:** the branch clears `cancelPending` for any label (`:3545`) and sets `OpenPositions = True` (`:3549-3550`). That block zeroes manual TP/SL and entry ids that are already zero (`:3576-3610`). The run shows 0 log lines and 0 alerts.

In both cases:
- `PositionSLOrderId` still points at the dead leg.
- `SLTriggered` stays False, so the triggered-SL chase and the M.SL emergency, which are both gated on `SLTriggered AndAlso PositionSLOrderId IsNot Nothing` (`:2523`), never run.
- A 298 USD flush afterwards produced **0 frames**.
- The status label still reads "In Position".
- `executor_feedback.json` keeps reporting `working.stop = 58971.5` (`:739-757`; `ExecutorFeedback.vb:257-262`).

No periodic reconciliation exists. The only order re-read happens at (re)connect and in the id-31 raced-abort repair (`:1831-1834`), and neither checks that a stop exists (G10).

**Q2. Once the post-only stop_limit triggers, how is it chased, how often, and until when? What bounds the loss with `chkMarketStopLoss` off, or if the WebSocket drops mid-chase?** *(proven by runs `trace`, `trace-msl-off`)*

**How it is chased.** On each quote tick, with the socket Open, no cancel pending, `SLTriggered` and a `PositionSLOrderId` (`:2523`):
- It takes one tick above the bid as the target, `bestBid + 0.5` (`:2621`).
- It edits only when the target is **below** the current reference (`:2622`). A long's stop only ever moves down.
- The edit is `private/edit` id 223350 with `price`, `amount = |position|`, `post_only:true, reject_post_only:false`, and no trigger fields (`:4744-4757`).

**How often.**
- At least 333 ms between attempts (`:2219`, `:2606`), single-flight (`:2641`).
- If the rate limiter is empty it waits up to 3 s, then forces the edit (`:4707-4716`).
- A red 223350 rejection escalates the throttle from 666 ms to 5 s (`:1866-1878`, `:2239-2249`). The counter resets only on a confirmed echo (`:3302-3304`).
- The run: one edit about every 0.40 s.
- Once `priceMovement` ≥ 35 (half the cap), it switches to `ForceStopLossUpdate`, whose "bypass" is undone by the caller (G19).

**Until when.**
- The SL fill echo (`:3481-3485`).
- The emergency firing, which runs `CancelOrderAsync`; that clears `SLTriggered` at `:4251`.
- A pending cancel.
- The socket leaving Open.

There is no time limit and no distance limit except M.SL.

**The only cap is M.SL** (`:2585-2590`), and only while ticked (`:2586`) and threshold > 0. It measures `anchor − bestAsk ≥ 70` against an anchor that latches to the first chase price (`:2665-2668`), or to whatever the restore finds (`:5830-5833`).

**With M.SL off, nothing bounds the loss.** The post-only sell is re-laid one tick above the bid and fills only on an uptick. In the run it reached 58,833.0 after 10 more seconds with no cap (`trace-msl-off`). `TryStart` does not require M.SL (first audit finding 4).

**With the WebSocket down, nothing runs at all.** The chase and the cap are both quote-driven, and the run sent 0 frames in 20 s. After a reconnect, the cap fires on the first quote if the market is already 70 below the anchor. If the drop outlasts the reconnect loop, nothing ever resumes (G4). A silent stall of ≤ 20 s is never even detected (G9).

**Q3. What happens to a working entry when the bridge stands down (stale payload, SKIPPED, engine ARM off)?** *(proven by run `standdown`)*

All three causes, and mode Off and local ARM off, only call `ForceStop`, which clears `_started`, logs, and sends an urgent notification (`SignalBridge.vb:521-533`; callers `:640`, `:665`, `:669`, `:513`, `:202`, `:229`). No cancel is sent. The public `CancelWorkingEntryAsync` (`:938-940`) has no caller in the bridge.

The entry is then **actively chased**, not left resting. The entry-chase gate (`:2366-2369`) reads no bridge state, so each uptick re-prices the stood-down bid. The run shows 8 edits after SKIPPED and 8 after ARM-off.

The cap measured against it changes with every stand-down payload. `GetEffectiveAtr` is re-read per tick (`:3690-3718`):
- SKIPPED and stale zero the payload ATR (`SignalBridge.vb:627-634`), so the cap falls back to `FrmIndicators` or to a flat 70.
- An ARM-off payload that is fresh and OK **refreshes** it (`:623-626`).

The run went 72.12 → 70 → **90** → 70. The only thing that eventually cancelled the entry was the ATR guard (`:2383-2386`), when the drift reached 72 > 70.

While the entry rests:
- The executor feedback says `started=False direction=FLAT` with zero stop and target (`ExecutorFeedback.vb:250-256`), so the engine cannot see the order.
- The staged signal tag survives, and a later fill is attributed to the stale signal (`:3114-3117`).
- Fresh signals are refused with `refused: working_entry` while it rests (`SignalBridge.vb:748-749`).

**Q4. Walk a long whose stop trigger is at or above its fill through every function above.** *(proven by runs `trace`, `q1`, `q5`, `restore`, `close-in-gap`)*

- **`ExecuteOrderAsync` (`:3821-3860`, `:4059-4108`)** places the trigger at `manualSL + txtStopLoss` = 59,001.5 over an entry at the bid of 58,998.0. There is no side check. It seeds `StopLossTriggerOriginal = 59,001.5` (`:4054`).
- **`UpdateEntryOrderOnlyAsync` (`:4516-4554`)** moves only the entry. In a flush it is never called, because the chase only moves a long's entry up (`:2373`). If the market bounces, the chase lifts the entry above the trigger, which "fixes" the side while shrinking the distance to the TP (first audit finding 3).
- **`UpdateLimitOrderWithOTOCOAsync` (`:4428-4510`)** runs once the drift reaches 30. It re-sends the TP at `manualTP` and the SL at `manualSL` with trigger `manualSL + 30`. With bridge levels these are unchanged values: two wasted matching-engine requests per re-anchor (run `q5`, G20). It resets `StopLossTriggerOriginal` and `emergencyBaseline` (`:4493-4495`). It only runs after an up-move, so the re-sent trigger is valid by then.
- **`SendRateLimitedUpdate` (`:4605-4631`)** sends each edit and swallows send exceptions. Responses share ids 223344/223346 (G20). A red 223346 feeds the SL backoff (`:1866-1869`).
- **`HandleOrderPositionUpdates`, filled branch (`:3435-3475`)** logs "Position entered". For bridge trades there is no TP re-anchor, because `manualTP > 0` (`:3473`). The `OpenPositions` block zeroes `manualTP`/`manualSL` (`:3576-3581`), which zeroes the feedback target (G12).
- **Exchange branch (i), rejected at placement:** `HandlePlacementResponse` rollback (`:1923-1943`). This is the only safe branch.
- **Exchange branch (ii), triggered at once:** the open echo sets `SLTriggered`, adopts the price and re-arms the anchor latch (`:3229-3266`). Then the next three items apply.
- **`UpdateStopLossForTriggeredStopLossOrder` (`:4664-4781`)** chases the stop down from 58,998.0, as in the trace.
- **`ForceStopLossUpdate` (`:5188-5193`)** takes over at ≥ 35 below the anchor.
- **The emergency** fires at ≥ 70 below the anchor: cancel-all, then a reduce-only market order, then a log line saying "Executed" whatever happened (G1).
- **Exchange branch (iii), rejected or cancelled at activation:** as in Q1. Nothing notices, nothing chases and nothing caps. **An unprotected long** (G2).
- **Exchange branch (iv), trailing re-base:** the app never learns the live trigger. `StopLossTriggerOriginal` stays 59,001.5, and it feeds the emergency baseline before the first open echo (`:2545`) and the journal R (`:5539-5546`).
- **`ApplyCloseFill` (`:5432-5448`)** records the close price and size from the fill. For `StopLossOrder` it reads `price`, not `average_price` (`:3483`), which only matters if a maker fill ever differs from the limit.
- **`CompletePositionClose` (`:5455-5591`)** records `plannedStop=59001.5`, a stop above a long's entry, and computes R with `Math.Abs`, which gave R = −33.71 in the trace (G13). It cancels all orders and starts the cooloff. It only runs if the flat transition is seen in a `user.changes` echo (`:3661`). A close during a socket gap never gets here (G7).
- **Startup restore (`:5721-5860`)** re-adopts an `open` SL as triggered and an `untriggered` one as pre-trigger. It never checks that a stop exists, or that it sits on the right side of the average entry (G10).

**Q5. How does the chase treat `trigger_offset`?** *(proven by run `q5`; the exchange semantics are unverified)*

- It never touches it. The pre-fill entry chase does not edit the SL leg.
- The full-bracket re-anchor edits the SL's `price` and `trigger_price` only (`:4503`, `:4612`). The run frame is `{"order_id":"SLTS-1","price":58971.5,"amount":10.0,"trigger_price":59001.5}`, with no `trigger_offset`.
- The triggered chase edits `price`/`amount` only (`:4748-4754`).
- Post-fill, the SL leg is deliberately not re-anchored, because "the exchange trails it" (`:3464-3465`, `:4559-4560`). Nothing reads a trailed trigger back. The untriggered echo mirrors whatever `trigger_price` the exchange pushes (`:3414`), but the app's own comment on its `trailing_stop` order says the channel does not push trigger moves (`:3419-3420`). If the same holds for a trailed `stop_limit`, `StopLossTriggerOriginal` goes stale on the first trail.
- The app's trigger arithmetic uses only `txtStopLoss` (`stopLossOffset`, `:3842`, `:4472`). `txtTriggerOffset` goes to the exchange as `trigger_offset` (`:3855`, `:4086`) and appears in no other calculation. Both default to 30, which hides this. If the exchange trails, the live trigger is peak − `txtTriggerOffset`, a number the app neither computes nor reads.

## Findings

Each finding has an ID of the form "G*n*" (a gap-audit finding, `D`-class in this repo's convention: a defect in code), always written next to its title.

### G1: the M.SL emergency cancels first, fires once and never checks the result, so a failed market reduce leaves a naked long while the app reports it closed

- **SEVERITY:** S0
- **LOCATION:** `:4675-4698` (latch `:4680`, `CancelOrderAsync` `:4681`, `SendReduceMarketOrderAsync` `:4683`, "Executed" log, alert and urgent notification at `:4684-4686`, all unconditional). `:4210-4255` (`CancelOrderAsync` clears `SLTriggered`, the anchor and the trigger after its send, whether or not the send landed). `:1293-1314` (send failures are swallowed). `:6397-6400` (the reduce is skipped when the socket is not Open). `:1728-1749`, `:1840-1844` (a reduce rejection is only logged). `:3546-3557` (a `cancelled` `ReduceMarketOrder`, i.e. a partial market fill, has no case). `:5728` (the restore is skipped while the raw `cancelPending` is set). `:2523` (the whole SL block needs `SLTriggered`).
- **DOWNSTREAM IMPACT:** once the emergency fires, the SL leg is gone (cancel-all), the app-side chase is off (`SLTriggered=False`) and the emergency can't fire again (`emergencyFired` is cleared only at `:5468`, `:4138` and `:5071`). If the reduce doesn't fully fill, the position has no protection of any kind, and the log, the taskbar alert and the ntfy notification all say it was closed.
- **FAILURE SCENARIO:** two routes, both run.
  1. *Rejection* (`emergency-rejected`): the cap fires at bid 58,917.5, cancel-all succeeds, and the reduce comes back `10028 too_many_requests`. The exchange now holds a 10 USD long with **no orders**, and the next −380 USD produced **0 frames**.
  2. *Send failure* (`trace-emergency-send-fails`): the cancel's send throws. `CancelOrderAsync` resets state anyway. The reduce is "skipped - not connected", yet the app logs "Emergency Sell Market Order Executed." and posts an urgent notification. The cancel never reached the exchange. The reconnect 2.5 s later hits `If cancelPending Then Return` at `:5728`, so the triggered stop is **not re-adopted**, and the next 300 USD of fall produced **0 frames**. Even when the snapshot is re-delivered after the 4-s window, `emergencyFired` stays latched: 450 USD below the new anchor, it sent 3 chase edits and no market reduce.
- **ANALYTICAL CRITIQUE:** a backstop that destroys the primary stop before it knows the replacement worked is worse than no backstop. The ordering should be reduce first (reduce-only can't overshoot), cancel on confirmed flat, and re-arm on failure. The "Executed" message is the worst part: it tells the one human who could intervene that there is nothing to do.
- **EVIDENCE:** proven by run (both routes). The partial-market-fill route is read from code.

### G2: no position-level loss cap. The only app-side backstop is keyed to the SL order's lifecycle, so a dead stop leg is uncapped, unchased and invisible

- **SEVERITY:** S0. The trigger needs the exchange to reject or cancel the leg. Whether it does that for a crossed sell-stop at OTOCO activation is unverified; a web-search summary of Deribit's "Stop-Limit" support page says a sell stop above the last price is rejected at placement.
- **LOCATION:** `:2523` (the chase and M.SL gate). `:3230`, `:5819` (the only `SLTriggered = True` writers). `:3155`/`:3386`/`:3430`/`:3541` (no `rejected` branch). `:3541-3557` (`cancelled` → `cancelPending = False` and `OpenPositions` only). `:739-757` with `ExecutorFeedback.vb:257-262` (the dead leg's price is published as `working.stop`).
- **DOWNSTREAM IMPACT:** the M.SL cap only exists after an `open` SL echo. For an untriggered leg that dies, whether rejected, cancelled, removed by OCO, or cancelled by hand in the Deribit UI, the app does nothing and says nothing. The engine is told a stop is working.
- **FAILURE SCENARIO:** run `q1`. The signal-B long fills at 58,998. The SL leg comes back `rejected`, or `cancelled`. Result: 0 log lines, 0 alerts, 0 frames over a 298 USD flush, the status reads "In Position", and the feedback says `working.stop=58971.5`.
- **ANALYTICAL CRITIQUE:** the first audit's finding 7 (a post-fill stop failure raises no alarm) is the missing alarm. This finding is the missing **action**: even the app's own backstop can't see a position that has no triggered stop order. A loss cap belongs on the position (`positionSizeUSD`, `positionAvgEntry`), not on the SL order's state machine.
- **EVIDENCE:** proven by run.

### G3: a partial entry fill arms full-size exit legs, one of them not reduce-only, and the echo handling is order-dependent. A TP fill can flip the account short with no stop

- **SEVERITY:** S1. It is the S0 class as soon as the size is above 10 USD (Amount > 10 or risk sizing on). The shipped 10 USD is one contract and cannot partially fill.
- **LOCATION:** `:4066` (`trigger_fill_condition: first_hit`). `:4068-4077` (TP leg, full `amount`, no `reduce_only`; `:4101` explains why it was removed). `:3181-3182` vs `:3196-3197` (Entry-`open` sets `OpenPositions=False`, TP-`open` sets it True, and the last one in the array wins). `:3602-3610` (ids nulled). `:4289-4292` (the scoped cancel assumes the children are still untriggered).
- **DOWNSTREAM IMPACT:** with `first_hit`, Deribit places both secondaries for the **full** primary amount on the first partial fill. That is a web-search summary of Deribit's "Order attributes" support page, not verified against the docs. Two outcomes, depending on the array order:
  - The remainder is orphaned: no chase, no ATR guard, `HasWorkingEntryOrder=False`, no cancel ever.
  - Or the remainder is still chased with 500-size edits, which also re-send the now-live TP/SL legs.
  Either way, a full-size TP fill against a small long leaves a **short with no stop**: OCO has cancelled the SL, and a reduce-only sell could not protect a short anyway. `TradeMode` still says LONG and no alert fires.
- **FAILURE SCENARIO:** run `partial`, Amount 500. The first fill is 10 at 58,998, and there is no "Position entered" line and no alert. The market bounces to 59,078. In one ordering, 0 frames are sent. In the other, 9 entry edits and two bracket re-sends (four leg edits at size 500) go out, then an ATR cancel at a drift of 74. The TP (500) then fills at 59,079 and the position becomes **−490**. 0 alerts. A 400 USD rally against it produced **0 frames**.
- **ANALYTICAL CRITIQUE:** the TP was made non-reduce-only to work around one exchange behaviour, which created a second, worse one. The legs have to track the filled quantity (`incremental`, or resize on each fill), and a TP must never be able to open a position.
- **EVIDENCE:** proven by run for the app side. The exchange's `first_hit` placement is scripted, per the web-search summary.

### G4: the reconnect loop gives up for good after 10 attempts, notifies no one remotely, and nothing ever retries while the position stays open

- **SEVERITY:** S1 (a silent total outage of position management)
- **LOCATION:** `:1322-1387` (10 attempts; delays 2 + 2/4/6/8/10/10/10/10/10 s, 72 s in total; each `ConnectAsync` can take up to 30 s, `:1417`; after that only `Alert("connection")`, a local sound and flash, `:1376-1378`). Callers of `HandleWebSocketDisconnect`: `:1303`, `:1312`, `:1598`, which are send failures and receive-loop exit, and none of them can occur once the socket is dead. `RemoteNotifier.Post` is never called on disconnect (grep). `:1177-1180` (`AuthorizeWebSocketConnection` waits for the auth reply with no timeout, so one attempt can hang the loop).
- **DOWNSTREAM IMPACT:** the chase, the M.SL cap, echo handling and close completion are all dead, and they stay dead until someone presses Connect.
- **FAILURE SCENARIO:** a 2-minute network or DNS outage, or a Deribit maintenance window, with a position open. The fast-failing attempts are used up after about 72 s, and the app sits disconnected indefinitely. On the AWS host the comments describe (`:1028-1030`), the local sound reaches nobody.
- **ANALYTICAL CRITIQUE:** a trading process that holds a position must retry forever, with capped back-off, and page someone. Giving up is a choice to stop managing risk.
- **EVIDENCE:** read from code.

### G5: stand-down does not freeze the working entry; the app keeps re-pricing it, and each stand-down payload re-sizes the chase cap

- **SEVERITY:** S1 (systematic stale-data orders)
- **LOCATION:** `SignalBridge.vb:521-533` (`ForceStop` only), `:623-634` (payload ATR zeroed on stale/SKIPPED, refreshed on ARM-off), `:640`. `frmMainPageV2.vb:2366-2408` (the chase gate reads no bridge state). `:3690-3718` (the ATR source is re-read per tick). `ExecutorFeedback.vb:250-256` (flat means zeros, and there is no working-entry field).
- **DOWNSTREAM IMPACT:** this goes beyond the first audit's finding 1 (the entry is never cancelled). The stood-down order is actively chased toward the market. Its abort threshold moves between the engine ATR, the app's own (understated, G11) ATR, and a flat 70 USD, depending on which stand-down payload arrived last. The engine sees FLAT and `started=false`.
- **FAILURE SCENARIO:** run `standdown`. Placed at 58,998, cap 72.12. SKIPPED → cap 70, 8 re-prices. ARM off (fresh payload, ATR 150) → cap **90**, 8 more re-prices. Stale → cap 70, cancelled at a drift of 72. The feedback read `started=False direction=FLAT` throughout.
- **ANALYTICAL CRITIQUE:** "stand down" has to mean stop acting on the old signal. Chasing it is acting on it, and letting a payload that disarms the engine widen the cap inverts the intent.
- **EVIDENCE:** proven by run.

### G6: position management takes its direction from the UI Buy/Sell toggle (`TradeMode`), not from the position

- **SEVERITY:** S1
- **LOCATION:** `:6263-6270` (`btnSell_Click`/`btnBuy_Click` call `SetTradeMode` with no position guard). `:2550-2554`, `:2617-2633` (emergency and chase direction). `:4675`, `:4688` (the emergency branches). `positionSizeUSD` is available and unused here.
- **DOWNSTREAM IMPACT:** one click flips the chase formula and the cap to the short side while a long is open. Adverse moves are then ignored. A favourable 70 USD move fires the cap and market-sells the long, and the log calls it "Emergency Buy". The same mismatch exists with no click after G3's flip (short position, `TradeMode` LONG).
- **FAILURE SCENARIO:** run `mode-flip`. Long, SL triggered, click Sell. The market falls 200 USD: **0 frames**. It then rallies 300: the stop is edited **up** to 59,060.5, then cancel-all plus a market sell, logged as "Emergency Buy Market Order Executed.".
- **ANALYTICAL CRITIQUE:** risk logic keyed to a cosmetic control. The sign of `positionSizeUSD` is the only valid direction for managing an open position.
- **EVIDENCE:** proven by run.

### G7: a position that closes during a socket gap is never completed

- **SEVERITY:** S2
- **LOCATION:** `:5873-5878` (`ProcessPositionData` writes `positionSizeUSD` with no transition detection). `:3661` (the only caller of `CompletePositionClose`). `:4054-4056`, `:4135-4138` (a new placement resets neither `SLTriggered` nor `PositionSLOrderId`).
- **DOWNSTREAM IMPACT:**
  - No DB row and no close alert or notification.
  - The cooloff is never anchored (`SignalBridge.vb:1167-1170`), so the bridge re-enters at once.
  - The triggered-SL context survives while the account is flat: edits to the dead order, then a phantom emergency. That phantom sends cancel-all, which would kill a new working entry, plus a reduce-only market sell of the Amount box ("Position model empty - using TradeMode/txtAmount fallback"), logged "Executed".
- **FAILURE SCENARIO:** run `close-in-gap`. The SL fills during the gap, and the reconnect sees size 0 and no orders. No `RecordCompletedTrade` is written. Signal #103 → `acted`. The same close observed normally → `refused: cooloff`. Then, flat, a 90 USD fall sent 5 dead-order edits plus cancel-all plus a market sell.
- **ANALYTICAL CRITIQUE:** the flat transition is detected in exactly one place, and that place only sees live echoes. Any size change the snapshot path observes has to go through the same transition logic.
- **EVIDENCE:** proven by run.

### G8: the M.SL cap is a fixed 70 USD from an anchor the chase chooses, with no relation to the engine's stop or ATR

- **SEVERITY:** S2
- **LOCATION:** `:2535-2546`, `:2585` (threshold `txtMarketStopLoss`, default 70, `frmMainPageV2.Designer.vb:242`). `:2665-2668` (the anchor latches to the first post-trigger chase price). `:5830-5833` (after a restore, the anchor is whatever price the resting SL has).
- **DOWNSTREAM IMPACT:** for the 2 USD engine stop, the cap sits 74 USD below the engine's stop, 37× the planned 2 USD risk. Risk sizing (first audit finding 5) sizes on the 2 USD distance. After each reconnect the anchor re-bases to the restored order price, so the cap moves.
- **FAILURE SCENARIO:** the trace. Anchor 58,997.5, cap at ask ≤ 58,927.5, a realised −25 bp net with the drop against a planned 0.34 bp, and −17.5 bp net without the drop (*derived*).
- **ANALYTICAL CRITIQUE:** the first audit's finding 4 noted that the fallback exists. Its size is the problem: a cap that doesn't scale with the signal's own stop is a disaster brake, not a stop.
- **EVIDENCE:** proven by run for the anchor and cap numbers. The counterfactual is derived.

### G9: blind to a stalled socket. Protection is quote-driven, there is no staleness watchdog, and "connected" means only `State = Open`

- **SEVERITY:** S2
- **LOCATION:** `:1409-1412` (keepalive 30 s plus pong timeout 20 s: a stall of ≤ 20 s is never detected, a longer one takes up to 50 s). `:678-682` (`IsWebSocketConnected`, which every gate reads, including `SignalBridge.vb:742`). No time-since-last-quote check exists (grep). `:3824-3829` (placement uses `BestBidPrice` with no age check). `:1533` → `:2523` (the chase and M.SL only run on a quote message).
- **DOWNSTREAM IMPACT:** during a stall the app believes it is connected. The bridge can still act on a stale bid, and nothing re-prices or caps anything. After the stall, the backlog is processed in one burst of stale quotes.
- **FAILURE SCENARIO:** the trace's 20 s gap sent **0 frames** and cost about 7.5 bp against the counterfactual cap. Under a silent stall the app would not even log a disconnect.
- **ANALYTICAL CRITIQUE:** the first audit's finding 4 says a socket drop leaves the loss unbounded. This finding is about knowing that it happened. A 1–2 s quote-silence watchdog that stands the bridge down and alerts is the minimum.
- **EVIDENCE:** 0 frames during the gap proven by run. The detection timing is read from code plus my understanding of .NET 9 `KeepAliveTimeout`, which I did not verify.

### G10: the restore scan never checks that the position has a stop, and it reads an empty order list as a flat restart

- **SEVERITY:** S2
- **LOCATION:** `:5736-5737` (`result.Count = 0` → silent `Return`, commented "flat restart"). `:5848-5850` (at most one cyan line naming `SL=none`). `:5728` (the whole scan is skipped while the raw `cancelPending` is set; used in G1).
- **DOWNSTREAM IMPACT:** the first audit's finding 7 locates the only "open position, no stop" check here. That check doesn't exist. A restart or reconnect into an unprotected position (G2, G1, G3) is never flagged.
- **FAILURE SCENARIO:** run `restore`. Position +10 with id-778 `[]`: "Open position detected" plus no alert. With TP only: a cyan "SL=none" line plus no alert. A 400 USD flush produced 0 frames.
- **ANALYTICAL CRITIQUE:** the one place that sees the position and the order book together does not compare them.
- **EVIDENCE:** proven by run.

### G11: the fallback ATR is computed on each bar's first update only, has no staleness check, and never reconnects if the first connect fails

- **SEVERITY:** S3
- **LOCATION:** `FrmIndicators.vb:280-286` (a live `chart.trades` bar is appended once, when `barMs > lastTimestamp`; every later update of the same bar is dropped). `:224-240` (the 5-s poll only fetches newer bars). `:141-145`, `:243` (a failed first connect is logged, never retried, and the poll timer never starts). `:1244-1276` (no timestamp on `_currentATR`). `SignalBridge.vb:457-461`, `:618-622` (the comments say the app's ATR period is 14; the default is 7, `frmMainPageV2.vb:35`).
- **DOWNSTREAM IMPACT:** whenever the payload ATR is absent (stale, SKIPPED, mode Off, G5), the slippage cap uses an ATR about half the true value, a frozen value, or a flat 70.
- **FAILURE SCENARIO:** run `atr`, a transcription of the append rule with the real Skender `GetAtr(7)`. The ATR was 27.87 against 51.91 true, a ratio of 0.537, giving a cap of 16.72 instead of 31.14.
- **ANALYTICAL CRITIQUE:** the error mostly points the safe way, a tighter cap. But the cap then jumps by 2–5× at every stand-down, which is what G5 shows.
- **EVIDENCE:** proven by run as a transcription, not the compiled `FrmIndicators`. The retry and staleness parts are read from code.

### G12: the executor feedback reports a zero target for every open bridge position, a limit price as the stop, and a dead leg as working

- **SEVERITY:** S3
- **LOCATION:** `:739-757` (`working.target = manualTPval`, `working.stop = placedStopLossPrice`). `:3576-3581` (the fill echo zeroes `manualTPval`). `:3410` (pre-trigger, `placedStopLossPrice` is the limit, 58,971.5, not the trigger, 59,001.5).
- **DOWNSTREAM IMPACT:** the engine's view of the executor is wrong in exactly the cases G2 is about.
- **FAILURE SCENARIO:** runs `trace` and `q1`: `working.target=0` in position, and `working.stop=58971.5` after the leg was rejected.
- **ANALYTICAL CRITIQUE:** the feedback was built to be truthful about flat (the "flat trap"). It is not truthful about in-position.
- **EVIDENCE:** proven by run.

### G13: the journal cannot record a wrong-side stop

- **SEVERITY:** S3
- **LOCATION:** `:3109` (`plannedStopAtEntry = StopLossTriggerOriginal`). `:5539-5546` (planned risk = `Math.Abs(entry − plannedStop)`).
- **DOWNSTREAM IMPACT:** a trigger 3.5 above a long's entry is stored as a normal 3.5 USD risk, and every analysis of the DB inherits that.
- **FAILURE SCENARIO:** the trace's DB row: `plannedStop=59001.5 R=-33.71`.
- **ANALYTICAL CRITIQUE:** this is the one artefact that would let someone find the inverted-stop class after the fact, and it throws the sign away.
- **EVIDENCE:** proven by run.

### G14: gate config fails open. Restart resets the unpersisted gates, the session-policy file loader drops bad rules silently, and the shipped breaker can't trip

- **SEVERITY:** S3
- **LOCATION:**
  - `AutoTradeSettings.vb:30-36`: the cooloff, window and tiers are not persisted, so every start gives cooloff 5, an unrestricted window (blank) and HIGH,MEDIUM.
  - `SessionPolicy.vb:321`, `:334`, `:341-344`: a malformed session rule falls back to the permissive defaults, an out-of-range `size_mult` becomes 1.0, and any exception gives `Defaults()` (disabled). None of these produce a message.
  - `AutoTradeSettings.vb:334-341` + `:312-313` + `AppUserSettings.vb:202`: the next save writes the dropped rule out of the file. Saves happen from the context menu (`frmMainPageV2.vb:995`) and at close (`frmMainPageV2.vb:6112`, found by grep; the rest of `FormClosing` was not read).
  - `AppUserSettings.vb:69` with Amount 10: the breaker trips at −10 USD, while a 10 USD position that runs to the 70 USD cap loses about 0.01 USD.
- **DOWNSTREAM IMPACT:** a restriction the trader set can silently stop existing.
- **FAILURE SCENARIO:** a typo such as `"tiers": ["MEDUIM"]` in `LONDON` makes LONDON trade HIGH and MEDIUM at full size, and the typo disappears from the file at the next close.
- **ANALYTICAL CRITIQUE:** the settings form's own parser fails closed and warns (`SessionPolicy.vb:183-260`). The file path should do the same.
- **EVIDENCE:** read from code.

### G15: after a gap in which the stop triggered, the chase compares against the pre-trigger limit price

- **SEVERITY:** S3
- **LOCATION:** `:5822-5825` (the restore seeds `placedStopLossPrice` only when it is 0), `:2622`.
- **DOWNSTREAM IMPACT:** the reference is the old limit, 30 below the trigger. The live order was repriced near the market at trigger time. The chase stays idle until the bid falls below the stale reference, which is up to 30 USD of un-chased adverse move.
- **FAILURE SCENARIO:** a signal-A-style stop triggers during a gap, and the market after reconnect is between the live order and the stale limit.
- **ANALYTICAL CRITIQUE:** the single-writer rule protects against lagging echoes, not against the snapshot, which is the more authoritative of the two.
- **EVIDENCE:** read from code.

### G16: a reconnect attempt can hang on authentication

- **SEVERITY:** S3
- **LOCATION:** `:1177-1180` (`ReceiveAsync` on the main token, no timeout), inside `:1350`.
- **DOWNSTREAM IMPACT:** if the handshake completes but the auth reply never comes, `isReconnecting` stays 1 and no further attempt is made. It only recovers if the keepalive aborts the socket.
- **FAILURE SCENARIO:** a degraded Deribit edge that accepts connections but stalls on auth.
- **ANALYTICAL CRITIQUE:** every await in the reconnect path needs a deadline.
- **EVIDENCE:** read from code.

### G17: the live-position refresh on echoes is dead code

- **SEVERITY:** S4
- **LOCATION:** `:3623-3627` sets `isRequestingLiveData = True`, then calls `GetLivePositionData`, which exits on that same flag (`:2902`).
- **DOWNSTREAM IMPACT:** id-777 is only ever sent at connect. The liquidation and leverage display never refreshes.
- **FAILURE SCENARIO:** any position echo.
- **ANALYTICAL CRITIQUE:** a guard that stops its own caller.
- **EVIDENCE:** read from code.

### G18: misleading log lines on the risk path

- **SEVERITY:** S4
- **LOCATION:** `:3730-3734` (slippage is logged in multiples of `FrmIndicators.CurrentATR`, not the ATR in force). `:4684`/`:4694` ("Sell" and "Buy" follow `TradeMode`, not the order sent).
- **DOWNSTREAM IMPACT:** operators misread events.
- **FAILURE SCENARIO:** runs `standdown` and `partial`: "(0.00x ATR)". Run `mode-flip`: "Emergency Buy" for a sell.
- **ANALYTICAL CRITIQUE:** logs are the only incident record the app keeps.
- **EVIDENCE:** proven by run.

### G19: `ForceStopLossUpdate`'s bypass does nothing in the chase path

- **SEVERITY:** S4
- **LOCATION:** `:5190` sets `lastStopLossUpdate = MinValue`; the caller re-stamps it at `:2684`. The `reason` parameter is unused.
- **DOWNSTREAM IMPACT:** none, beyond a false comment.
- **FAILURE SCENARIO:** n/a.
- **ANALYTICAL CRITIQUE:** a name and comment that promise behaviour the call site cancels.
- **EVIDENCE:** read from code.

### G20: edit bookkeeping is optimistic and unattributable

- **SEVERITY:** S4
- **LOCATION:**
  - `:2392-2407`: `placedPrice` and `legAnchorPrice` advance even when the edit function returned early.
  - `:4444`: consumes one credit and then tests the same `CanMakeRequest` twice.
  - `:4621`: every entry and TP edit shares id 223344, and every SL edit shares 223346.
  - `:4499-4503`: the bracket re-anchor re-sends unchanged manual TP/SL levels (run `q5`: two redundant frames).
- **DOWNSTREAM IMPACT:** the engine state can claim edits that were never sent, and responses can't be matched to requests.
- **FAILURE SCENARIO:** rate-limit edge cases.
- **ANALYTICAL CRITIQUE:** fire-and-forget edits with shared ids make every downstream reconciliation guesswork.
- **EVIDENCE:** read from code; the redundant frames were proven by run.

## Not verified

- **Deribit behaviour.** docs.deribit.com and the API hosts are blocked from here (`curl` returned 000; WebFetch was refused by the egress proxy). What I have are web-search summaries of support.deribit.com pages, which are secondary and LLM-summarised:
  - `first_hit` places the secondaries in full on the first partial hit.
  - A sell stop above the last price is rejected at placement.
  - Post-only reprices to one tick inside the spread.
  - `trigger_offset` is the "maximum deviation from the price peak" for trailing orders.
  - Order states include `rejected` and `untriggered`.

  Still unknown:
  - What happens to a crossed sell stop at **OTOCO activation**, as against at placement.
  - Whether `trigger_offset` on a `stop_limit` trails.
  - Whether a triggered stop keeps its `order_id`.
  - Market-order partial fills and price bands.
- **The harness is not the app.** It is an isolated harness: extracted methods plus stubs, with every exchange message scripted by me. The scripted exchange behaviours are assumptions, listed in its README. `ConnectToWebSocketDirectly`, `HandleWebSocketDisconnect` and the real receive loop were not executed. Reconnects are simulated by the two field resets `ConnectToWebSocketDirectly` performs.
- **G11** is a transcription of the append rule, not the compiled `FrmIndicators`.
- **.NET `KeepAliveTimeout` semantics (G9)** are from my understanding of .NET 9, not tested.
- **The app was not built.** It targets `net9.0-windows8.0` and this machine only has the .NET 8 SDK. The harness compiles the app's text against `net8.0`.
- The members in the "Not read in full" table.
