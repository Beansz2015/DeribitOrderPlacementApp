# Implementation report — placement debounce (SF2)

**Spec:** `docs/spec-placement-single-flight-v2.md` (owner-ticked 2026-08-01, §2 RULED at
`PlacementDebounceMs = 500`).
**Commit:** `550d673` — one code commit, one file, +35/−1.
**Base:** `5e7c581` (7 ahead of origin, tree clean, verified with `git rev-parse` on arrival).
**Gate:** executed at the commit and at final HEAD — **GATE PASSED, OrderCheck 153/153** both times.
**Implementer:** Opus HIGH, fresh seat, own pass. No trades placed, no bridge armed.

v1's latch (`569a834`) and its dead-code deletion (`1773c94`) are **KEPT**. This adds a debounce
*ahead* of the existing latch take; nothing was reverted.

## 1. What changed

| Item | Location | Note |
|---|---|---|
| `PlacementDebounceMs` constant + shared stamp field | `frmMainPageV2.vb:2076-2077` | immediately below the v1 latch field, sharing its comment neighbourhood |
| `btnLimit_Click` | `:5941-5943` | three-line admission preamble |
| `btnNoSpread_Click` | `:5965-5967` | " |
| `btnMarket_Click` | `:6001-6003` | " |
| `btnReduceLimit_Click` | `:6024-6026` | " |
| `btnReduceMarket_Click` | `:6098-6100` | " (its existing "only the BUTTON is latched" comment extended to name the debounce) |
| `btnTrail_Click` | `:6294-6296` | " |

All line numbers in this report are **post-commit** (`550d673`). The declaration block shifted
everything below `:2055` down by 22 lines, so sites the spec and the v1 review cite by their
pre-commit numbers appear here 22 higher.

The preamble is §3 verbatim, identical in all six:

```vb
If (DateTime.UtcNow - lastPlacementAdmittedUtc).TotalMilliseconds < PlacementDebounceMs Then Return   ' duplicate actuation
If Interlocked.Exchange(isPlacingOrder, 1) = 1 Then Return   ' a placement is already in flight
lastPlacementAdmittedUtc = DateTime.UtcNow
Try
```

No other file was touched. `ExecuteOrderAsync`, `SendReduceMarketOrderAsync`, `SendReduceOrderAsync`,
`StopLossForTrailingOrderAsync`, `PlaceAutomatedOrder`, `btnBuy`/`btnSell` and `btnCancelAllOpen` are
unchanged, per §4.

## 2. §4 — the emergency exclusion, verified by inspection

This is the load-bearing one, so it is verified positively rather than by "I didn't touch it".

`SendReduceMarketOrderAsync` has exactly four callers in the codebase:

| Caller | Line | Debounced? |
|---|---|---|
| `FlattenPositionAsync` | `:793` | **NO** — calls the function directly |
| emergency site 1, `UpdateStopLossForTriggeredStopLossOrder` | `:4396` (was `:4374`) | **NO** — direct |
| emergency site 2, same method | `:4406` (was `:4384`) | **NO** — direct |
| `btnReduceMarket_Click` | `:6102` | yes — the debounce is in the *handler*, above this call |

The three non-button callers reach the function without passing through any handler, so no debounce
check exists on any path reachable from `FlattenPositionAsync` or either emergency site. This is not
an accident of call order — it is structural: the check is a statement inside the handler body, and
those three callers never enter that body.

The route that *would* have re-coupled them — `btnReduceMarket.PerformClick()` — does not exist.
A repo-wide grep for `PerformClick` over all `*.vb` returns four hits, **all of them comments**
(`:4396`, `:4406`, `:5833`, `:6060`), three of which are tombstones recording the earlier
cross-thread fix that removed exactly this coupling. `Application.DoEvents` returns zero hits, so no
nested pump can re-enter a handler mid-await either.

Consequence, stated plainly: an emergency reduce arriving 1 ms after an admitted manual placement is
**not** swallowed. The `emergencyFired` census is unchanged at 10.

## 3. §3 — stamp on admission only

Three claims, each checked against the emitted code rather than the intent:

- **A rejected-by-debounce actuation cannot stamp.** Statement 1 `Return`s. Statements 2 and 3 are
  unreachable on that path. So a stuck or repeating button pinned at, say, 30 Hz produces exactly one
  admission per 500 ms window forever — the lockout is bounded at one window and cannot be extended
  by the input persisting. This was the specific failure mode §3 warned about, and it is closed.
- **A rejected-by-latch actuation cannot stamp.** Statement 2 `Return`s *before* statement 3. §3's
  "the stamp is set AFTER the latch take, so a losing actuation cannot move it" holds by
  construction, not by convention.
- **The first placement of a session is always admitted.** The field initialises to
  `DateTime.MinValue`, so the first evaluation computes ≈ 6.4 × 10¹⁴ ms, which is not `< 500`. A
  sentinel that accidentally blocked the session's first placement would have been a shipping defect;
  it does not.

## 4. The latch is still leak-free — the v1 proof, re-discharged

v1's review verified that the acquire sits outside the `Try` **with zero statements between**. That
is no longer literally true: statement 3 now sits there. The obligation transfers, so it is
discharged rather than assumed — if that statement could throw, the latch would be taken and never
released, and placement would be dead for the rest of the session. That is a worse outcome than the
defect being fixed, so it gets a real proof:

`lastPlacementAdmittedUtc = DateTime.UtcNow` cannot throw. `DateTime.UtcNow` is a static clock read
with no documented exception and no failure path. The assignment is a store of a value type into an
instance field of `Me`, which is non-null by construction inside an instance method; a field store
cannot raise. There is no conversion, no boxing, no allocation, and no user code in the statement.

