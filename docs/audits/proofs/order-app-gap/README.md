# Proofs for `docs/audits/2026-09-25-order-app-gap.md`

This is an isolated .NET 8 console harness that runs the order app's **own code at `8232e9e`** against scripted exchange messages. It is not the app's build and it does not touch the app project.

## What is real and what is stubbed

- **Real, extracted verbatim:** `gen.sh` copies 37 line ranges (4,177 lines) of `DeribitOrderPlacementApp/frmMainPageV2.vb` from the audited worktree into `src/Extracted.vb`. It checks each range's first line against an expected string and refuses to run if the worktree is not at `8232e9e`. The ranges cover:
  - the receive handlers: `HandleQuoteUpdates`, `HandleOrderPositionUpdates`, `HandleOpenOrdersSnapshot`, `ProcessPositionData`, `HandlePlacementResponse`, `HandleUnhandledJsonRpcError`, `HandleRateLimitError`
  - placement: `PlaceAutomatedOrder`, `ExecuteOrderAsync`, `SetTradeTargets`, `SyncTradeInputsFromUi`
  - every chase, edit and cancel path in the brief
  - `ApplyCloseFill`, `CompletePositionClose` and `SendReduceMarketOrderAsync`
  - the ATR guard, the rate limiter, and `SendWebSocketMessageAsync`, whose real catch path runs when the socket throws
- **Real, whole files:** `gen.sh` also copies `SignalBridge.vb`, `SessionPolicy.vb`, `ExecutorFeedback.vb`, `RemoteNotifier.vb`, `AppSecrets.vb` and `AppUserSettings.vb` unchanged. The bridge's gate chain, `ForceStop`, the ATR hand-off and the cooloff are the app's.
- **Stubbed (`src/Stubs.vb.txt`):**
  - WinForms controls become `Box`, with `.Text` setters on the ten trade inputs firing `SyncTradeInputsFromUi`, as the real `TextChanged` wiring at `:537-543` does.
  - `Invoke`/`BeginInvoke` run synchronously.
  - The socket is `FakeSocket`, which records every frame and can be told to throw.
  - `AppendColoredText`, `Alert`, `RecordCompletedTrade` and `LogTradeDecision` only record.
  - `SetTradeMode` sets `TradeMode`, which is its only engine effect (`:6165-6262`).
  - `HandleWebSocketDisconnect` and `InitializeRateLimits` are no-ops.
  - `AutoTradeSettings` returns its real field defaults (`AutoTradeSettings.vb:32-46`).
  - `FrmIndicators` exposes only a settable `CurrentATR`.
  - The controls start at the Designer defaults.
- **Not exercised:** `ConnectToWebSocketDirectly`, the reconnect loop and the real receive loop. A "reconnect" in the driver is `Reconnect()`, a new socket plus `positionRestoreAnnounced = False`, which are the two things `ConnectToWebSocketDirectly` resets (`:1407-1414`), followed by delivering id-777 and id-778. `Deliver()` calls the extracted handlers in the receive loop's order (`:1531-1553`), leaving out heartbeat, index, balance, token refresh and account summary.
- **The bridge's entry point** is its private `EvaluateNow`, the method the watcher callback reaches. The driver calls it by reflection, so the run does not depend on inotify timing.
- **The `atr` scenario is a TRANSCRIPTION** of `FrmIndicators.vb:280-286`'s append rule, run through the real `Skender.Stock.Indicators` 2.6.1 `GetAtr(7)`. It is not the compiled `FrmIndicators`, because that is a WinForms `Form`.

## Exchange behaviour the scripts ASSUME (none of it verified; docs.deribit.com is blocked here)

