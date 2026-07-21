# Coordinator review — housekeeping v2 bundle (`141f81d..20338ea`, 24 code commits + report)

**Date:** 2026-07-22 · **Reviewer:** coordinator seat (Fable; implementer was a Sonnet-medium
subagent per the spec's routing) · **Verdict: APPROVED — all 24 commits.** Owner runtime pass
pending on the three [owner-visible] items (below).

**Method:** range overview + protected-file check (`SignalBridge.vb`, `SessionPolicy.vb`, `tools/`
— ZERO diff across the range, as required); full-diff reads of the risk-bearing commits (15, 14f,
14d, 12+13, 18, 8b, 9); tripwire greps re-run; **`tools/checks/verify-gate.ps1` EXECUTED at HEAD**
→ Release 0/0, Debug 0/0, OrderCheck **96/96**, GATE PASSED.

## Risk-bearing commits — verified individually

- **Item 15 (`bd34fbf`) — the mandated line-by-line review: PASS.** Every converted site is
  display-only (LED, top-bid/ask text, PnL label, placed-price/margin display clears,
  margin/leverage labels); the PnL 6→3 consolidation hoists color into locals before the lambda
  (correct closure discipline) and is value-equivalent; `AppendColoredText`'s `BeginInvoke` keeps
  the handle guard and item I's save/restore inside the one marshalled action. The exclusion list
  was respected — the two engine-mutating `HandleOrderPositionUpdates` lambdas, `SetTradeTargets`/
  `PlaceAutomatedOrder`, and `LogTradeDecision` are untouched.
- **Item 14f (`7d95efd`):** arms `cancelPending`/`cancelPendingSince` exactly as the nuclear cancel
  does, before the id-30 send; no reroute through `CancelOrderAsync` (the spec's own red line).
  **Reviewer-noted residual edge, accepted:** if the `cancel_all` finds nothing open, no `cancelled`
  echo arrives and the gate stays armed up to the 4-s self-clear, suppressing the trailing
  placement's own echoes in that window (the raced-abort suppression shape, but bounded and on a
  path only reachable with a live SL to cancel). Inherent to the spec's chosen minimal design; if a
  trailing transition ever shows stale boxes, look here first.
- **Item 12+13 (`f941422`):** mirror-field swaps are equivalence-preserving (mirrors sync on
  TextChanged); the receive-path block is a parse-swap only, structure intact; **Edit T.S.
  (`btnEditSLPrice_Click`, id 223346) keeps byte-equivalent semantics** — blank input now warns
  instead of throwing, `EditStopLossTo` and the no-recording rule untouched. `btnEstimateMargins`
  now also refuses ≤0 input (strictly better than the old IsNumeric).
- **Item 14d (`820ff38`):** trim inside the marshalled action, UI-thread only. Accepted cosmetic
  nuances: a user selection in progress on the exact trim tick is lost, and `atBottom` is computed
  pre-trim — both rare and display-only.
- **Item 18 (`06176dd`):** exact F-2 pattern (`.tmp` + `File.Move overwrite`), error handling
  unchanged.
- **Item 8b (`8908c6e`):** exactly the six specced pre-placement sites gain
  `maxSlippageATRchecked AndAlso`; nothing else in those lines changed.
- **Item 9 (`24084f9`):** exactly the four entry-button delays deleted; the heartbeat blink delay
  (`:1715`) survives, verified by grep — it is the only `Task.Delay(500)` left.

## Tripwires — all hold at HEAD

`ResetCommandedSLPrices` = definition + 7 call sites (8 lines) · `cancelPending = False` = 5 sites
(the 4 pre-existing + the raced-abort repair; 14f adds only a `True` site) ·
`RecordCommandedSLPrice` = 3 · protected post-July-6 code (gate chain, policy, raced-abort block,
seed ordering, `ApplyBridgeStatusLine`) untouched.

## Skips and flags

- **14b SKIPPED** correctly (the tie-in re-code already removed the `Me.Hide()`), per the addendum's
  prediction.
- **Item 8 drift flag stands for the owner** (decision, not code): the trailing-LONG slippage gate
  passes `bestAsk` where entry-LONG uses `bestBid` — audit F18 calls it copy/paste drift; changing
  it alters armed-guard behaviour, so it ships as-is until ruled.

## Owner runtime checklist (the spec's smoke tests — pending, normal-trading opportunistic)

1. After 8b/9: place + cancel a test order with the slippage checkbox OFF → expect ZERO slippage
   log lines; then ON → guard behaves as before. Entry buttons should feel ~0.5 s snappier.
2. 14g: long-position PnL now marks to the BID (value and colour) — expect slightly more
   conservative long PnL display.
3. After 7: any DB operation (view trades grid) as the package-removal smoke.
4. After 15: a normal session — quotes flowing, log lines in order, cancel/close displays clearing.
