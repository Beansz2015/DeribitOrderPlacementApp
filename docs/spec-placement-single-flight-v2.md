# Micro-spec v2 — placement debounce (supersedes the single-flight-only approach)

**Status: OWNER-TICKED 2026-08-01, §2 RULED — `PlacementDebounceMs = 500`. Ready for a fresh
Opus-HIGH implementer seat; it is the head of the queue (live exposure on LIVE).**
Supersedes `spec-placement-single-flight.md` as the fix of record; that spec's output
(`569a834`, `1773c94`) is **KEPT, not reverted** — see `review-placement-single-flight.md` §4.
**Implementer: Opus HIGH, fresh conversation, own pass** (order-placement path). Ships ON.

## §1 — Why v1 was insufficient

v1 added an `Interlocked` single-flight latch. Acceptance 2 then failed with the defect fully
intact: 3 actuations in 26 ms ⇒ 3 entries ⇒ reduce of 30, with the latch verifiably in the running
binary.

**The latch proves its own irrelevance.** Had any two invocations overlapped, one would have been
blocked; three orders landed, so none overlapped. The UI message pump dispatches the queued clicks
**strictly sequentially**, each handler completing — and releasing the latch in its `Finally` —
before the next is dispatched. Mutual exclusion cannot exclude events that never coincide.

The defect is a **rate** problem, not an **overlap** problem.

## §2 — The debounce window: **RULED 500 ms** (owner, 2026-08-01)

> **RULING: `PlacementDebounceMs = 500`** — the Windows default double-click time, which is the
> natural Schelling point: it is exactly the threshold below which the OS itself considers two
> clicks to be one gesture rather than two intents. Do not "tune" this number without a new ruling;
> if it ever needs to change, change it as a named constant with a comment pointing here, and note
> that a value below ~300 ms stops catching real double-clicks. The reasoning that produced it:

Reject a placement actuation arriving within `PlacementDebounceMs` of the previously **admitted**
one. The window is a genuine trade-off and is the owner's call:

- It must **exceed the interval within a real double-click** to catch the accidental case. Windows'
  default double-click time is **500 ms**, and physical double-clicks typically land 100–300 ms
  apart.
- Any **legitimate** second placement typed inside the window is silently dropped. In this app's
  workflow a deliberate second placement within half a second is implausible — but it is the owner's
  hand-feel, not mine.

A stuck or repeating button is covered as long as the stamp is taken on admission only (below).

*(Alternatives considered and not taken: 300 ms is nearly invisible but catches fewer real
double-clicks; 750 ms+ is belt-and-braces at the cost of noticeably blocking rapid manual work.)*

## §3 — The change

One field, one constant, one line per handler — the admission point v1 already established:

```vb
Private Const PlacementDebounceMs As Integer = 500      ' §2, owner-ruled
Private lastPlacementAdmittedUtc As DateTime = DateTime.MinValue
```

At the top of each of the six handlers, **before** the existing latch take:

```vb
If (DateTime.UtcNow - lastPlacementAdmittedUtc).TotalMilliseconds < PlacementDebounceMs Then Return
If Interlocked.Exchange(isPlacingOrder, 1) = 1 Then Return
lastPlacementAdmittedUtc = DateTime.UtcNow
Try
    ...
```

**Design points, deliberate:**

- **Stamp on ADMISSION only, never on rejection.** If a rejected actuation refreshed the stamp, a
  stuck or repeating button would extend the lockout indefinitely and could wedge placement for as
  long as the input persists. Stamping only what we admit bounds the lockout at exactly one window.
- **The stamp is set AFTER the latch take**, so a losing actuation cannot move it.
- **Keep the latch.** It is leak-free (proven in the v1 impl report §2 and verified at review) and
  still guards genuine concurrency, which is reachable when a congested socket makes the send truly
  yield. v1's ordering, `Finally` release and dead-code deletion all stand.
- **One shared stamp across all six**, mirroring the shared latch: two *different* placement buttons
  inside the window is the same hazard as one pressed twice.
- **Silent `Return`**, consistent with v1. If the owner wants visibility, a gray
  `Duplicate placement ignored (debounce)` line is a one-line addition — raise at review.

## §4 — Out of scope (unchanged from v1 §3)

The bridge act path (`PlaceAutomatedOrder`), the emergency/flatten sites (they reach
`SendReduceMarketOrderAsync` directly and must never be debounced — an emergency stop is not a
duplicate), `btnBuy`/`btnSell`, `btnCancelAllOpen`, and `ExecuteOrderAsync` itself.

⚠ **The emergency exclusion is load-bearing.** The debounce must live in the six *handlers*, never
inside `SendReduceMarketOrderAsync` or `ExecuteOrderAsync`, or an emergency reduce arriving just
after a manual placement would be swallowed. That is the one change that would turn this fix into a
new and much worse defect.

## §5 — Acceptance

1. Gate per commit; fixtures counted.
2. **THE acceptance — the reproduction must stop reproducing.** Same instrument, same call:
   ```bash
   powershell -NoProfile -File tools/place-and-verify.ps1 "btnMarket" "Market buy order placed" -Actuations 3 -ExpectCount 1
   ```
   Must report `Effect verified: ... x1`, and the flatten that follows must report a reduce of
   **10**, not 30. This is the acceptance of record and it is now automatable end-to-end.
3. **Legitimate sequential placement still works**: two placements more than one window apart both
   succeed (this is v1's acceptance 3, which passed — it must not regress).
4. **The leak case**: a placement after a failed/disconnected one still succeeds (v1 acceptance 4,
   passed — must not regress).
5. **The emergency path is NOT debounced**: verify by inspection that no debounce check exists on
   any path reachable from `FlattenPositionAsync` or the two emergency sites — plus the existing
   `emergencyFired` census unchanged at 10.
6. Censuses: `isPlacingOrder` 13 · `lastPlacementAdmittedUtc` = 1 decl + 6 stamps + 6 reads = 13 ·
   `emergencyFired` 10 · `IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 ·
   `RecordCommandedSLPrice` 3 · `slUpdateFailures = 0` 1.

## §6 — Note for the reviewer

v1's lesson: **do not accept a static derivation of the mechanism.** Run acceptance 2 before
believing anything, and run it against a binary you have verified contains the change (the v1 review
checked the assembly for the field name — do the same). The failure mode this fix targets was
mis-diagnosed once already, from evidence that looked conclusive.
