# Investigation — the triple placement (WATCH protocol closed), 2026-08-01

> **⚠ CORRECTION 2026-08-01 (same day), from `review-placement-single-flight.md`.**
> §3/§4 below say N actuations produce N ***concurrent*** `ExecuteOrderAsync` calls. **The
> "concurrent" part is WRONG.** The evidence gathered here supports ***multiplicity*** — three
> orders, one cached price, three log lines, reduce of 30 — every part of which is equally explained
> by three *sequential* complete executions. It was inference presented as mechanism.
> The v1 fix specced against it (an `Interlocked` single-flight latch) **failed its acceptance with
> the defect fully intact**: had any two invocations overlapped, the latch would have blocked one;
> three orders landed, so **none overlapped**. The UI message pump dispatches queued clicks strictly
> sequentially, each handler completing and releasing before the next is dispatched.
> **Everything else in this record stands** — the harness exoneration (53/53), the dead
> `ButtonDisabler`/`ButtonEnabler` finding, the absence of any re-entrancy guard, and the
> reproduction itself. The defect is real and the diagnosis of *where* it lives is right; only the
> *why* was wrong. The fix of record is now `spec-placement-single-flight-v2.md` (a time-based
> debounce — it is a rate problem, not an overlap problem).

**Owner-authorised.** The `ROADMAP-2026-08.md` §5 WATCH protocol named its own exit — *"protocol
until investigated … that session becomes the investigation"* — and this is that session. Driven by
the coordinator (Opus seat) on the harness-launched **AnyCPU Debug** bin, TESTNET, min size. The
owner's x64 journal was never touched (verified: `bin\x64\Debug\trades.db` unmodified throughout;
all trades landed in `bin\Debug\trades.db`).

## VERDICT

**The harness is exonerated, and the actual defect is in the app.** A single UIA `Invoke()` places
exactly one order — 53 times out of 53. But the placement buttons are **never disabled while an
order is in flight**, and the six `Async Sub` placement handlers carry **no single-flight guard**, so
*N* actuations from any source produce *N* concurrent orders. Firing three `Invoke()`s in 33 ms
reproduced the 2026-07-27 signature **exactly**: three entries at an identical price, three log
lines, and a reduce of 30.

**This exposure is not harness-specific and is live on the owner's LIVE sessions today.** A
double-click on any placement button places two orders.

## 1. Reproduction attempt — 53/53 clean

The original incident was *"on the FIRST placement only"*, with the next two placements of that same
session clean. So the hypothesis under test was cold-start-first-placement, which needs many app
*sessions*, not many clicks. Three arms:

| Arm | Sessions × placements | Result |
|---|---|---|
| Warm loop (repeat placements in one session) | 1 × 3 | 3/3 exactly one entry |
| Cold start, first placement only | 10 × 1 | 10/10 exactly one entry |
| Cold start + the geometry dance the original session performed first (`set-textbox -CommitViaBlur` on three boxes, i.e. focus manipulation immediately before the click) | 40 × 1 | 40/40 exactly one entry |
| **Total** | **53 placements** | **53/53, every reduce exactly 10** |

Zero events in 53 trials puts the 95% upper bound on a spurious multi-invoke rate at **~5.7%**
(rule of three). The naive 1-in-3 reading of the original tally is excluded overwhelmingly — at
p = 1/3 the chance of 53 consecutive clean placements is about 5 × 10⁻¹⁰.

**The harness does not spuriously multi-invoke.**

## 2. A third hypothesis, excluded by the code

The original triage framed the cause as *"either the button was invoked three times (harness/UIA
side) or the handler re-entered"*. A third possibility — a transport-level retry or exchange-side
duplication — would produce three **orders** but only **one** log line, because
`ExecuteOrderAsync` prints its line once per invocation, after its own single
`SendWebSocketMessageAsync`. There is no retry logic anywhere in the placement path. Three log
lines therefore mean three executions of the method body. Excluded.

## 3. The finding: the guard exists and is never called

`frmMainPageV2.vb` contains a purpose-built pair:

```vb
Private Sub ButtonDisabler()    ' btnLimit/btnNoSpread/btnTrail/btnMarket -> Enabled = False
Private Sub ButtonEnabler()     ' ... -> Enabled = True
```

