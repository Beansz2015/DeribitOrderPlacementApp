# Spec — Signal-bridge tie-in (consumer, interlock, AutoTradeSettings re-code)

**Date:** 2026-07-03
**Contract:** `docs/integration-contract-verdictengine.md` — **FROZEN v1, accepted by both coordinators.** This spec implements the consumer side. Read the contract first; where this spec and the contract disagree, the contract wins and the deviation gets reported.
**Also read:** `docs/spec-decouple-v2.md` (the API this consumer calls) + `docs/HANDOVER-2.md` §4 (invariants).
**Recommended implementer:** Opus at **high** (or Fable at high if in-window). New subsystem + threading + a dying-module touch.
**Pre-req:** decouple-v2 + resilience + restore-hardening + close-completion + SL-reconciliation all landed (base ≥ `968b26d`; see `spec-back-session-2026-07-04.md` for the ~617-line delta in `frmMainPageV2.vb`). **Locate strictly by symbol — line anchors from before 2026-07-04 are stale.**
**Coordination (from the session spec-back §10):** this spec does NOT touch the triggered-SL chase/echo regions — keep it that way. If any future revision adds a programmatic SL edit, it MUST call `RecordCommandedSLPrice` or its echo will be misread as a manual edit. `ApplyCloseFill` is fields-only now (no ByRef). `btnClose`/`btnMark` no longer exist — don't reference them. All UI marshals must be handle-guarded (`UiInvoke`/guarded `AppendColoredText`).
**Ground rules:** standing — build 0/0 per commit, local commits, never push, scope discipline, impl report. **New files start `Option Strict On` / `Option Explicit On`.**

---

**STATUS 2026-07-06 — A2 GO-AHEAD (engine coordinator).** The engine emitter is implemented, coordinator-reviewed, live-smoke-tested and pushed (engine repo `23fd8b9`), fixture-pinned to schema v1; emission ships OFF (`signal_bridge.enabled: false`) until the owner flips it for the log-only soak. The go-ahead was cross-checked against the frozen contract — **no v1 changes**; emitter implementation notes are now recorded in contract §3 (indented JSON; `settings_version` already at 50 and drifting — never pin; `kelly` zeros never null on no-edge runs; `SKIPPED` always carries `ledger_mismatch:false` — never read health semantics from skips; `health.ws` precedence, only `DOWN` blocks; `signal_id` gaps legal — never infer missed signals from gaps). Implementer consequences:
- **Disposition-log join (soak requirement):** the §3a log line already carries `instance_id | signal_id` — the engine's CSV (v0.8+) logs matching `InstanceId`/`SignalId` columns and the soak reviewers join row-for-row. Keep the §3a format and disposition tokens **stable once the soak starts**. This side's tokens beyond the contract-§4 set: log-only's `would-act: …` and the API-level `rejected: <reason>` — list them in the impl report so the soak reviewers know the full set.
- **Testing:** §6 mock-file path first (the contract §3 example is byte-representative). When live payloads are wanted for the soak, the owner flips `signal_bridge.enabled` on the engine's bin copy — the engine is live and collecting, so real per-run files arrive immediately, including genuine `SKIPPED`; overnight engine power-downs exercise the staleness stand-down for free.
- **Rollout change (contract §7 addendum 2026-07-06):** the step to live-at-minimum-size now ALSO waits for the owner's confirmation that the engine-side placed-geometry (structural-first) pass is live. Log-only does not wait. `cap_reason` may gain new labels — informational, never gate.

---

## 1. Shape

A new engine-side-of-this-app class, **`SignalBridge.vb`** (Option Strict On), owned by `frmMainPageV2`, consuming `verdict_signal.json` per the contract and driving the decouple-v2 public API. `AutoTradeSettings` gains a **SIGNAL BRIDGE panel** (ARM toggle, START/STOP, mode, status, gate config). `FrmIndicators`' own autotrade trigger path is **neutralized** (R1: the engine is the sole signal source) but the form otherwise keeps running until post-soak retirement.

