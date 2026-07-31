# Coordinator review — placement single-flight (SF)

**Reviewed:** `569a834` (latch) · `1773c94` (dead-code deletion) · `963a9c5` (impl report).
**Gate re-executed by the reviewer at HEAD: GATE PASSED, OrderCheck 153/153.** Censuses re-run
independently — `isPlacingOrder` 13 (1 decl + 6 takes + 6 releases, takes and releases both exactly
6), `emergencyFired` 10, `IsATRSlippageExcessive` 8, `NextSlBackoff` 2, `RecordCommandedSLPrice` 3,
`slUpdateFailures = 0` 1. Acceptances 2/3/4 run by the reviewer on the harness (permitted by the
amended WATCH protocol), TESTNET, trades #63/#64 in the harness DB.

## VERDICT: **ACCEPTANCE 2 FAILED — the defect is NOT fixed. The implementation is faithful; the
SPEC was wrong, and the spec was mine.**

The code does exactly what `spec-placement-single-flight.md` asked for, and does it well. What it
guards against is not what is happening.

## 1. The failing acceptance

Acceptance 2 is the reproduction from the investigation — the instrument that demonstrated the
defect. Post-fix, with the identical call:

```
*** BURST: 3 actuations of 'Mkt. BUY' ***
  invoke 1 at t+12 ms; IsEnabled now = True
  invoke 2 at t+16 ms; IsEnabled now = True
  invoke 3 at t+20 ms; IsEnabled now = True
Burst issued (TESTNET): 3 invokes in 26 ms

Market buy order placed For 10 starting at 63009.5.
Market buy order placed For 10 starting at 63009.5.
Market buy order placed For 10 starting at 63009.5.
Reduce-only MARKET sell 30  order sent.
```

**Three entries, one price, reduce of 30 — byte-for-byte the pre-fix result.**

**The fix was verified to be in force**, against the standing stale-binary trap: the running
`bin\Debug` assembly contains the string `isPlacingOrder` and does **not** contain `ButtonDisabler`.
This is not a stale-build artifact.

## 2. Why single-flight cannot work here — the latch proves its own irrelevance

If any two of the three handler invocations had overlapped, the latch would have blocked one and
fewer than three orders would exist. **Three orders exist. Therefore no two invocations overlapped.**

The actuations are processed **strictly sequentially** by the UI message pump: each
`btnMarket_Click` runs to completion — acquiring the latch, placing, and releasing it in the
`Finally` — before the pump dispatches the next queued click. The most likely reason is that the
await chain completes synchronously (a small `SendAsync` on a healthy socket typically does not
yield), so the handler never returns to the pump mid-flight. But the precise reason is secondary:
the sequential conclusion follows from the observation alone, and it is fatal to the approach. A
mutual-exclusion primitive cannot exclude things that never coincide.

## 3. Where the spec went wrong — my error, stated plainly

`investigation-triple-placement-2026-08-01.md` §3/§4 asserted that N actuations produce N
***concurrent*** `ExecuteOrderAsync` calls. The evidence only ever supported ***multiplicity***:
three orders, one cached price, three log lines, reduce of 30. Every one of those is equally
explained by three *sequential* complete executions — which is what they now demonstrably are.
Concurrency was inference presented as mechanism, and the spec was written against the inference.
The identical price, which felt like strong evidence of a same-tick race, is explained just as well
by three sequential placements inside 26 ms: the quote simply does not move in that window.

The implementer had no way to catch this. The spec named the mechanism, the fix follows from the
mechanism, and acceptance 2 was correctly deferred to runtime — where it did its job and caught it.
**This is the acceptance-instrument discipline working exactly as intended**, and it is the third
time this era that a runtime acceptance has overturned a static derivation.

## 4. What to KEEP

Nothing here should be reverted:

- **The latch is correct, leak-free, and worth keeping.** §2 of the impl report is a genuinely good
  proof, and I verified its load-bearing claims independently: the acquire sits outside the `Try`
  with zero statements between (so a losing actuation cannot release a latch it does not own);
  `btnReduceLimit`'s two early `Return`s are inside the `Try`; no `PerformClick` on any of the six
  buttons exists (only historical comments); no `Application.DoEvents` anywhere; the emergency and
  flatten sites reach `SendReduceMarketOrderAsync` **directly** (`:793`, `:4374`, `:4384`) so an
  emergency stop can never be blocked by an in-flight manual placement; and the ack-awaiting
  `Task.WhenAny` is in `PlaceAutomatedOrder` (`:737`), not in any of the six — so the latch is held
  across one WS **send**, not a round trip.
  It still guards genuine concurrency, which is reachable when the socket is congested enough for
  the send to truly yield. It is simply not sufficient on its own.
- **The dead-code deletion is right** and the tombstone comment is the right way to do it.
- **Acceptances 3 and 4 PASSED** and remain meaningful: two consecutive placements both succeeded
  (reduce of 20 — the `Finally` releases on the normal path), and a placement after a disconnected
  failure succeeded (`WebSocket is not connected.` → reconnect → `Effect verified: x1`), which is
  the leak case closed empirically as well as statically.

## 5. What is actually needed — `spec-placement-single-flight-v2.md`

The defect is a **rate** problem, not an **overlap** problem, so the fix must be time-based:
reject a placement actuation that arrives within a debounce window of the previously admitted one.
That works whether or not the actuations overlap.

**Keep the latch and add the debounce** — belt and braces, and the diff shape is already in place.
The debounce window is an owner ruling because it trades hand-feel against protection: it must
exceed the interval inside a real double-click (Windows' default double-click time is 500 ms) to
catch the accidental case, while any legitimate second placement typed inside that window would be
silently dropped.

Spec v2 is written alongside this review.

## 6. Residual, unchanged

A physical double-click is still unobserved — the burst is UIA-driven. The mechanism is
actuation-source-agnostic so the result should carry, but it remains inference, and it is the one
thing an owner-mouse check could settle in ten seconds.