**Neither has a single call site.** Lines 5086–5104 are the *only* writes to any placement button's
`.Enabled` in the entire codebase (verified exhaustively across all `*.vb`), and `git log -S` traces
both back to the initial import — legacy from the retired `frmMainPage`, carried into V2 and never
wired up.

Consequently all six placement handlers are `Async Sub` with the button live throughout the await:

```vb
Private Async Sub btnMarket_Click(...) Handles btnMarket.Click
    btnEstimateMargins_Click(Nothing, Nothing)
    Await ExecuteOrderAsync("BuyMarket")     ' button remains clickable for this entire duration
```

The six: `btnLimit`, `btnNoSpread`, `btnTrail`, `btnMarket` (entries) and `btnReduceLimit`,
`btnReduceMarket` (exits). (`btnBuy`/`btnSell` are plain `Sub` direction toggles, not placements.)

**This is conspicuous against the file's own conventions.** Every *automated* path is single-flighted
with `Interlocked.Exchange` — `isRepositioning`, `isSLRepositioning`, `refreshInFlight`,
`isReconnecting`. Every *user-actuated placement* path is not.

## 4. Confirmatory reproduction

Three `Invoke()`s against one resolved element, TESTNET and harness-PID gates intact:

```
Resolved 'Mkt. BUY'. Enabled BEFORE burst: True
  invoke 1 at t+20 ms; button IsEnabled now = True
  invoke 2 at t+23 ms; button IsEnabled now = True
  invoke 3 at t+26 ms; button IsEnabled now = True
3 invokes issued in 33 ms
```

Result:

```
Market buy order placed For 10 starting at 62763.5.
Market buy order placed For 10 starting at 62763.5.
Market buy order placed For 10 starting at 62763.5.
Cancelled all open orders
Reduce-only MARKET sell 30  order sent.
Position reduced at 62763.48 (market order).
Scratch close: P/L = $0.00.
```

Every element of the 2026-07-27 record matches:

| Recorded 2026-07-27 | Reproduced 2026-08-01 |
|---|---|
| three entries, **identical price** (same tick) | three entries at 62763.5 |
| three log lines | three log lines |
| **reduce of 30** on a size-10 intent | `Reduce-only MARKET sell 30` |

`IsEnabled = True` after every invoke is the direct observation of the missing lockout.

## 5. What this revises

The WATCH protocol was written on a 5-clean-vs-1 tally that *"leans harness-side, unproven"*. The
lean was wrong. The harness reliably places one order per Invoke; the app has no defence against
multiple actuations from **any** source — UIA, a double-click, a stuck mouse button, an RDP input
replay, an accessibility tool. Restricting the harness never addressed the exposure, and the
exposure has been open on every placement path, on LIVE, the whole time.

## 6. What is NOT established

- **What produced three actuations on 2026-07-27 remains unknown.** This investigation shows the
  harness does not do it at any measurable rate, which makes a one-off external cause more likely —
  but that is inference, not evidence. The app-side gap is what made a one-off harmful, and that is
  what is now proven and fixable.
- The burst here was UIA-driven. A physical double-click was not tested (it needs a human hand);
  the mechanism is actuation-source-agnostic, so it should behave identically, but it is not observed.
- Only `btnMarket` was burst-tested. The other five handlers share the identical shape, so the same
  conclusion follows by inspection rather than by measurement.

## 7. Recommendations

1. **`spec-placement-single-flight.md`** — a single-flight guard on the six handlers. Order-placement
   path ⇒ **Opus HIGH implementer, own pass**. Written alongside this record.
2. **Amend the WATCH protocol** on the corrected premise (§5 of the roadmap): the restriction on
   harness placement is lifted, replaced by a mandatory effect assertion, and the real exposure is
   re-pointed at the app until the guard lands.
3. **`tools/place-and-verify.ps1`** (added with this record) wraps `click-PLACES-ORDER.ps1` and
   asserts the log effect of every harness placement, dumping the log **and** the UIA tree on
   mismatch. Any future recurrence fails loudly instead of silently corrupting a result — the
   standing harness-commit-verification lesson applied to placements.