**Design decisions (owner may veto):**
- **Live mode requires `chkMaxSlippageATR` checked** (mult default 0.6) as a START precondition — the contract's slippage-cap commitment rides the existing guard machinery instead of new plumbing. START refuses with a clear log if unchecked.
- Transition scaffolding: `AutoTradeSettings` stays owned/positioned by `FrmIndicators` for now; `frmMainPageV2` constructs the bridge and hands the reference to the settings form (`Friend Property Bridge`). Full ownership moves at retirement (separate post-soak spec).
- ATR source for the slippage guard becomes **bridge-first**: last actionable payload's `atr` when fresh, falling back to `_indicators.CurrentATR` while FrmIndicators lives, then the $70 constant. (Contract: payload `atr` is the sole source post-retirement.)
- Sizing v1: fixed `size_usd` from bridge config (`kelly` ignored per contract).

## 2. Commit 1 — `TimedOut` ack hardening (decouple-v2 review addendum)

**Problem:** `PlaceAutomatedOrder`'s timeout path removes the registry entry, so a >5s-late rejection is fully silent (the generic logger skips the placement id range) and no rollback occurs.

**Change:** add `Public TimedOut As Boolean` to `PendingPlacement`. In `PlaceAutomatedOrder`, replace the timeout-path `TryRemove` with:

```vb
        Dim lateEntry As PendingPlacement = Nothing
        If pendingPlacements.TryGetValue(reqId, lateEntry) Then lateEntry.TimedOut = True
        Return New PlacementResult With {.Accepted = False, .Reason = "timeout"}
```

In `HandlePlacementResponse`, directly after the successful `TryRemove`:

```vb
            If entry.TimedOut Then
                ' Late response after the ack timed out: LOG ONLY. No rollback (a newer placement's
                ' state may be live - restoring old snapshots could clobber it), no TCS completion.
                Dim lateErr = json.SelectToken("error")
                AppendColoredText(txtLogs,
                    $"LATE placement response (id {messageId.Value}, after ack timeout): " &
                    If(lateErr IsNot Nothing, $"REJECTED code {lateErr.SelectToken("code")} - {lateErr.SelectToken("message")}", "accepted"),
                    Color.Orange)
                Return
            End If
```

The 60-s sweep still garbage-collects never-answered entries. **Acceptance:** build green; normal ack/rejection behavior unchanged (test 2 of decouple-v2 §8 still passes).

## 3. Commit 2 — `SignalBridge.vb` + host glue

### 3a. Config + state files (beside the exe, both git-ignored; ship `.example` copies)

- `bridge.json`: `{ "path": "C:\\Dev\\DeribitBridge\\verdict_signal.json", "tiers": ["HIGH","MEDIUM"], "size_usd": 10, "cooloff_min": 5, "circuit_breaker_usd": 50, "window_start": "", "window_end": "", "slippage_atr_mult": 0.6 }` — loaded at construction; missing file ⇒ defaults + yellow log.
- `bridge-state.json`: `{ "instance_id": "...", "last_acted_signal_id": N }` — the persisted de-dupe pair, written after every acted signal, read at construction.
- `bridge-dispositions.log` (append-only): `utc | instance_id | signal_id | verdict | confidence | direction | disposition` — one line per consumed payload (contract §4 commitment; v2 feedback-file precursor).

### 3b. The class (Option Strict On; constructor `New(host As frmMainPageV2, log As Action(Of String, Color))`)

