# Coordinator review — placement debounce (SF2)

**Reviewed:** `550d673` (the debounce) · `b88d1da` (impl report), against
`spec-placement-single-flight-v2.md` (§2 ruled at 500 ms).
**Reviewer gate at HEAD: GATE PASSED, OrderCheck 153/153.** Censuses re-run independently.
**All four runtime acceptances executed by the reviewer on the harness** (permitted by the amended
WATCH protocol), TESTNET, trades #65–#67 in the harness DB — the owner's x64 journal untouched.

## VERDICT: **APPROVED. The defect is FIXED — confirmed at runtime, not argued.**

The exposure that has been open on LIVE since the initial import is closed.

## 1. Acceptance 2 — the instrument that killed v1 — PASSES

Verified in force first, against the standing stale-binary trap: the running assembly contains
**both** `lastPlacementAdmittedUtc` and `isPlacingOrder`.

```
*** BURST: 3 actuations of 'Mkt. BUY' ***
  invoke 1 at t+16 ms;  invoke 2 at t+23 ms;  invoke 3 at t+25 ms
Burst issued (TESTNET): 3 invokes in 27 ms
Effect verified: 'Market buy order placed' x1 after one Invoke of 'btnMarket'.
...
Reduce-only MARKET sell 10  order sent.
```

**One entry, reduce of 10.** The direct before/after, same instrument, same command:

| | v1 (latch only) | v2 (debounce + latch) |
|---|---|---|
| 3 actuations in ~27 ms | **3 entries**, one price | **1 entry** |
| flatten reports | `sell 30` | `sell 10` |

## 2. The other three acceptances

- **1 — Gate:** PASSED, 153/153, re-executed by the reviewer at HEAD.
- **3 — Legitimate sequential placement not regressed:** two placements more than one window apart
  both landed; flatten reported `sell 20`. The window blocks duplicates, not work.
- **4 — The leak case:** a placement while disconnected logged `WebSocket is not connected.` and
  placed nothing; after reconnecting, the next placement succeeded. Neither the latch nor the stamp
  wedged on the failure path.
- **5 — Emergency exclusion:** verified structurally, which is stronger than a runtime probe here.
  `SendReduceMarketOrderAsync` has four callers: `FlattenPositionAsync` (`:793`) and the two
  emergency sites (`:4396`, `:4406`) call it **directly**, never entering a handler body, so no
  debounce statement lies on any path reachable from them; only `btnReduceMarket_Click` (`:6102`) is
  behind the guard. `PerformClick` appears nowhere in the codebase except four comments (three of
  them tombstones for the cross-thread fix that removed exactly this coupling), and there is no
  `Application.DoEvents`. **An emergency reduce arriving 1 ms after an admitted manual placement is
  not swallowed.** `emergencyFired` census unchanged at 10.
- **6 — Censuses**, scoped to `frmMainPageV2.vb`: `isPlacingOrder` 13 · `lastPlacementAdmittedUtc`
  13 (1 decl + 6 reads + 6 stamps) · `emergencyFired` 10 · `IsATRSlippageExcessive` 8 ·
  `NextSlBackoff` 2 · `RecordCommandedSLPrice` 3 · `slUpdateFailures = 0` 1. All as specced.

## 3. Statement order — checked in the file, not in the diff

The one thing a filtered diff cannot show. §3 requires the stamp **after** the latch take, so a
losing actuation cannot move it. Confirmed by reading all six handlers directly:

```vb
If (DateTime.UtcNow - lastPlacementAdmittedUtc).TotalMilliseconds < PlacementDebounceMs Then Return
If Interlocked.Exchange(isPlacingOrder, 1) = 1 Then Return
lastPlacementAdmittedUtc = DateTime.UtcNow
Try
```

Identical in `btnLimit`, `btnNoSpread`, `btnMarket`, `btnReduceLimit`, `btnReduceMarket`, `btnTrail`
— no variation. Worth recording as method: in the unified diff the two added lines appear adjacent
because the latch line is unchanged context, which reads as though the stamp precedes the take. It
does not. **A reviewer who checks ordering from a diff alone will get this wrong.**

## 4. The transferred proof obligation — correctly identified and discharged

The implementer noticed, unprompted, that v1's review had verified "zero statements between the
acquire and the `Try`", and that the new stamp now sits precisely there — so the leak-freedom proof
does not carry over for free. That is exactly the right instinct, and the discharge is sound:
`lastPlacementAdmittedUtc = DateTime.UtcNow` is a static clock read stored into a value-type field of
a non-null `Me`, with no conversion, boxing, allocation or user code, so it cannot throw and cannot
strand the latch. The rest of v1's proof is untouched: 6 takes, 6 releases, `Finally` on every path,
`btnReduceLimit`'s early `Return`s inside the `Try`.

## 5. Residuals — all four accepted as argued

1. **`DateTime.UtcNow` is not monotonic.** A backward clock step blocks placement until the clock
   catches up. Accepted: NTP slews rather than steps; every other stamp in the file uses the same
   clock; and the failure direction is safe **because §4 keeps the emergency path outside the
   debounce entirely** — it can delay an entry, never an exit. `Stopwatch.GetTimestamp()` would
   remove it but that is a change to a ruled mechanism and needs its own ruling.
2. **`btnReduceLimit`'s no-op paths consume a window** ("No open position to reduce", "Invalid
   price" — both after the stamp). Accepted: bounded at one window, errs toward blocking rather than
   placing, and follows §3 as written. Correctly flagged rather than silently "fixed", since moving
   the stamp past those returns would change a ticked spec.
3. **A physical double-click is still unobserved** — the burst is UIA-driven. Now the single most
   interesting ten-second check available, because 500 ms was chosen precisely as the double-click
   threshold. See §6.
4. **Silent rejection.** Raised for ruling, not added — see §6.

## 6. Two things for the owner

- **RULING — the gray line: NOT added.** Keep the silent `Return`. A duplicate actuation is the
  user's own stray input; logging it every time would put a line in the trading log for a
  non-event, and the log's value is that everything in it matters. The condition is observable
  when it is actually needed — the harness wrapper asserts placement effects directly. If a case
  ever arises where a dropped click is genuinely mysterious, revisit then.
- **One optional ten-second check, owner-mouse:** double-click `Mkt. BUY` on testnet and confirm one
  entry. It closes residual 3 and is the only acceptance evidence the harness structurally cannot
  produce. Not blocking — the mechanism is actuation-source-agnostic and the UIA burst is a strictly
  harder case than a human double-click (27 ms vs 100–300 ms, both inside the 500 ms window).

## 7. What this arc cost, and what it bought

Two implementation passes for a nine-line fix, because the first was specced against a mechanism I
inferred rather than measured. The correction is recorded on the investigation record itself.
What it bought: the era's standing lesson gained a corollary that is now proven twice over —
**an acceptance must test the DEFECT, not the fix's theory of it.** Acceptance 2 was defined as the
reproduction rather than as "the latch is taken", which is the only reason v1's failure surfaced in
a testnet harness run instead of on the owner's live account.