| Scenario | Assumed exchange behaviour |
|---|---|
| `trace*`, `mode-flip`, `close-in-gap`, `emergency-rejected` | A sell stop whose trigger is already crossed at OTOCO activation triggers at once. The post-only sell at 58,971.5 is repriced one tick above the 58,997.5 bid (58,998.0), and the triggered order arrives as an `open` `StopLossOrder` under a new id |
| `trace` | The emergency's market reduce fills 2.5 below the bid |
| `trace-emergency-send-fails` | The socket throws on the emergency's first send. The cancel-all never reaches the exchange |
| `emergency-rejected` | Cancel-all succeeds, and the reduce-only market sell is rejected `10028 too_many_requests` |
| `q1` | The activated SL leg comes back `rejected`, or `cancelled` |
| `close-in-gap` | During the gap the resting stop fills and the TP is OCO-cancelled. After the reconnect, id-777 reports size 0 and id-778 `[]` |
| `partial` | `trigger_fill_condition: first_hit` places the TP and SL for the **full 500** on a 10-USD first fill (per a web-search summary of Deribit's "Order attributes" support page). Later, the 500 TP fills and OCO cancels the SL |

## Run

From the repo root, on Linux with the .NET 8 SDK and nuget.org reachable:

```
apt-get install -y dotnet-sdk-8.0
git worktree add /tmp/app-8232e9e 8232e9e
bash docs/audits/proofs/order-app-gap/run.sh /tmp/app-8232e9e all
```

`run.sh` copies these files into a `mktemp` directory and renames `*.vb.txt` → `*.vb` and `OrderAppGap.vbproj.txt` → `OrderAppGap.vbproj`. It then runs `gen.sh` against the worktree, builds and runs. The second argument picks one scenario: `trace | trace-msl-off | trace-emergency-send-fails | emergency-rejected | q1 | restore | close-in-gap | partial | standdown | q5 | mode-flip | atr`. The full run takes about 2.5 minutes, because the 20-second WebSocket drop runs in real time.

Environment of the run below: Ubuntu 24.04 container, .NET SDK 8.0.131, Newtonsoft.Json 13.0.3, Skender.Stock.Indicators 2.6.1, 2026-09-29 (UTC). Timestamps in the output are this run's wall clock. Two earlier full runs matched this one line for line, apart from timestamps and one counter in the `partial` scenario that was fixed in between (it had also counted the cancel frame).

## Scenario → finding map

| Scenario | Shows | Report |
|---|---|---|
| `trace` | the LOGIC TRACE: placement frame, 8 chase edits, 0 frames during the drop, emergency on the first post-reconnect quote, −25 bp net, R −33.71 | LOGIC TRACE, Q2, Q4, G8, G9, G12, G13 |
| `trace-msl-off` | the chase follows the market to 58,833.0 with nothing capping it | Q2 |
| `trace-emergency-send-fails` | "Executed" logged with nothing sent; restore skipped; 0 frames over −300; the latch holds after a late restore | G1 |
| `emergency-rejected` | a naked long with no orders; 0 frames over −380 | G1 |
| `q1` | a `rejected` or `cancelled` SL leg gives 0 log lines, 0 alerts, 0 frames, and `working.stop=58971.5` | Q1, G2, G12 |
| `restore` | an empty or TP-only snapshot with an open position: no alert, 0 frames | G10 |
| `close-in-gap` | no record; the next signal is `acted` rather than `refused: cooloff`; dead-order edits and a phantom emergency while flat | G7 |
| `partial` | both echo orderings; the 500 TP flips the account to −490 with no stop, no alert, 0 frames | G3 |
| `standdown` | chased after SKIPPED/ARM-off/stale; the cap goes 72.12 → 70 → 90 → 70; the feedback says FLAT | Q3, G5, G18 |
| `q5` | the frames each chase path sends for the SL leg; no `trigger_offset` anywhere | Q5, G20 |
| `mode-flip` | after `btnSell_Click`: 0 frames on −200; the stop edited upward on the rally; "Emergency Buy" for a sell | G6, G18 |
| `atr` | as-appended ATR is 0.537 of the true ATR | G11 |

## Output of `bash docs/audits/proofs/order-app-gap/run.sh /tmp/app-8232e9e all`

```
generated 4254 lines from 8232e9e; copied 6 app files
Build succeeded.

=====================================================================
== TRACE  STRONG LONG in a flush, fill 58998.0, 20 s WS drop from fill+3 s  [M.SL ON]
=====================================================================
[t=  0.08s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  0.14s] signal #102 disposition: acted (id ORD-E1)
[t=  0.14s] placement frame: +0.10s {"jsonrpc":"2.0","id":600001,"method":"private/buy","params":{"instrument_name":"BTC-PERPETUAL","amount":10.0,"type":"limit","label":"EntryLimitOrder","time_in_force":"good_til_cancelled","linked_order_type":"one_triggers_one_cancels_other","trigger_fill_condition":"first_hit","reject_post_only":false,"otoco_config":[{"amount":10.0,"direction":"sell","type":"limit","label":"TakeLimitProfit","price":59079.0,"time_in_force":"good_til_cancelled","post_only":true},{"amount":10.0,"direction":"sell","type":"stop_limit","trigger_price":59001.5,"price":58971.5,"label":"StopLossOrder","time_in_force":"good_til_cancelled","trigger_offset":30.0,"post_only":true,"reduce_only":true,"reject_post_only":false,"trigger":"last_price"}],"price":58998.0,"post_only":true}}
[t=  0.17s] after placement echo: pos=0 avg=0 TradeMode=LONG CurrentOpenOrderId=ORD-E1 PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=59079 manualSL=58971.5 lblOrderStatus='Order Placed' slipLimit=72.12 originalSignalPrice=58998
[t=  0.00s] FILL echo delivered (entry filled 58998.0, TP open, SL leg untriggered @trig 59001.5)
[t=  0.00s] state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998
[t=  0.00s] SL leg TRIGGERED at activation -> open post-only sell @58998.0; state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58998 StopLossTriggerOriginal=59001.5 emergencyBaseline=58998 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='Stop Loss Triggered' slipLimit=72.12 originalSignalPrice=58998
[t=  0.00s] feedback: started=True direction=LONG size_usd=10 avg_entry=58998 working.stop=58998 working.target=0
      log 15:59:50.435 [LimeGreen] Position entered: LONG 10 @ $58998.00
      log 15:59:50.438 [Red] Triggered SL placed @ $58998
[t=  3.03s] 0-3 s: SL-chase frames SENT: 8
      +0.11s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58997.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +0.51s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58995.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +0.91s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58993.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +1.32s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58991.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +1.72s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58989.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +2.12s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58987.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +2.53s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58985.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +2.93s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58983.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
[t=  3.03s] at drop: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58983.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=58997.5 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='Stop Loss Triggered' slipLimit=72.12 originalSignalPrice=58998
[t=  3.03s] WS DROPPED (state Aborted). Delivering nothing for 20 s while the market falls another 100 USD.
[t= 23.03s] 3-23 s (drop): frames SENT: 0
[t= 23.03s] counterfactual: with the socket up, the M.SL cap would have fired when ask <= emergencyBaseline - 70
[t= 23.03s] RECONNECTED; restore delivered. state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58983.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=58997.5 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998
[t= 23.04s] first post-reconnect quote bid 58882.5
[t= 23.04s] frames SENT on that quote: 2
      +23.04s {"jsonrpc":"2.0","id":30,"method":"private/cancel_all_by_instrument","params":{"instrument_name":"BTC-PERPETUAL","type":"all"}}
      +23.04s {"jsonrpc":"2.0","id":1,"method":"private/sell","params":{"instrument_name":"BTC-PERPETUAL","amount":10.0,"type":"market","reduce_only":true,"time_in_force":"good_til_cancelled","label":"ReduceMarketOrder"}}
      log 16:00:13.467 [Yellow] Open position detected: LONG 10 @ 58998.00
      log 16:00:13.468 [Red] LIVE position data - Liq: N/A, Leverage: pending
      log 16:00:13.469 [Cyan] Restored order context: entry=none, TP=ORD-TP1@59079.00, SL=ORD-SL2@58983.50 (triggered)
      log 16:00:13.471 [Yellow] Cancelled all open orders
      log 16:00:13.473 [Green] Reduce-only MARKET sell 10  order sent.
      log 16:00:13.473 [Red] Emergency Sell Market Order Executed.
[t= 23.04s] state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=False placedPrice=0 placedStopLossPrice=0 StopLossTriggerOriginal=0 emergencyBaseline=0 emergencyFired=True cancelPending(raw)=True manualTP=0 manualSL=0 lblOrderStatus='Awaiting Orders' slipLimit=72.12 originalSignalPrice=0
[t= 23.04s] alerts so far: entry_fill,emergency_stop
      log 16:00:13.474 [Crimson] Position reduced at 58880.00 (market order).
      log 16:00:13.476 [Crimson] Position executed at 58880.
      log 16:00:13.476 [Crimson] Loss of: $0.02.
      log 16:00:13.476 [Gray] [BRIDGE] cooloff started: 5 min from position close
[t= 23.24s] DB LogTradeDecision Order Placed
[t= 23.24s] DB LogTradeDecision In Position
[t= 23.24s] DB LogTradeDecision Exit Position - Market Order Loss
[t= 23.24s] DB LogTradeDecision Exit Position - Loss
[t= 23.24s] DB RecordCompletedTrade entry=58998 exit=58880 size=10 pl=-0.02 label=ReduceMarketOrder plannedStop=59001.5 R=-33.71
[t= 23.24s] engine's planned risk: entry 59003.5 - stop 59001.5 = 2.0 USD (0.34 bp).  Realised: exit 58880.0 vs fill 58998.0 = -118.0 USD (-20.00 bp gross), -25.00 bp net of maker 1.5 + taker 3.5

=====================================================================
== TRACE  STRONG LONG in a flush, fill 58998.0, 20 s WS drop from fill+3 s  [M.SL OFF]
=====================================================================
[t=  0.00s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  0.03s] signal #102 disposition: acted (id ORD-E1)
[t=  0.03s] placement frame: +0.01s {"jsonrpc":"2.0","id":600001,"method":"private/buy","params":{"instrument_name":"BTC-PERPETUAL","amount":10.0,"type":"limit","label":"EntryLimitOrder","time_in_force":"good_til_cancelled","linked_order_type":"one_triggers_one_cancels_other","trigger_fill_condition":"first_hit","reject_post_only":false,"otoco_config":[{"amount":10.0,"direction":"sell","type":"limit","label":"TakeLimitProfit","price":59079.0,"time_in_force":"good_til_cancelled","post_only":true},{"amount":10.0,"direction":"sell","type":"stop_limit","trigger_price":59001.5,"price":58971.5,"label":"StopLossOrder","time_in_force":"good_til_cancelled","trigger_offset":30.0,"post_only":true,"reduce_only":true,"reject_post_only":false,"trigger":"last_price"}],"price":58998.0,"post_only":true}}
[t=  0.03s] after placement echo: pos=0 avg=0 TradeMode=LONG CurrentOpenOrderId=ORD-E1 PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=59079 manualSL=58971.5 lblOrderStatus='Order Placed' slipLimit=72.12 originalSignalPrice=58998
[t=  0.00s] FILL echo delivered (entry filled 58998.0, TP open, SL leg untriggered @trig 59001.5)
[t=  0.00s] state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998
[t=  0.00s] SL leg TRIGGERED at activation -> open post-only sell @58998.0; state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58998 StopLossTriggerOriginal=59001.5 emergencyBaseline=58998 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='Stop Loss Triggered' slipLimit=72.12 originalSignalPrice=58998
[t=  0.00s] feedback: started=True direction=LONG size_usd=10 avg_entry=58998 working.stop=58998 working.target=0
      log 16:00:13.706 [LimeGreen] Position entered: LONG 10 @ $58998.00
      log 16:00:13.707 [Red] Triggered SL placed @ $58998
[t=  3.01s] 0-3 s: SL-chase frames SENT: 8
      +0.10s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58997.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +0.50s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58995.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +0.90s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58993.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +1.31s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58991.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +1.71s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58989.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +2.11s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58987.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +2.51s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58985.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +2.91s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58983.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
[t=  3.01s] at drop: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58983.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=58997.5 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='Stop Loss Triggered' slipLimit=72.12 originalSignalPrice=58998
[t=  3.01s] WS DROPPED (state Aborted). Delivering nothing for 20 s while the market falls another 100 USD.
[t= 23.02s] 3-23 s (drop): frames SENT: 0
[t= 23.02s] counterfactual: with the socket up, the M.SL cap would have fired when ask <= emergencyBaseline - 70
[t= 23.02s] RECONNECTED; restore delivered. state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58983.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=58997.5 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998
[t= 23.02s] first post-reconnect quote bid 58882.5
[t= 23.02s] frames SENT on that quote: 1
      +23.02s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58883.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      log 16:00:36.721 [Yellow] Open position detected: LONG 10 @ 58998.00
      log 16:00:36.722 [Red] LIVE position data - Liq: N/A, Leverage: pending
      log 16:00:36.722 [Cyan] Restored order context: entry=none, TP=ORD-TP1@59079.00, SL=ORD-SL2@58983.50 (triggered)
      log 16:00:36.722 [Orange] SL reposition sent: $58983.50 → $58883.00
[t= 23.02s] state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58883.0 StopLossTriggerOriginal=59001.5 emergencyBaseline=58997.5 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998
[t= 23.02s] alerts so far: entry_fill
[t= 33.07s] M.SL OFF, 10 more seconds of flush to bid 58832.5: 25 frames sent, all SL edits: True; last: +33.07s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58833.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
[t= 33.07s] state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58833.0 StopLossTriggerOriginal=59001.5 emergencyBaseline=58997.5 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998

=====================================================================
== TRACE  STRONG LONG in a flush, fill 58998.0, 20 s WS drop from fill+3 s  [M.SL ON, emergency send fails]
=====================================================================
[t=  0.00s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  0.02s] signal #102 disposition: acted (id ORD-E1)
[t=  0.02s] placement frame: +0.00s {"jsonrpc":"2.0","id":600001,"method":"private/buy","params":{"instrument_name":"BTC-PERPETUAL","amount":10.0,"type":"limit","label":"EntryLimitOrder","time_in_force":"good_til_cancelled","linked_order_type":"one_triggers_one_cancels_other","trigger_fill_condition":"first_hit","reject_post_only":false,"otoco_config":[{"amount":10.0,"direction":"sell","type":"limit","label":"TakeLimitProfit","price":59079.0,"time_in_force":"good_til_cancelled","post_only":true},{"amount":10.0,"direction":"sell","type":"stop_limit","trigger_price":59001.5,"price":58971.5,"label":"StopLossOrder","time_in_force":"good_til_cancelled","trigger_offset":30.0,"post_only":true,"reduce_only":true,"reject_post_only":false,"trigger":"last_price"}],"price":58998.0,"post_only":true}}
[t=  0.02s] after placement echo: pos=0 avg=0 TradeMode=LONG CurrentOpenOrderId=ORD-E1 PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=59079 manualSL=58971.5 lblOrderStatus='Order Placed' slipLimit=72.12 originalSignalPrice=58998
[t=  0.00s] FILL echo delivered (entry filled 58998.0, TP open, SL leg untriggered @trig 59001.5)
[t=  0.00s] state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998
[t=  0.00s] SL leg TRIGGERED at activation -> open post-only sell @58998.0; state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58998 StopLossTriggerOriginal=59001.5 emergencyBaseline=58998 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='Stop Loss Triggered' slipLimit=72.12 originalSignalPrice=58998
[t=  0.00s] feedback: started=True direction=LONG size_usd=10 avg_entry=58998 working.stop=58998 working.target=0
      log 16:00:46.798 [LimeGreen] Position entered: LONG 10 @ $58998.00
      log 16:00:46.798 [Red] Triggered SL placed @ $58998
[t=  3.01s] 0-3 s: SL-chase frames SENT: 8
      +0.10s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58997.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +0.50s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58995.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +0.90s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58993.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +1.31s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58991.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +1.71s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58989.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +2.11s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58987.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +2.51s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58985.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +2.91s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58983.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
[t=  3.01s] at drop: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58983.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=58997.5 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='Stop Loss Triggered' slipLimit=72.12 originalSignalPrice=58998
[t=  3.01s] WS DROPPED (state Aborted). Delivering nothing for 20 s while the market falls another 100 USD.
[t= 23.02s] 3-23 s (drop): frames SENT: 0
[t= 23.02s] counterfactual: with the socket up, the M.SL cap would have fired when ask <= emergencyBaseline - 70
[t= 23.02s] RECONNECTED; restore delivered. state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58983.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=58997.5 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998
[t= 23.02s] first post-reconnect quote bid 58882.5
[t= 23.02s] frames SENT on that quote: 0
      log 16:01:09.813 [Yellow] Open position detected: LONG 10 @ 58998.00
      log 16:01:09.813 [Red] LIVE position data - Liq: N/A, Leverage: pending
      log 16:01:09.814 [Cyan] Restored order context: entry=none, TP=ORD-TP1@59079.00, SL=ORD-SL2@58983.50 (triggered)
      log 16:01:09.816 [Red] WebSocket Error: simulated: connection reset while sending
      log 16:01:09.816 [Gray] (stub) HandleWebSocketDisconnect scheduled
      log 16:01:09.816 [Yellow] Cancelled all open orders
      log 16:01:09.816 [Red] WebSocket is not connected - reduce order skipped.
      log 16:01:09.816 [Red] Emergency Sell Market Order Executed.
[t= 23.02s] state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=False placedPrice=0 placedStopLossPrice=0 StopLossTriggerOriginal=0 emergencyBaseline=0 emergencyFired=True cancelPending(raw)=True manualTP=0 manualSL=0 lblOrderStatus='Awaiting Orders' slipLimit=72.12 originalSignalPrice=0
[t= 23.02s] alerts so far: entry_fill,emergency_stop
[t= 25.52s] RECONNECTED 2.5 s later (cancel-all never reached the exchange: SL + TP still live). state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=False placedPrice=58998 placedStopLossPrice=0 StopLossTriggerOriginal=0 emergencyBaseline=0 emergencyFired=True cancelPending(raw)=True manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=0
      log 16:01:12.317 [Yellow] Open position detected: LONG 10 @ 58998.00
      log 16:01:12.317 [Red] LIVE position data - Liq: N/A, Leverage: pending
[t= 31.55s] market falls another 300 USD to bid 58582.5 over 6 s: frames sent = 0
[t= 31.55s] state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=False placedPrice=58998 placedStopLossPrice=0 StopLossTriggerOriginal=0 emergencyBaseline=0 emergencyFired=True cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=0
[t= 35.75s] snapshot re-delivered after the 4 s window: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58983.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=58983.5 emergencyFired=True cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=0
[t= 36.75s] 50 USD further down: 3 frames, any market reduce: False; emergencyFired stays latched

=====================================================================
== EMERGENCY-REJECTED  M.SL fires; cancel-all succeeds; the reduce-only market sell is rejected (10028)
=====================================================================
[t=  0.00s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  0.02s] signal #102 disposition: acted (id ORD-E1)
[t=  1.63s] flush to 58917.5: frames SENT: 6
      +0.12s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58993.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +0.53s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58973.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +0.93s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58953.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +1.33s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58933.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +1.53s {"jsonrpc":"2.0","id":30,"method":"private/cancel_all_by_instrument","params":{"instrument_name":"BTC-PERPETUAL","type":"all"}}
      +1.53s {"jsonrpc":"2.0","id":1,"method":"private/sell","params":{"instrument_name":"BTC-PERPETUAL","amount":10.0,"type":"market","reduce_only":true,"time_in_force":"good_til_cancelled","label":"ReduceMarketOrder"}}
      log 16:01:23.678 [Orange] SL reposition sent: $58998.00 → $58993.00
      log 16:01:24.080 [Orange] SL reposition sent: $58993.00 → $58973.00
      log 16:01:24.482 [Orange] SL reposition sent: $58973.00 → $58953.00
      log 16:01:24.883 [Orange] SL reposition sent: $58953.00 → $58933.00
      log 16:01:25.084 [Yellow] Cancelled all open orders
      log 16:01:25.085 [Green] Reduce-only MARKET sell 10  order sent.
      log 16:01:25.085 [Red] Emergency Sell Market Order Executed.
      log 16:01:25.186 [Red] Rate limit exceeded - reducing API frequency
[t=  1.73s] alerts: entry_fill,emergency_stop; state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=False placedPrice=0 placedStopLossPrice=0 StopLossTriggerOriginal=0 emergencyBaseline=0 emergencyFired=True cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='Awaiting Orders' slipLimit=72.12 originalSignalPrice=0
[t=  4.76s] flush continues to 58617.5 (-380.5 from the fill): frames sent = 0; exchange holds a 10 USD long with NO orders

=====================================================================
== Q1  SL leg 'rejected' by the exchange right after the fill, then a 300 USD flush
=====================================================================
[t=  0.00s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  0.02s] signal #102 disposition: acted (id ORD-E1)
[t=  0.07s] after 'rejected' echo: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998
[t=  0.07s] feedback: started=True direction=LONG size_usd=10 avg_entry=58998 working.stop=58971.5 working.target=0
[t=  0.07s] log lines produced by that echo:
[t=  0.07s] alerts produced: 0
[t=  3.10s] flush to bid 58697.5 (-298 USD): frames sent = 0, alerts = 0
[t=  3.10s] final: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998

=====================================================================
== Q1  SL leg 'cancelled' by the exchange right after the fill, then a 300 USD flush
=====================================================================
[t=  0.00s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  0.02s] signal #102 disposition: acted (id ORD-E1)
[t=  0.07s] after 'cancelled' echo: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998
[t=  0.07s] feedback: started=True direction=LONG size_usd=10 avg_entry=58998 working.stop=58971.5 working.target=0
[t=  0.07s] log lines produced by that echo:
[t=  0.07s] alerts produced: 0
[t=  3.10s] flush to bid 58697.5 (-298 USD): frames sent = 0, alerts = 0
[t=  3.10s] final: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998

=====================================================================
== RESTORE  restart with an open long and NO stop order on the exchange
=====================================================================
[t=  0.00s] case 1: id-778 result [] (no orders at all)
      log 16:01:34.516 [Gray] [BRIDGE] consumer ready (mode Off) - payload path: /tmp/order-app-gap-payload/verdict_signal.json
      log 16:01:34.516 [Yellow] Open position detected: LONG 10 @ 58998.00
      log 16:01:34.516 [Red] LIVE position data - Liq: N/A, Leverage: pending
[t=  0.00s] alerts: 0; state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=Nothing SLTriggered=False placedPrice=58998 placedStopLossPrice=0 StopLossTriggerOriginal=0 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=70 originalSignalPrice=0
[t=  0.00s] case 2: id-778 result = TP only
      log 16:01:34.517 [Gray] [BRIDGE] consumer ready (mode Off) - payload path: /tmp/order-app-gap-payload/verdict_signal.json
      log 16:01:34.517 [Yellow] Open position detected: LONG 10 @ 58998.00
      log 16:01:34.517 [Red] LIVE position data - Liq: N/A, Leverage: pending
      log 16:01:34.517 [Cyan] Restored order context: entry=none, TP=ORD-TP1@59079.00, SL=none
[t=  0.00s] alerts: 0; state: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=Nothing SLTriggered=False placedPrice=58998 placedStopLossPrice=0 StopLossTriggerOriginal=0 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=70 originalSignalPrice=0
[t=  0.81s] flush to 58597.5: frames sent = 0

=====================================================================
== CLOSE-IN-GAP  the triggered SL fills while the socket is down
=====================================================================
[t=  0.00s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  0.02s] signal #102 disposition: acted (id ORD-E1)
[t=  1.03s] before drop: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58993.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=58997.5 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='Stop Loss Triggered' slipLimit=72.12 originalSignalPrice=58998
[t=  1.03s] WS DROPPED; during the gap the market bounces and the resting SL sell fills; TP is OCO-cancelled. None of it is delivered.
[t=  4.13s] RECONNECTED: id-777 size 0, id-778 []
      log 16:01:39.361 [Red] LIVE position data - Liq: N/A, Leverage: pending
[t=  4.13s] DB/trade-log records: [LogTradeDecision Order Placed | LogTradeDecision In Position]; alerts: entry_fill
[t=  4.13s] state: pos=0 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58993.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=58997.5 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='Stop Loss Triggered' slipLimit=72.12 originalSignalPrice=58998
[t=  4.43s] signal #103, delivered immediately after the unobserved close -> acted (id ORD-E1)  (the 5-min cooloff did not apply: CompletePositionClose/NotifyPositionClosed never ran)
[t=  4.43s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  4.45s] signal #102 disposition: acted (id ORD-E1)
[t=  4.96s] CONTRAST, close observed: records [LogTradeDecision Order Placed | RecordCompletedTrade entry=58998 exit=58993 size=10 pl=-0.00 label=StopLossOrder plannedStop=59001.5 R=0]; signal #103 -> refused: cooloff
[t=  4.96s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  4.98s] signal #102 disposition: acted (id ORD-E1)
[t=  8.60s] third host, FLAT after an unobserved close, market falls 90 USD: frames SENT: 7
      +5.89s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58986.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +6.29s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58974.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +6.69s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58962.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +7.09s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58950.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +7.49s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":58938.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +7.90s {"jsonrpc":"2.0","id":30,"method":"private/cancel_all_by_instrument","params":{"instrument_name":"BTC-PERPETUAL","type":"all"}}
      +7.90s {"jsonrpc":"2.0","id":1,"method":"private/sell","params":{"instrument_name":"BTC-PERPETUAL","amount":10.0,"type":"market","reduce_only":true,"time_in_force":"good_til_cancelled","label":"ReduceMarketOrder"}}
      log 16:01:41.219 [Orange] SL reposition sent: $58995.50 → $58986.50
      log 16:01:41.621 [Orange] SL reposition sent: $58986.50 → $58974.50
      log 16:01:42.022 [Orange] SL reposition sent: $58974.50 → $58962.50
      log 16:01:42.424 [Orange] SL reposition sent: $58962.50 → $58950.50
      log 16:01:42.826 [Orange] SL reposition sent: $58950.50 → $58938.50
      log 16:01:43.227 [Yellow] Cancelled all open orders
      log 16:01:43.227 [Orange] Position model empty - using TradeMode/txtAmount fallback for market reduce
      log 16:01:43.227 [Green] Reduce-only MARKET sell 10  order sent.
      log 16:01:43.227 [Red] Emergency Sell Market Order Executed.
[t=  8.60s] state: pos=0 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=False placedPrice=0 placedStopLossPrice=0 StopLossTriggerOriginal=0 emergencyBaseline=0 emergencyFired=True cancelPending(raw)=True manualTP=0 manualSL=0 lblOrderStatus='Awaiting Orders' slipLimit=72.12 originalSignalPrice=0

=====================================================================
== PARTIAL  Amount 500, first partial fill of 10, OTOCO first_hit legs for 500  [echo order: TP, SL, Entry]
=====================================================================
[t=  0.00s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  0.02s] signal #102 disposition: acted (id ORD-E1)
[t=  0.07s] after the partial-fill echo: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=ORD-E1 PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=59079 manualSL=58971.5 lblOrderStatus='Order Placed' slipLimit=72.12 originalSignalPrice=58998
[t=  4.10s] bounce to 59078 over 4 s: 14 frames sent; edits of ORD-E1 9; cancels 1
      +0.18s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59000.0,"amount":500.0,"post_only":true,"reject_post_only":false}}
      +0.58s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59008.0,"amount":500.0,"post_only":true,"reject_post_only":false}}
      +0.98s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59016.0,"amount":500.0,"post_only":true,"reject_post_only":false}}
      +1.38s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59024.0,"amount":500.0,"post_only":true,"reject_post_only":false}}
      +1.79s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59032.0,"amount":500.0,"post_only":true,"reject_post_only":false}}
      +1.79s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-TP1","price":59079.0,"amount":500.0}}
      +1.79s {"jsonrpc":"2.0","id":223346,"method":"private/edit","params":{"order_id":"SLTS-1","price":58971.5,"amount":500.0,"trigger_price":59001.5}}
      +2.19s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59040.0,"amount":500.0,"post_only":true,"reject_post_only":false}}
      +2.59s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59048.0,"amount":500.0,"post_only":true,"reject_post_only":false}}
      +2.99s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59056.0,"amount":500.0,"post_only":true,"reject_post_only":false}}
      +3.39s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59064.0,"amount":500.0,"post_only":true,"reject_post_only":false}}
      +3.40s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-TP1","price":59079.0,"amount":500.0}}
      +3.40s {"jsonrpc":"2.0","id":223346,"method":"private/edit","params":{"order_id":"SLTS-1","price":58971.5,"amount":500.0,"trigger_price":59001.5}}
      +3.80s {"jsonrpc":"2.0","id":31,"method":"private/cancel","params":{"order_id":"ORD-E1"}}
      log 16:01:44.110 [Yellow] Order repositioned: $58998.00 → $59000.00
      log 16:01:44.512 [Yellow] Order repositioned: $59000.00 → $59008.00
      log 16:01:44.914 [Yellow] Order repositioned: $59008.00 → $59016.00
      log 16:01:45.318 [Yellow] Order repositioned: $59016.00 → $59024.00
      log 16:01:45.722 [Yellow] Order repositioned: $59024.00 → $59032.00
      log 16:01:46.124 [Yellow] Order repositioned: $59032.00 → $59040.00
      log 16:01:46.526 [Yellow] Order repositioned: $59040.00 → $59048.00
      log 16:01:46.927 [Yellow] Order repositioned: $59048.00 → $59056.00
      log 16:01:47.329 [Yellow] Order repositioned: $59056.00 → $59064.00
      log 16:01:47.731 [Red] LONG slippage $74.00 (0.00x ATR) exceeds limit $72.12
      log 16:01:47.732 [Yellow] Working entry cancelled (ATR slippage) - position legs untouched
[t=  4.15s] TP (not reduce_only, 500) filled against a 10 long; SL cancelled by OCO; position -490:
[t=  4.15s] alerts: 0; state: pos=-490 avg=59079 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=0 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=0
[t=  6.17s] market rallies to 59478 against the short: frames sent = 0; bridge IsFlat=False

=====================================================================
== PARTIAL  Amount 500, first partial fill of 10, OTOCO first_hit legs for 500  [echo order: Entry, TP, SL]
=====================================================================
[t=  0.00s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  0.02s] signal #102 disposition: acted (id ORD-E1)
[t=  0.07s] after the partial-fill echo: pos=10 avg=58998 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998
[t=  4.09s] bounce to 59078 over 4 s: 0 frames sent; edits of ORD-E1 0; cancels 0
[t=  4.14s] TP (not reduce_only, 500) filled against a 10 long; SL cancelled by OCO; position -490:
[t=  4.14s] alerts: 0; state: pos=-490 avg=59079 TradeMode=LONG CurrentOpenOrderId=Nothing PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='In Position' slipLimit=72.12 originalSignalPrice=58998
[t=  6.16s] market rallies to 59478 against the short: frames sent = 0; bridge IsFlat=False

=====================================================================
== STAND-DOWN  working bridge entry, then SKIPPED / ARM-off / stale payloads, market rises
=====================================================================
[t=  0.00s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  0.02s] signal #102 disposition: acted (id ORD-E1)
[t=  0.02s] working entry resting: pos=0 avg=0 TradeMode=LONG CurrentOpenOrderId=ORD-E1 PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=58998 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=59079 manualSL=58971.5 lblOrderStatus='Order Placed' slipLimit=72.12 originalSignalPrice=58998
[t=  0.23s] SKIPPED payload -> disposition 'skipped', Started=False, LastSignalAtr=0, slip limit now 70
[t=  0.23s]    feedback: started=False direction=FLAT size_usd=0 avg_entry=0 working.stop=0 working.target=0
[t=  3.43s]    market up to 59030: 10 frames; edits of ORD-E1 8; cancels 0; HasWorkingEntryOrder=True
      +0.63s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59002.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +1.03s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59006.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
[t=  3.43s] (trader re-STARTs after the SKIPPED auto-STOP, so the ARM-off payload's own auto-STOP is visible: STARTED)
[t=  3.63s] ARM off payload -> disposition 'refused: interlock', Started=False, LastSignalAtr=150, slip limit now 90.0
[t=  3.63s]    feedback: started=False direction=FLAT size_usd=0 avg_entry=0 working.stop=0 working.target=0
[t=  6.84s]    market up to 59062: 10 frames; edits of ORD-E1 8; cancels 0; HasWorkingEntryOrder=True
      +4.03s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59034.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +4.43s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59038.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
[t=  7.04s] stale payload -> disposition 'stale', Started=False, LastSignalAtr=0, slip limit now 70
[t=  7.04s]    feedback: started=False direction=FLAT size_usd=0 avg_entry=0 working.stop=0 working.target=0
[t= 10.24s]    market up to 59094: 2 frames; edits of ORD-E1 1; cancels 1; HasWorkingEntryOrder=False
      +7.44s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59066.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +7.84s {"jsonrpc":"2.0","id":31,"method":"private/cancel","params":{"order_id":"ORD-E1"}}
[t= 10.24s] bridge log lines:
      16:01:56.265 [Gray] [BRIDGE] consumer ready (mode Off) - payload path: /tmp/order-app-gap-payload/verdict_signal.json
      16:01:56.266 [DodgerBlue] [BRIDGE] mode Live - watching /tmp/order-app-gap-payload/verdict_signal.json
      16:01:56.268 [DodgerBlue] [BRIDGE] ARM on (local)
      16:01:56.268 [LimeGreen] [BRIDGE] STARTED - live auto-trading interlock satisfied
      16:01:56.270 [LimeGreen] [BRIDGE] signal #102 STRONG LONG (HIGH/LONG) -> acted (id ORD-E1)
      16:01:56.290 [Red] [BRIDGE] auto-STOP: SKIPPED payload (engine stand-down)
      16:01:59.696 [LimeGreen] [BRIDGE] STARTED - live auto-trading interlock satisfied
      16:01:59.697 [Red] [BRIDGE] auto-STOP: engine ARM off in latest payload
      16:02:04.104 [Red] LONG slippage $72.00 (0.00x ATR) exceeds limit $70.00
      16:02:04.104 [Yellow] Working entry cancelled (ATR slippage) - position legs untouched

=====================================================================
== Q5  what each chase path sends for the SL leg (pre-fill entry chase, full-bracket re-anchor, post-trigger chase)
=====================================================================
[t=  0.00s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  0.02s] signal #102 disposition: acted (id ORD-E1)
[t=  0.02s] placed SL leg: {"amount":10.0,"direction":"sell","type":"stop_limit","trigger_price":59001.5,"price":58971.5,"label":"StopLossOrder","time_in_force":"good_til_cancelled","trigger_offset":30.0,"post_only":true,"reduce_only":true,"reject_post_only":false,"trigger":"last_price"}
[t=  4.83s] pre-fill, market up 36 USD (legReanchorDriftMax = min(txtTakeProfit 60, txtTrigger 60)/2 = 30): frames: 14
      +0.42s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59001.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +0.82s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59004.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +1.23s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59007.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +1.63s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59010.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +2.03s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59013.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +2.43s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59016.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +2.83s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59019.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +3.23s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59022.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +3.63s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59025.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +4.03s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59028.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +4.03s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-TP1","price":59079.0,"amount":10.0}}
      +4.03s {"jsonrpc":"2.0","id":223346,"method":"private/edit","params":{"order_id":"SLTS-1","price":58971.5,"amount":10.0,"trigger_price":59001.5}}
      +4.43s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59031.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +4.83s {"jsonrpc":"2.0","id":223344,"method":"private/edit","params":{"order_id":"ORD-E1","price":59034.0,"amount":10.0,"post_only":true,"reject_post_only":false}}
[t=  4.83s] state: pos=0 avg=0 TradeMode=LONG CurrentOpenOrderId=ORD-E1 PositionSLOrderId=SLTS-1 SLTriggered=False placedPrice=59034.0 placedStopLossPrice=58971.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=0 emergencyFired=False cancelPending(raw)=False manualTP=59079 manualSL=58971.5 lblOrderStatus='Order Placed' slipLimit=72.12 originalSignalPrice=58998

=====================================================================
== MODE-FLIP  long open, SL triggered; the trader clicks the Sell mode button; market falls, then rallies
=====================================================================
[t=  0.00s] TryStart -> STARTED; first payload disposition: refused: interlock
[t=  0.02s] signal #102 disposition: acted (id ORD-E1)
[t=  0.43s] after btnSell_Click: pos=10 avg=58998 TradeMode=SHORT CurrentOpenOrderId=Nothing PositionSLOrderId=ORD-SL2 SLTriggered=True placedPrice=58998 placedStopLossPrice=58997.5 StopLossTriggerOriginal=59001.5 emergencyBaseline=58997.5 emergencyFired=False cancelPending(raw)=False manualTP=0 manualSL=0 lblOrderStatus='Stop Loss Triggered' slipLimit=72.12 originalSignalPrice=58998
[t=  4.45s] market falls 200 USD to 58795.5 with the long open: frames sent = 0; alerts entry_fill
[t= 10.48s] market rallies 300 USD to 59095.5: frames SENT: 6
      +8.57s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":59000.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +8.97s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":59020.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +9.37s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":59040.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +9.77s {"jsonrpc":"2.0","id":223350,"method":"private/edit","params":{"order_id":"ORD-SL2","price":59060.5,"amount":10.0,"post_only":true,"reject_post_only":false}}
      +9.97s {"jsonrpc":"2.0","id":30,"method":"private/cancel_all_by_instrument","params":{"instrument_name":"BTC-PERPETUAL","type":"all"}}
      +9.97s {"jsonrpc":"2.0","id":1,"method":"private/sell","params":{"instrument_name":"BTC-PERPETUAL","amount":10.0,"type":"market","reduce_only":true,"time_in_force":"good_til_cancelled","label":"ReduceMarketOrder"}}
      log 16:02:19.909 [Orange] SL reposition sent: $58997.50 → $59000.50
      log 16:02:20.311 [Orange] SL reposition sent: $59000.50 → $59020.50
      log 16:02:20.712 [Orange] SL reposition sent: $59020.50 → $59040.50
      log 16:02:21.115 [Orange] SL reposition sent: $59040.50 → $59060.50
      log 16:02:21.316 [Yellow] Cancelled all open orders
      log 16:02:21.316 [Green] Reduce-only MARKET sell 10  order sent.
      log 16:02:21.316 [Red] Emergency Buy Market Order Executed.

=====================================================================
== ATR  FrmIndicators fallback: live bars are kept as their FIRST update only (transcribed rule, real Skender GetAtr)
=====================================================================
  bars: 179 true / 180 as-appended
  mean ATR(7) on true 1-min bars      : 51.91
  mean ATR(7) on as-appended bars     : 27.87
  ratio as-appended / true            : 0.537
  -> fallback slippage cap 0.6 x ATR  : 16.72 instead of 31.14
```
