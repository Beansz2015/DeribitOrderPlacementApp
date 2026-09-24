# Adversarial audit — signal-to-exchange order path (2026-09-24)

**Scope:** how the order app consumes DeribitVerdictEngine's `verdict_signal.json` (schema v1) and turns it into BTC-PERPETUAL orders. That covers the signal reader and watcher, the gate chain, order construction (entry, stop-limit, TP), rounding, position and fill handling, and rejection handling.
**Audited at:** `8232e9e` on `claude/gracious-fermat-w8sacs`.
**Engine-side context:** relayed in the audit brief from the engine repo's `docs/adversarial-audit-2026-09-24.md`. I did not read that document.
**Proofs:** `docs/audits/proofs/signal-to-exchange-order-path/` (the README there has the run commands and output).

## Coverage: what was NOT fully covered

The audit did not read every in-scope path end to end. What was read in full: `SignalBridge.vb` (all 1,277 lines); `frmMainPageV2.vb` `PlaceAutomatedOrder`, `SetTradeTargets`, `ExecuteOrderAsync` (all six order types), `RoundToTick`, the slippage/ATR functions, `HandlePlacementResponse`, the `open` / `untriggered` / `filled` branches of `HandleOrderPositionUpdates`, the long entry-chase gate and the market-stop emergency gate.

Not covered, or covered only by grep:

| File / member | Status |
|---|---|
| `frmMainPageV2.vb` `UpdateLimitOrderWithOTOCOAsync`, `UpdateEntryOrderOnlyAsync` (chase edits that resend the legs) | Only the `manualSLval` lines found by grep (`:4470-4495`) |
| `frmMainPageV2.vb` `UpdateStopLossForTriggeredStopLossOrder`, `ForceStopLossUpdate` (triggered-stop chase and market-stop fallback) | Not read. Only the calling gate at `:2525-2600` |
| `frmMainPageV2.vb` `SendRateLimitedUpdate` (rejection handling for order *edits*) | Not read |
| `frmMainPageV2.vb` `ApplyCloseFill`, `CompletePositionClose` body | Grep only (confirmed `CompletePositionClose` calls `CancelOrderAsync`, which resets the slippage anchor) |
| `frmMainPageV2.vb` `cancelled` order-state branch (`:3541+`) and the startup restore (`:5730+`) | Partially read |
| `FrmIndicators.vb` (the app's own 14-period ATR, the slippage fallback) | Not read |
| `SessionPolicy.vb`, `AutoTradeSettings.vb`, `AppUserSettings.vb` | Grep only (defaults and seams) |
| `ExecutorFeedback.vb`, `TradeDatabase.vb`, `TradeRecord.vb`, `WsEdgeLog.vb`, `RemoteNotifier.vb` | Not opened (these are feedback, logging and persistence, not order placement) |

## Answers to the six severity questions

| Question | Answer (all from reading the code) |
|---|---|
| Who rounds prices, and in which direction? | The app does, to the nearest tick, with halves rounded away from zero. The rounding ignores whether it tightens or loosens the stop (`frmMainPageV2.vb:381`). The TP is rounded to the tick directly. The stop's limit price is rounded, and the trigger is then set to that limit plus the offset (`SignalBridge.vb:975`, `frmMainPageV2.vb:3842`). |
| Is the stop side checked against the actual fill before placement? | **No.** The only check on levels is `StopLevel <= 0 OrElse Target <= 0` (`SignalBridge.vb:687`). Sizing wraps the stop distance in `Math.Abs` (`:1053`), so an inverted stop is sized like a normal one. |
| What happens if Deribit rejects the stop after the entry fills? | Nothing notices. Rejection handling only covers the first placement ack (`frmMainPageV2.vb:1902-1951`). A position with no stop order is only detected by the restore scan at startup. |
| Which trigger do stops use? | `last_price` (`frmMainPageV2.vb:4090`). The stop leg is also sent with `trigger_offset` = 30. The repo's own `docs/spec-fill-reanchor-fix.md:15` says that makes the exchange trail the stop. |
| How does the reader open the file? | `File.ReadAllText`, which shares Read but not Delete (`SignalBridge.vb:857`). |
| Does anything read `kelly.*`? | No. It appears once, in a comment (`SignalBridge.vb:678`). |

## --- LOGIC TRACE ---

The rules below apply to both signals.
- **Gate chain:** the timestamp parses because date auto-conversion is off. "Fresh" means `now − generated_at_utc ≤ 2.5 × exec_resolution_min`, and both of those come from the payload itself. After that come schema = 1, not SKIPPED, the de-dupe check, state OK, direction LONG, levels > 0, tier, MTF, `BELOW_MIN_MOVE`, `ledger_mismatch`, and `ws <> DOWN`. Then session policy, the ARM/START interlock, and the operational gates: connected, rate limit, flat, no working entry, cooloff, breaker, time window, size > 0.
- **Act:** `SetTradeTargets(manualTP:=RoundToTick(target), manualSL:=RoundToTick(stop − 30))`, then `PlaceAutomatedOrder("long","limit")` and `ExecuteOrderAsync("BuyLimit")`.
- **Order sent (OTOCO):**
  - Entry: post-only buy at the **app's best bid**. `levels.entry` never reaches the exchange; it's used only for risk sizing.
  - TP: post-only sell limit.
  - SL: `stop_limit`, reduce-only, post-only, trigger = limit + 30, `trigger:"last_price"`, `trigger_offset:30`.
- **Slippage check at placement:** `IsATRSlippageExcessive` stores the current bid as its anchor on the first call and returns False (`:3721-3723`). It never compares against `levels.entry`.

Fees are in basis points of notional: 3.0 bp round trip when both legs are maker, 5.0 bp when the exit is taker. I ran the numbers through a Python copy of the app's pure functions (`proofs/signal-to-exchange-order-path/trace.py`; output in that folder's README). It is not their compiled code.

