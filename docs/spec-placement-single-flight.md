# Micro-spec — single-flight guard on the placement handlers

**Status: OWNER-TICKED 2026-08-01 — NEXT, ahead of N2** (it is live exposure on LIVE, so it jumps
the queue; EV §6.3 follows it). Awaiting a fresh Opus-HIGH implementer seat. Origin:
`docs/investigation-triple-placement-2026-08-01.md` §3/§4 — the triple placement is reproduced and
explained. **Implementer: Opus HIGH, fresh conversation, own pass** (order-placement path). Ships
ON — this is a defect fix, not an opt-in feature, so there is no knob.

## §1 — The defect

Six placement handlers are `Async Sub` and leave their button **enabled for the whole duration of
the await**, with no re-entrancy guard:

`btnLimit_Click` · `btnNoSpread_Click` · `btnTrail_Click` · `btnMarket_Click` (entries) ·
`btnReduceLimit_Click` · `btnReduceMarket_Click` (exits)

*N* actuations during that window produce *N* concurrent `ExecuteOrderAsync` calls, each reading the
same cached `BestPrice`, i.e. *N* orders at one price. Reproduced: 3 invokes in 33 ms ⇒ 3 entries ⇒
a 30-USD position on a size-10 intent.

`ButtonDisabler()` / `ButtonEnabler()` (`:5085` / `:5097`) were written for exactly this and are
**never called** — legacy from the retired `frmMainPage`.

## §2 — The fix

Add an `Interlocked.Exchange` single-flight, matching the file's established pattern
(`isRepositioning`, `isSLRepositioning`, `refreshInFlight`, `isReconnecting`):

```vb
Private isPlacingOrder As Integer = 0
```

Each of the six handlers becomes:

```vb
Private Async Sub btnX_Click(sender As Object, e As EventArgs) Handles btnX.Click
    If Interlocked.Exchange(isPlacingOrder, 1) = 1 Then Return   ' a placement is already in flight
    Try
        ... existing body unchanged ...
    Catch ex As Exception
        ... existing catch unchanged ...
    Finally
        Interlocked.Exchange(isPlacingOrder, 0)
    End Try
End Sub
```

**Design decisions, deliberate:**

- **One shared latch across all six**, not one per button. Two *different* placement buttons pressed
  in the same window is the same hazard as one pressed twice (e.g. `Mkt. BUY` then `Limit BUY`
  before the first returns). A per-button latch would leave that open.
- **Silently `Return`, do not log.** The dropped actuation is the *user's own* duplicate; a warning
  line on every fast double-click would be noise. (If the owner wants visibility, a gray
  `Duplicate placement ignored` line is a one-line addition — raise it at review, do not add it
  unasked.)
- **`Finally`, not a plain post-await reset** — the existing handlers already swallow into a `Catch`,
  and a latch that leaks on an exception would wedge placement for the session. This is the one
  place the implementer must not take a shortcut.
- **Do NOT wire up `ButtonDisabler`/`ButtonEnabler`.** They also flip `btnReduce*`/`btnCancelAllOpen`
  enablement, which is a *position-state* concern with its own (unimplemented) lifecycle — adopting
  them would smuggle in an untested UI state machine. Delete them as dead code in the same commit,
  or leave them; the implementer should recommend, and say which they did.
- **The latch is UI-thread-only** (all six handlers run on the UI thread), so `Interlocked` here is
  belt-and-braces consistency with the file's convention rather than a strict necessity. Keep it
  anyway — uniformity is the point.

## §3 — Explicitly out of scope

- The bridge act path (`PlaceAutomatedOrder`) — it is not user-actuated and already serialised.
- `btnBuy`/`btnSell` — plain `Sub` direction toggles, no placement.
- Any change to `ExecuteOrderAsync` itself. The defect is actuation admission, not execution.
- The 6 pre-placement gates, chase gates, and every disposition/emergency invariant: untouched.

## §4 — Acceptance

1. Gate per commit; fixtures counted.
2. **The reproduction no longer reproduces.** Re-run
   `multi-actuation-probe.ps1`-style burst (3 `Invoke()`s in <50 ms) on TESTNET ⇒ **exactly one**
   `Market buy order placed` line and a reduce of **10**, not 30. This is the acceptance of record —
   it is the same instrument that demonstrated the defect.
3. **Normal single placement is byte-identical in behaviour** — one click, one order, on all six
   buttons. Latch taken and released; a second placement after the first completes must succeed
   (proves the `Finally` releases).
4. **Latch survives a failing placement**: force an error path (e.g. placement while disconnected)
   and confirm a subsequent placement still works — the leak case.
5. Greps: `emergencyFired` 10 · `IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 ·
   `RecordCommandedSLPrice` 3 · `slUpdateFailures = 0` 1 — all unchanged (this touches none of them).
   New: `isPlacingOrder` = 1 decl + 6 takes + 6 releases = 13.

## §5 — Note for the reviewer

The interesting question is **not** the latch, it is the `Finally`. Walk every exit path of all six
handlers — including the existing `Catch` arms and any early `Return` inside the bodies — and prove
the latch cannot leak. A wedged latch silently disables all placement for the rest of the session,
which is a worse failure than the defect being fixed.
