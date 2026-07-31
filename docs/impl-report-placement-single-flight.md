# Impl report — single-flight guard on the placement handlers

**Spec:** `docs/spec-placement-single-flight.md` (owner-ticked 2026-08-01, NEXT ahead of N2).
**Origin:** `docs/investigation-triple-placement-2026-08-01.md` §3/§4.
**Seat:** Opus HIGH implementer, fresh conversation, own pass (order-placement path).
**Commits:** `569a834` (the latch) · `1773c94` (dead-code deletion) · this report.
**Gate:** executed per commit and at final HEAD — **GATE PASSED, OrderCheck 153/153** every time.
**No trades were placed and the bridge was never armed** (implementer safety boundary). Acceptance
2/3/4 are runtime and are handed to the owner below.

## 1. What changed

| Site | Line (at this HEAD) | Change |
|---|---|---|
| `isPlacingOrder` declaration | `frmMainPageV2.vb:2055` | new field, placed with `isRepositioning` / `isSLRepositioning` |
| `btnLimit_Click` | `:5919` take · `:5934` release | acquire before `Try`, `Finally` release |
| `btnNoSpread_Click` | `:5941` · `:5956` | same |
| `btnMarket_Click` | `:5975` · `:5990` | same |
| `btnReduceLimit_Click` | `:5996` · `:6021` | same |
| `btnReduceMarket_Click` | `:6068` · `:6074` | same |
| `btnTrail_Click` | `:6262` · `:6277` | same |
| `ButtonDisabler` / `ButtonEnabler` | was `:5085` / `:5097` | **deleted** (see §4) |

Shape, identical in all six — exactly the spec §2 template, no variation:

```vb
Private Async Sub btnX_Click(sender As Object, e As EventArgs) Handles btnX.Click
    If Interlocked.Exchange(isPlacingOrder, 1) = 1 Then Return   ' a placement is already in flight
    Try
        ... existing body, byte-identical ...
    Catch ex As Exception
        ... existing catch, byte-identical ...
    Finally
        Interlocked.Exchange(isPlacingOrder, 0)
    End Try
End Sub
```

No existing statement was moved, reordered or reworded. The diff is 6 acquire lines, 6 `Finally`
blocks, one field with its comment, one deletion.

## 2. The `Finally` proof (spec §5 — the load-bearing part)

The question is not whether the latch blocks a second actuation; it is whether the latch can be left
set, because a wedged latch silently disables **all** placement for the rest of the session. Every
exit path of all six handlers, enumerated:

**a. The losing actuation never reaches the `Finally`.** The acquire is *outside* the `Try`. A caller
that loses the race executes `Return` before the `Try` is entered, so it cannot run the release and
cannot clear a latch it does not own. This is the double-release hazard, and the placement of the
acquire line is what excludes it. (Had the acquire been the first statement *inside* the `Try`, every
dropped duplicate would have released the winner's latch — reinstating the defect with extra steps.)

**b. Nothing can throw between the acquire and the `Try`.** There are zero statements between them in
all six handlers. `Interlocked.Exchange` on an `Integer` field cannot throw; if it somehow did, the
write did not happen either, so the latch is not taken.

**c. Normal completion** → falls out of the `Try` → `Finally`. ✔

**d. Early `Return` inside the `Try`.** Only `btnReduceLimit_Click` has any: `posSize = 0D` ("No open
position to reduce.") at `:6004` and `price <= 0` ("Invalid price.") at `:6013`. A `Return` out of a
VB `Try` executes the `Finally` first — including inside an `Async Sub`, where the compiler preserves
the `Try/Finally` in the generated `MoveNext` and the leave triggers it. Both were exercised by
inspection against the state machine, not assumed. ✔ No `Exit Sub`, no `GoTo`, and no other early
return exists in any of the six bodies.

**e. Exception in the `Try` body** → the pre-existing `Catch ex As Exception` → `Finally`. ✔ In
practice the body rarely throws at all: `ExecuteOrderAsync` (`:3512`), `SendReduceOrderAsync`
(`:4055`), `StopLossForTrailingOrderAsync` and `SendWebSocketMessageAsync` (`:1132`) each wrap their
whole body in their own `Try/Catch` and swallow — the handler `Catch` arms are backstops. The latch
is released either way.

**f. Exception thrown inside the `Catch` arm** (i.e. `AppendColoredText` itself failing) → `Finally`
still runs, then the exception escapes the `Async Sub`. The latch is released before that happens.
This is a theoretical path only: `AppendColoredText` (`:5029`) marshals inside its own `Try/Catch`
and returns early when the handle is gone (`:5051`); its only unguarded statements are string
comparisons on a non-null interpolated string.

**g. The awaited task never completes.** The single genuine hang vector: every one of the six ends in
`webSocketClient.SendAsync(..., cancellationTokenSource.Token)` (`:1137`). A stuck send would hold
the latch. It self-heals: `HandleWebSocketDisconnect` cancels that token (`:1233`), which faults the
send, which surfaces as `OperationCanceledException` in `SendWebSocketMessageAsync`'s own catch
(`:1144`), the awaited `Task` completes, and the `Finally` runs. `FormClosing` cancels it too
(`:5778`). There is no unbounded `Task.Delay`, no `Task.WhenAny` ack-wait and no response round-trip
on any of the six paths — the latch is held for the duration of one WebSocket **send**, sub-millisecond
in the normal case. (The ack-awaiting `Task.WhenAny` at `:784` is `PlaceAutomatedOrder`, which does
not take this latch.)

**h. The continuation is never posted** because the UI message loop has ended (form closing during
the await). The `Finally` does not run — and cannot matter: the session is over. No other state is
left inconsistent, because the latch is the only thing the `Finally` touches.

**i. Re-entrancy during the synchronous prefix.** Four handlers call `btnEstimateMargins_Click(Nothing,
Nothing)` synchronously before the first `Await`. If that ever pumped messages, a queued click would
re-enter — and hit case (a), returning without touching the latch. There is no `Application.DoEvents`
anywhere in the codebase (verified), so this is closed on both sides.

**Conclusion: the latch cannot leak on any reachable path.** The only non-release cases are process
teardown (h) and a wedged socket that the existing reconnect path already cancels (g).

## 3. What the latch deliberately does NOT cover

- **The bridge act path** (`PlaceAutomatedOrder`, `:775`) — not user-actuated, already serialised,
  spec §3. It calls `ExecuteOrderAsync` directly, never a handler.
- **The emergency / flatten reduce.** The two emergency sites in
  `UpdateStopLossForTriggeredStopLossOrder` (`:4374`, `:4384`) and `FlattenPositionAsync` (`:793`)
  call `SendReduceMarketOrderAsync()` **directly** — the cross-thread fix that replaced
  `btnReduceMarket.PerformClick()`. They therefore bypass the latch entirely, which is the outcome we
  want: an emergency stop must never be blocked by an in-flight manual placement. Verified by grep —
  no `PerformClick` on any of the six buttons exists anywhere in the codebase, so there is no internal
  caller whose actuation the latch could silently swallow.
- `btnBuy` / `btnSell` (direction toggles), `btnCancelAllOpen`, and `ExecuteOrderAsync` itself:
  untouched, per spec §3.

## 4. Decisions the spec left to the implementer

**`ButtonDisabler` / `ButtonEnabler`: deleted** (commit `1773c94`). Recommendation and action agree.
They had no call site since the initial import, and they were the only `.Enabled` writes on any
placement button in the codebase — which is precisely why the buttons stayed live across the await.
Spec §2 rules out wiring them up (they also flip `btnReduce*`/`btnCancelAllOpen`, a position-state
lifecycle that does not exist), and left in place they read as a ready-made fix sitting next to the
new latch — an invitation to exactly the change the spec forbids. A comment at the old site records
what was there and why it is not coming back, so the deletion does not erase the finding.

**No log line on the dropped actuation**, per spec §2 — silent `Return`. The one-line addition
(`Duplicate placement ignored`, gray) is offered for review, not added: raise it if wanted.

**`Interlocked` retained** although all six handlers are UI-thread-only, per spec §2 — uniformity
with `isRepositioning` / `isSLRepositioning` / `refreshInFlight` / `isReconnecting` is the point.

## 5. Acceptance status

| # | Item | Status |
|---|---|---|
| 1 | Gate per commit; fixtures counted | **DONE** — GATE PASSED, OrderCheck 153/153 at `569a834`, `1773c94`, and this HEAD |
| 2 | The reproduction no longer reproduces (3 `Invoke()`s in <50 ms ⇒ one entry, reduce of 10) | **OWNER-RUNTIME, PENDING** — §6 |
| 3 | Normal single placement byte-identical on all six; a second placement after the first succeeds | **OWNER-RUNTIME, PENDING** — §6 |
| 4 | Latch survives a failing placement (the leak case) | **OWNER-RUNTIME, PENDING** — §6; statically proven in §2 (e)/(g) |
| 5 | Censuses | **DONE** — below |

Censuses, scoped to `frmMainPageV2.vb` (a repo-wide grep inflates two of them):

```
isPlacingOrder           13   <- new: 1 decl + 6 takes + 6 releases
emergencyFired           10   unchanged
IsATRSlippageExcessive    8   unchanged
NextSlBackoff             2   unchanged
RecordCommandedSLPrice    3   unchanged
slUpdateFailures = 0      1   unchanged
```

## 6. Owner runtime pass — the runbook

Standing discipline first: back up `orderapp-settings.json` → **x64 rebuild** → confirm the window
title reads `— TESTNET` (the rebuild clobbers the bin's testnet `secrets.json`) → run → teardown,
restore, verify the restore by reading a box back.

**Acceptance 3 (do this first — it proves the `Finally` releases).** For each of the six buttons in
turn, using the existing asserting wrapper, twice in a row:

```bash
powershell -NoProfile -File tools/place-and-verify.ps1 "Mkt. BUY" "Market buy order placed"
```

Expected: `Effect verified: ... x1` both times. The **second** call succeeding is the acceptance —
it is the direct observation that the latch was released. Cancel/flatten between buttons as usual.

**Acceptance 4 (the leak case).** With the app disconnected (or the socket dropped), click a
placement button — expect `WebSocket is not connected.` and no order — then reconnect and place
normally. The normal placement succeeding is the acceptance.

**Acceptance 2 (the burst) — needs an owner decision first.** `multi-actuation-probe.ps1` does not
exist: the investigation's burst was ad hoc, and the spec names it `-style`. It cannot be built by
wrapping `click-PLACES-ORDER.ps1` (three process launches are seconds apart, not <50 ms), so a
committed probe would be a **second** script that Invokes trade buttons — which
`spec-ui-test-harness.md` §6.1 forbids in as many words ("only `click-PLACES-ORDER.ps1` can touch
them"). That is a harness safety-model amendment and is the owner's call, not the implementer's, so
nothing was committed. Two ways forward:

1. **Owner amends `spec-ui-test-harness.md` §6.1** to permit a second placing script under the same
   kill rule, and a harness seat lands `tools/multi-actuation-probe.ps1`; or
2. **Owner runs the burst inline**, which changes no policy — this is the investigation's own
   instrument, and it inherits the TESTNET + harness-PID gates from `Get-MainForm`:

```powershell
. tools\harness-common.ps1
$form = Get-MainForm -RequireTestnet -RequireHarnessPid
Write-Host "Environment verified: '$($form.Current.Name)' (PID $($form.Current.ProcessId))"
$btn = Get-ProcessWindows -OwnerPid $form.Current.ProcessId |
       ForEach-Object { Find-ByControlType -Element $_ -TypeName Button } |
       Where-Object { Test-ElementMatch -Element $_ -Pattern 'Mkt. BUY' } | Select-Object -First 1
$p = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
1..3 | ForEach-Object { $p.Invoke(); Write-Host "invoke $_" }
```

Then read the log: **exactly one** `Market buy order placed` line (three, pre-fix), and the flatten
that follows must report a reduce of **10**, not 30. That pair is the acceptance of record.

## 7. Residuals

- Acceptance 2/3/4 are unobserved until the owner's pass; §2 is a static proof, and static proofs are
  not runtime evidence.
- The latch is held across one WS send, so two *intentional* placements typed inside that window
  would drop the second silently. Sub-millisecond in the normal case; a genuine second placement one
  human reaction-time later is unaffected. This is the spec's chosen trade-off (§2, silent `Return`).
- A physical double-click is still unobserved (investigation §6). The mechanism is
  actuation-source-agnostic, so the burst result carries — but it is inference.
- Case (h) — the app closing mid-await — leaves the latch set for the few remaining milliseconds of
  process life. Not worth code.