**Signal A (−1.5% flush): entry 59,003.5 / stop 58,811.13 / target 59,079.15 / ATR 120.2**
- Placed: TP 59,079.0; stop trigger 58,811.0 (0.13 looser than the engine's); stop limit 58,781.0.
- Size: 500 USD with the default cap. If `max_size_usd` is 0 (no cap), it's 7,660.
- Slippage cap: 0.6 × 120.2 = 72.12 from the **first bid the app saw**.

| Fill | Gross TP / stop | Net win | Net loss (maker) | Net loss (market-stop fallback, taker) | Break-even win rate |
|---|---|---|---|---|---|
| 59,003.5 (at engine entry) | 12.80 / 32.63 bp | 9.80 | 35.63 | 49.49 | 78.4% / 83.5% |
| 58,953.5 (flush continued) | 21.29 / 24.17 | 18.29 | 27.17 | 41.05 | 59.8% / 69.2% |
| 59,075.6 (bounced, chased to the cap) | 0.57 / 44.79 | **−2.43** | 47.79 | 61.64 | a TP fill loses money |

**Signal B: 2 USD swing stop (59,001.5), same entry and target**
- Placed: stop trigger 59,001.5, which is 4 ticks under the engine's entry, triggered on `last_price`; stop limit 58,971.5.
- Size with risk sizing on: 500 USD capped, **737,540 USD uncapped**.
- At a 59,003.5 fill, the stop move is 0.34 bp against 3.0 bp of fees, so fees are 90% of the loss.
- In a flush, if the bid is 2.5 USD or more under the engine's entry when the order goes out, the long's stop trigger is **at or above the fill**. Nothing checks this. What Deribit then does with a sell stop whose trigger is already crossed is left to Deribit; I couldn't check which behaviour you get.

## Findings

**1. HIGH: a resting entry is never cancelled by the bridge, and has no time limit**
- **Location:** `SignalBridge.vb:521-533` (`ForceStop` only clears `_started`). The bridge never calls any cancel function. `orderCreationTime` at `frmMainPageV2.vb:3682` is never used.
- **Downstream impact:** a stale payload, a SKIPPED payload, engine ARM off, or an opposite signal each block new entries. None of them removes the post-only bid already on the book.
- **Failure scenario:** signal A's bid rests while price bounces. Two hours later a new flush fills it, using two-hour-old stop and target levels.
- **Critique:** a resting post-only bid in a falling market fills when price keeps falling through it, which is the worst time to buy. Refusing new trades is not the same as standing down, because this order stays live.

**2. HIGH: nothing checks that the stop and target are on the right side of the entry or the fill**
- **Location:** `SignalBridge.vb:687` and `:1053`; `frmMainPageV2.vb:3840-3842`.
- **Downstream impact:** a long from the engine with its stop above entry (the bad-settings case) goes through every gate and is sized normally. So does a correct 2 USD stop once price has moved 2.5 USD.
- **Failure scenario:** case C in `trace.py` (stop 59,050): the stop trigger sits 46.5 above a 59,003.5 fill.
- **Critique:** what happens next depends on how Deribit treats that stop. If it fires immediately, you pay fees for nothing. If it's rejected after the fill, see finding 7: the position has no stop. The app does no check that would pick one of these on purpose.

**3. HIGH: the slippage cap is anchored to the app's first bid, not `levels.entry`, and the chase never checks the target**
- **Location:** `frmMainPageV2.vb:3721-3723` and the chase loop at `:2371-2402`. The EV floor defaults to off: `AppUserSettings.vb:75`.
- **Downstream impact:** "0.6 × ATR from `levels.entry`" is not enforced anywhere in the app. The entry can be chased right up to the TP.
- **Failure scenario:** the third row of the signal A table. Fill at 59,075.6; a TP fill loses 2.43 bp; the stop distance grows 37% beyond what sizing assumed.
- **Critique:** the slippage limit is measured from where the app happened to start, not from the price the engine's levels were built on.

**4. HIGH: the exchange doesn't guarantee the stop fills once triggered; the app has to finish the job**
- **Location:** the post-only `stop_limit` leg at `:4081-4090`. The market-stop fallback is at `:2575-2580` and depends on the `chkMarketStopLoss` checkbox. `TryStart` (`SignalBridge.vb:316`) requires only the Max Slippage ATR checkbox, not that one.
- **Downstream impact:** once the stop triggers, it becomes a post-only sell that gets repriced to the ask and then chased by the app. The 70 USD market fallback also runs in the app.
- **Failure scenario:** a flush plus a socket drop, or the fallback checkbox unticked. The post-only sell keeps being repriced to the ask while price falls. The loss has no upper bound.
- **Critique:** in a flush, the engine's stop level decides when the stop triggers, not what price you exit at.

**5. HIGH if `max_size_usd` ≤ 0: risk sizing on a 2 USD stop produces 737,540 USD**
- **Location:** `SignalBridge.vb:1123-1128`.
- **Downstream impact:** the default cap of 500 is the only thing stopping this. The newest commit specs "Max Size = 0 means NO CAP" for the settings form too.
- **Failure scenario:** signal B with risk sizing on and no cap. Either Deribit rejects it for margin, or it fills at hundreds of times leverage with a stop inside the spread.
- **Critique:** fees aren't in the formula, and the stop that actually runs (see finding 6) is not the stop the size was calculated for.

**6. MEDIUM-HIGH: `trigger_offset` = 30 turns the engine's stop into a trailing stop**
- **Location:** `:4086`. The repo's own docs assert the exchange trails it: `docs/spec-fill-reanchor-fix.md:15`.
- **Downstream impact:** the stop that actually runs is neither the engine's 192-USD stop nor its 2-USD stop.
- **Failure scenario:** if the trail follows the peak minus 30, signal A's risk:reward and signal B's sizing are both wrong.
- **Critique:** I couldn't confirm Deribit's semantics for `trigger_price` combined with `trigger_offset`; access to docs.deribit.com is blocked from here.

**7. MEDIUM: a stop leg that fails after the fill leaves a position with no stop, and nothing raises an alarm**
- **Location:** `frmMainPageV2.vb:1902-1951`. The only "open position, no stop" check is the startup restore at `:5730+`.
- **Downstream impact:** the "accepted" ack only covers the primary (entry) order. The TP and stop legs are created by the exchange at fill time.
- **Failure scenario:** combine finding 2 with a post-fill rejection of the stop leg: an open position with no stop, while the app shows "In Position".
- **Critique:** one ack is being taken as proof that the whole OTOCO order took effect.

**8. MEDIUM: freshness is judged by emission time and a window the payload sets for itself**
- **Location:** `SignalBridge.vb:602`. `exec_resolution_min` has no upper bound. Only `ws: DOWN` is refused (`:701`).
- **Failure scenario:** the engine's data is stale but `generated_at_utc` is current, so the payload passes as fresh. A payload with `exec_resolution_min: 60` stays valid for 150 minutes.
- **Critique:** the payload being checked supplies both the timestamp and the window it's checked against.

**9. MEDIUM: nearest-tick rounding ignores direction, and the trigger stays on the tick grid only by coincidence**
- **Location:** `:381`, `SignalBridge.vb:975`.
- **Examples:** 58,811.30 rounds the trigger to 58,811.5 (tighter). 58,811.13 rounds to 58,811.0 (looser). With a 2 USD stop, that's ±12.5% of the stop distance.
- **Failure scenario:** an offset of 12.3 gives a trigger of 58,811.3, which is off the 0.5 tick grid. The whole OTOCO order gets a −32602 rejection. That fails safe, but the signal is lost.
- **Critique:** a code comment claims "the offset is integer". Nothing enforces it.

**10. LOW-MEDIUM: the reader can cause the engine's failed-rename problem**
- **Location:** `SignalBridge.vb:857`, `:862`.
- **Mechanism:** because the reader doesn't share Delete, the engine's `File.Move(overwrite)` or `File.Replace` fails with a sharing violation while a read is in progress. The window is one short read, 150 ms after the last file event.
- **Also:** `UnauthorizedAccessException` is not an `IOException`, so it skips the retry and the payload is dropped.
- **Mitigation already in place:** the freshness check stands the app down within 2.5 × exec resolution.

**11. LOW: placement failures inside the app are reported as `rejected: timeout`**
- **Location:** the early `Return`s in `ExecuteOrderAsync`, which happen before `RegisterPendingPlacement`, and `SignalBridge.vb:833`.
- **Impact:** a bad quote or an exception looks like an exchange timeout after 5 seconds. The signal tag stays staged and can attach to the next manual trade.

**12. INFO: `kelly.*` is never read**
- The engine's mismatch between the Kelly stop and the levels stop has no effect in this app.

## How the engine findings rank from this side

| Engine finding | Severity here | Why |
|---|---|---|
| Inverted stop | High | Finding 2 |
| 2 USD `SWING_STOP` with 0.6 × ATR slip | High | Findings 2, 3 and 5 |
| `generated_at_utc` is emission time | Medium | Finding 8 |
| `ws: OK` ≠ trades flowing | Medium | Finding 8 |
| Levels not tick-rounded | Low | The app rounds, but see finding 9 |
| Stale file when the rename fails | Low | The reader can cause it (finding 10), and freshness catches it |
| `kelly.*` computed against a different stop | None | Not read |

## What I did not verify

- I couldn't compile or run the app: .NET isn't installed in the audit environment.
- `trace.py` copies the app's formulas into Python. It ran, and the numbers above are its output, but it isn't their code.
- `AuditFixtures.vb.txt` is an OrderCheck fixture written against the app's own `Friend Shared` functions. **It hasn't been run.** The run steps are in the proofs README.
- Three pieces of Deribit behaviour decide how bad findings 2, 4 and 6 are, and I couldn't check any of them because docs.deribit.com is blocked:
  - what `trigger_offset` does on a `stop_limit` order;
  - what happens to a sell stop whose trigger is already crossed when the OTOCO legs are created;
  - how a triggered post-only stop gets repriced.
- The members listed under "Coverage" above were not read. Findings 4 and 7 in particular could be softened, or made worse, by `UpdateStopLossForTriggeredStopLossOrder` / `ForceStopLossUpdate` and by the `cancelled` order-state branch.