Statement 1, which sits *before* the take, cannot leak the latch under any circumstance — nothing is
held yet — but for completeness it cannot throw either: `DateTime` subtraction is an unchecked tick
subtraction whose operands here are bounded by `DateTime.MaxValue.Ticks`, well inside `TimeSpan`'s
range, and `.TotalMilliseconds` is a double division.

The rest of v1's proof is untouched and still holds: every take is paired with a `Finally` release,
takes and releases are both exactly 6, and `btnReduceLimit`'s two early `Return`s are inside the
`Try` so they reach the `Finally`.

## 5. Threading

All six handlers are reachable only through their `Handles` clauses, i.e. from the message pump on
the UI thread. With `PerformClick` absent (§2) there is no second entry point. The stamp is therefore
read and written on one thread only, and needs no interlocking — which is why it is a plain
`DateTime` field and not, say, a `Long` behind `Interlocked`. Even in a hypothetical cross-thread
entry, a torn 64-bit read on x86 could only mis-admit or mis-reject a single actuation, with the
latch still behind it.

The file's existing idiom is the same shape — `lastParseWarn` at `:2080` does
`(DateTime.UtcNow - field).TotalSeconds >= 5` — so this reads as local code, not as an import.

## 6. Censuses (§5.6) — re-run, scoped to `frmMainPageV2.vb`

| Identifier | Expected | Actual |
|---|---|---|
| `isPlacingOrder` | 13 | **13** (1 decl + 6 takes + 6 releases, unchanged from v1) |
| `lastPlacementAdmittedUtc` | 13 | **13** (1 decl + 6 reads + 6 stamps) |
| `emergencyFired` | 10 | **10** |
| `IsATRSlippageExcessive` | 8 | **8** |
| `NextSlBackoff` | 2 | **2** |
| `RecordCommandedSLPrice` | 3 | **3** |
| `slUpdateFailures = 0` | 1 | **1** |

`PlacementDebounceMs` is 7 (1 decl + 6 reads) — not specced, recorded so a future census has a
baseline. The new comment block was written to avoid naming any censused identifier, so the counts
are code sites only, per the standing "prose breaks tripwire greps" lesson.

Scope matters: these are counts within the hot file. A repo-wide grep inflates `emergencyFired`,
`IsATRSlippageExcessive` and `NextSlBackoff` for the reasons already recorded in the era checkpoint,
and now also inflates the two new names via this report and the spec.

## 7. What is NOT verified here

**Acceptance 2 — the reproduction — has not been run.** It requires placing orders, which this seat
does not do. §5.2, §5.3 and §5.4 are all runtime and all belong to the owner or the reviewing seat:

```bash
powershell -NoProfile -File tools/place-and-verify.ps1 "btnMarket" "Market buy order placed" -Actuations 3 -ExpectCount 1
```

Expected: `Effect verified: ... x1`, and the flatten that follows reporting a reduce of **10**, not 30.

Per §6 and the v1 lesson, I am deliberately **not** offering a static argument that this works. v1 was
a correct implementation of a wrong theory and read as convincing right up until the runtime pass
contradicted it. What can be said statically is only this: the guard is time-based, so it does not
depend on the actuations overlapping — which is the specific assumption that killed v1. Whether the
window is positioned and sized correctly against a real burst is an empirical question.

Two things for whoever runs it:

- **Rebuild x64 before testing.** The gate builds AnyCPU only; the harness bin is not the owner's bin.
  The rebuild clobbers the bin's testnet `secrets.json`, so re-read the window title for `— TESTNET`
  before any placement.
- **Check the change is in force.** The v1 review checked the running assembly for `isPlacingOrder`;
  the equivalent here is `lastPlacementAdmittedUtc`. Both should now be present.

## 8. Residuals, for the reviewer

1. **`DateTime.UtcNow` is not monotonic.** A backward system-clock step between an admission and the
   next actuation makes the elapsed value negative, which is `< 500`, so placement is refused until
   the clock catches back up to the stamp. Judged acceptable and implemented as specced: NTP slews
   rather than steps under normal operation; the file's every other stamp uses the same clock; and
   the failure direction is safe — it can block an *entry*, never an emergency exit, because §4 keeps
   the emergency path outside the debounce entirely. `Stopwatch.GetTimestamp()` would remove it if the
   owner ever wants that, but it is a change to a ruled mechanism and would need a ruling.
2. **`btnReduceLimit`'s no-op paths stamp the clock.** Its two early `Return`s ("No open position to
   reduce", "Invalid price") are inside the `Try`, hence after the stamp, so a reduce click that
   places nothing still consumes one window. This follows from §3's rule as written — admission means
   winning the latch, not reaching the wire — and it is bounded at one window and errs toward
   blocking rather than placing. Flagged rather than fixed, because moving the stamp past those
   returns would be a semantic change to a ticked spec and would have to be ruled, not assumed.
3. **A physical double-click is still unobserved.** Carried unchanged from the v1 review §6 — the
   mechanism is actuation-source-agnostic, but the burst instrument is UIA-driven. An owner-mouse
   double-click on `btnMarket` would settle it in ten seconds, and is now much more interesting than
   it was under v1, since 500 ms was chosen precisely as the double-click threshold.
4. **Silent rejection.** Per §3 a rejected actuation logs nothing, matching v1. If the owner wants
   visibility, a gray `Duplicate placement ignored (debounce)` line is one call to
   `AppendColoredText` per handler — §3 says raise it at review, so it is raised.