- **Watcher:** `FileSystemWatcher` on the payload path (+ create-if-missing directory watch), ~150 ms debounce (`System.Threading.Timer` reset per event), one retry on `IOException` share-violation. **Independent 10-s staleness timer** always running while mode ≠ Off.
- **Single-flight processing:** `SemaphoreSlim(1,1)` (or `Interlocked` flag) — one payload evaluated at a time; a newer file event supersedes a queued one (process latest only).
- **Parse + gate chain, exactly contract §4 order:** schema_version → freshness/`SKIPPED` → de-dupe (`instance_id`,`signal_id`) → action mapping (state OK ∧ direction ≠ NONE ∧ confidence ∈ tiers ∧ Not mtf_blocked ∧ context ≠ "BELOW_MIN_MOVE" ∧ Not ledger_mismatch ∧ ws ≠ "DOWN" — **REST passes**) → interlock (§4 below) → operational gates (cooloff since `lastActionUtc`; circuit breaker `host.SessionPnLUSD ≤ -circuit_breaker_usd` ⇒ trip + force-STOP; UTC+8 window, blank = unrestricted, spans-midnight semantics like the old FrmIndicators check; then `host.IsFlat`/`Not host.HasWorkingEntryOrder` for a precise disposition before the API's own defense-in-depth). First failing gate = the disposition string (`refused: <gate>`); every payload gets exactly one disposition line (log + file). **Never gate on informational fields** (contract §3 list).
- **Placement (mode = Live, all gates green):**
  ```
  manualSL = If(direction = LONG, stop - host.StopLimitOffset, stop + host.StopLimitOffset)
  host.SetTradeTargets(manualTP := target, manualSL := manualSL, sizeUSD := size_usd)
  result = Await host.PlaceAutomatedOrder(direction, "limit")
  ```
  disposition `acted (id …)` or `rejected: <reason>`; on acted → persist de-dupe pair + `lastActionUtc = now`. In **log-only** mode: run the entire chain, log `would-act: LONG @ entry, stop S, target T, size N` instead of placing (this is the soak's whole point).
- **WEAK note (contract):** `WEAK LONG` arrives `direction=LONG, confidence=LOW` — the tier gate refuses it; never infer from direction. Implement the tier check against the pinned enum strings only.
- **Staleness/`SKIPPED` handling:** mark state STALE, **force Started = False** (§4), alert after 3 consecutive stale checks; a fresh payload clears the counter but does NOT re-start.
- **Public surface for the UI + host:** `Mode` (Off/LogOnly/Live), `LocalArmed`, `Started`, `EngineArmed` (from latest payload), `IsFreshNow`, `LastDisposition`, `LastSignalAtr As Decimal` (0 when none/stale), `TryStart() As String` (Nothing on success, else the refusal reason), `Stop()`, events/callbacks for status change (UI binds).

### 3c. Host glue (`frmMainPageV2`)

- Construct after `Shown` init: `signalBridge = New SignalBridge(Me, AddressOf BridgeLog)` where `Private Sub BridgeLog(msg As String, c As Color)` wraps `AppendColoredText(txtLogs, "[BRIDGE] " & msg, c)`.
- New tiny API members: `Public ReadOnly Property StopLimitOffset As Decimal` (returns `stopLossOffset`) and — for the ATR repoint — modify `CalculateATRSlippageLimit`:
  ```vb
  Dim bridgeAtr As Decimal = If(signalBridge IsNot Nothing, signalBridge.LastSignalAtr, 0D)
  Dim currentATR As Decimal = If(bridgeAtr > 0D, bridgeAtr, If(_indicators IsNot Nothing, _indicators.CurrentATR, 0D))
  ```
  (rest of the function unchanged; the $70 fallback stays). This runs on the receive thread — `LastSignalAtr` must be a simple field-backed property, no locking, no controls.
- Hand the bridge to the settings form: after `_indicators` creates it (transition scaffolding): `_indicators.AttachBridgeToSettings(signalBridge)` → FrmIndicators passes it to `_autoTradeSettings.Bridge`.

## 4. Commit 3 — AutoTradeSettings SIGNAL BRIDGE panel

New GroupBox (existing controls stay — FrmIndicators still reads them until retirement): **ARM AUTOTRADE** checkbox (default unchecked, never persisted), **START/STOP** button, **mode selector** (Off / Log-only / Live; default Off), status labels (engine armed?, freshness, last signal + disposition, mode/started state), and editable gate config bound to `bridge.json` (tiers, size, cooloff, breaker, window) with a Save button (reload into the bridge on save).

**Interlock enforcement (contract §6, trader-fixed):**
- START (`TryStart`) succeeds only when: mode = Live ∧ LocalArmed ∧ latest payload fresh ∧ `EngineArmed` ∧ `chkMaxSlippageATR` checked (design decision §1). Refusal reason shown + logged.
- **START is not sticky:** any of — ARM unchecked, engine armed goes false in a payload, staleness/`SKIPPED`, circuit-breaker trip, mode change — forces `Started = False` with a red log line naming the cause. Resuming = full sequence again.
- Nothing persists: mode/ARM/Started all reset to Off/unchecked/stopped at every app start (do NOT put them in item-A ergonomics persistence later — note in code).
- Log-only mode ignores ARM/START (places nothing; soak-safe).

## 5. Commit 4 — neutralize FrmIndicators' autotrade trigger (R1)

In `FrmIndicators.UpdateSignals`, remove the auto-trading integration block (the `If enableAutoTrading AndAlso CanPlaceAutomatedOrder() Then ProcessAutomatedSignal(...)` block); repurpose `btnAutoTrade` to a disabled state with text "AUTO: see Bridge" (or hide it — implementer's choice, report it). Leave `ProcessAutomatedSignal`/`ExecuteAutomatedTrade`/`CanPlaceAutomatedOrder` in place but unreachable (they die with the form post-soak; don't churn the dying file). `CurrentATR`, the ATR display, `btnATR` paste, and the settings-form ownership are untouched. **Add `AttachBridgeToSettings(bridge)`** (one-liner pass-through to `_autoTradeSettings.Bridge`).

`IsAutoTradingEnabled` (read by `HandleOrderPositionUpdates` for `LogTradeDecision` gating): repoint the property to return the **bridge's** live-and-started state via the host — simplest: `frmMainPageV2` exposes `Friend ReadOnly Property BridgeLiveStarted As Boolean` and FrmIndicators' property returns... **No — cleaner:** change the three `_indicators.IsAutoTradingEnabled` call sites in `frmMainPageV2` to `signalBridge IsNot Nothing AndAlso signalBridge.IsLiveStarted` and leave FrmIndicators' property unused. Report which was done.

## 6. Test plan (fully runnable WITHOUT the engine — hand-crafted payload files)

**Implementer:** build 0/0 ×4; Option Strict On on new files; greps — gate order matches contract §4; no controls touched from bridge threads (all UI via the settings form's own `Invoke` and the host log delegate); `LastSignalAtr` read path allocation-free.

**Owner (test sub-account; craft payloads by editing a JSON file and copying it over the path — File.Replace semantics matter only for the engine side):**
1. **Log-only soak mechanics:** drop a fresh OK/HIGH/LONG payload (armed true) → `would-act` disposition with correct levels/size; WEAK-LOW → `refused: tier`; SKIPPED → stand-down; stale timestamp → `stale` + alert after 3 checks; `schema_version: 2` → alert, no action; same (instance, id) twice → `duplicate`; armed false → `refused: interlock`.
2. **Interlock ladder (mode = Live):** START with ARM off → refusal; ARM on + engine armed false → refusal; full sequence → Started; then flip the payload to armed false → auto-STOP with named cause; re-arm requires full sequence. Restart app → everything off.
3. **Live placement (min size):** fresh actionable payload, full interlock → order places via the API with `manualTP = target` and SL **trigger** = engine stop exactly (verify on exchange UI); disposition `acted`; de-dupe file updated; cooloff refuses the next payload for the configured minutes.
4. **ATR repoint:** with a fresh payload, the slippage-limit log line reflects payload `atr` × mult (not FrmIndicators' ATR).
5. **Regression:** manual trading unaffected in every mode; FrmIndicators still displays signals but its AUTO button is inert.

## 7. Rollout reminder (contract §7)

Off → **log-only for a few sessions** → live at minimum size → normal. The engine side ships behind `signal_bridge.enabled: false` — coordinate the first joint session through the owner. **2026-07-06:** the live-at-minimum-size step additionally waits for the owner's confirmation that the engine's placed-geometry (structural-first) pass is live (contract §7 addendum); log-only does not wait.

## 8. Implementation report

`docs/impl-report-autotrade-tiein.md`, standard format + explicitly: the §5 `IsAutoTradingEnabled` choice, any contract-vs-spec friction found (contract wins), and the §1 design-decision confirmations.
