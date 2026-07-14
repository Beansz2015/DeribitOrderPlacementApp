# Coordinator review — signal-bridge tie-in (2026-07-15) — ALL 5 CODE COMMITS APPROVED

**Scope:** `030788b` (TimedOut hardening), `ebde3aa` (SignalBridge + glue), `2dcb84e` (panel), `5e5acdf` (FrmIndicators R1 + repoint), `7be69b8` (debounce shutdown guard) — HEAD `9a51b5f` on `origin/master = afca0c2`, tree clean. **Method:** every diff read via `git show`; `SignalBridge.vb` read END-TO-END (824 lines); build re-run at HEAD **0/0**; the report's greps re-run independently; contract §4 chain verified clause-by-clause against the frozen contract; adversarial threading pass.

## Verdicts

- **`030788b` APPROVED** — spec §2 verbatim: timeout path flags instead of removing; late-response block sits directly after the successful `TryRemove`, log-only, no rollback, no TCS completion; sweep untouched.
- **`ebde3aa` + `7be69b8` APPROVED** — the gate chain is **contract-exact** (4.1→4.6 in order; informational fields appear in no gate — re-grepped; WEAK dies at `tier`; only ws `DOWN` blocks; interlock live-only; cancel-pending correctly delegated to `PlaceAutomatedOrder` and surfaced as `rejected:`). **manualSL sign math independently verified against the host's own derivation** (LONG: trigger = manualSL + offset ⇒ manualSL = stop − offset; SHORT mirror) — the exchange trigger lands exactly on the engine's stop. Threading sound: single-flight + rerun-flag logic correct including the tail-race re-entry; no event raised under `_sync`; no lock held across an `Await`; `LastSignalAtr` is a plain field read (accepted torn-read class, documented). Config/state/disposition files follow the secrets.json convention; `.gitignore` + conditional-copy vbproj correct. The amendment's `ObjectDisposedException` guard is the right minimal fix for a process-fatal threadpool race.
- **`2dcb84e` APPROVED** — interlock enforcement stays bridge-side; the panel marshals `StatusChanged` via handle-guarded `BeginInvoke` with a teardown Try/Catch; `_suppressBridgeUi` prevents setter loopback; handler unhooked in `FormClosed`; nothing persists (Load defaults + explicit anti-persistence comment); `WFO1000` attributes on `Bridge` correct.
- **`5e5acdf` APPROVED** — trigger block removed (R1), helpers left unreachable (no churn in the dying file); `btnAutoTrade` inert pointer; `AttachBridgeToSettings` ordering safe (`_autoTradeSettings` created in Load, attach called in Shown); **all FIVE call sites repointed** (`_indicators.IsAutoTradingEnabled` → 0 hits), `And`-composition preserved at the two And-form sites (behavior-preserving; And→AndAlso stays housekeeping's).

**Invariant sweep:** the full-stack `frmMainPageV2.vb` hunk map contains NOTHING inside `HandleQuoteUpdates`, the open-`StopLossOrder` echo classification, `UpdateStopLossForTriggeredStopLossOrder`, or the 7 SL-context reset sites. Zero SL edits added (grep `private/edit` in `SignalBridge.vb` → 0) ⇒ no `RecordCommandedSLPrice` obligation. Zero control touches from bridge threads (grep → comments only). Orders flow only through `PlaceAutomatedOrder` (maker/reduce discipline stays inside it). `ApplyCloseFill`/`btnClose`/`btnMark`: untouched/absent.

## Findings

**F-1 — payload validation guard (SHOULD FIX before the §6 tests; MUST before soak-start freezes the token set).** Report deviation #9 claims "missing ids → yellow log, no disposition" — but `ParsePayload` *defaults* missing fields (`InstanceId=""`, `SignalId=-1`, levels `0`) instead of rejecting. Consequences: (a) an id-less payload flows into the chain and can emit an un-joinable junk disposition row; (b) worse, an actionable payload with missing/zero `levels` would reach placement with `manualTP:=0` → the host silently falls back to **offset-derived** levels — a partial-apply, which the agreed failure semantics forbid ("reject + log, never partial-apply") and which violates R2 (engine levels as-is). Unreachable from the fixture-pinned emitter; cheap to close:
1. After `ParsePayload`: `If p.InstanceId.Length = 0 OrElse p.SignalId < 0 Then` yellow log + `Return` (no disposition — can't join without identity; matches the report's stated intent).
2. In gate 4.4, after the `direction` clause: `ElseIf p.StopLevel <= 0D OrElse p.Target <= 0D Then disposition = "refused: levels"` — **new token `refused: levels`, added to the §d set now, pre-soak**.

**F-2 — nit, optional:** `PersistState` uses plain `WriteAllText` (not temp+move); a crash mid-write could tear `bridge-state.json` → watermark lost on restart. Already triple-mitigated (mode resets to Off, freshness gate, not-flat gate); fix only if touching the file anyway.

## Judgment calls — all six ACCEPTED

1. Contract-order operational gates over the spec parenthetical — the contract is canonical; per-gate `refused:` tokens are strictly better dispositions.
2. Cancel-pending downstream in the API — the gate exists in the composite chain; no host-API widening. Correct restraint.
3. Window = allowed-INSIDE — contract §4.6 wording followed. (Owner awareness below.)
4. De-dupe `≤` watermark, same-instance only — sound under the contract's monotonic-per-instance guarantee; a new `instance_id` never matches the stored pair, so engine restarts are unaffected.
5. Log-only advances de-dupe + cooloff — correct for soak fidelity (`refused: cooloff` rows appear exactly as live would produce them). Consequence: a log-only→live flip never re-acts an already-would-acted signal.
6. Breaker unlatched, re-evaluated per payload + force-STOP — matches the old behavior, and re-START during a breach dies at the next payload's gate.

## Owner-awareness items (no code change; know these going in)

1. **Session window semantics INVERTED vs the old FrmIndicators check:** the config now names the ALLOWED window (entries only inside `window_start`–`window_end` UTC+8; blank = unrestricted). The old check was an exclusion range. If your mental model is "block these hours", configure the complement.
2. **Log-only → live flip:** a signal that already logged `would-act` will not fire after the flip — the first live entry waits for the next fresh signal. Deliberate (no surprise entry on mode flip).
3. **`chkMaxSlippageATR` is a START precondition only** — unchecking it mid-run disables the slippage guard without auto-STOPping the bridge (same trust level as editing the multiplier live). Flag if you want it to disarm.
4. During the log-only soak, `LogTradeDecision` gating (`IsLiveStarted`) is False — the autotrade DB log stays quiet until live mode. The soak's record is the disposition log, by design.

## Next

F-1 amendment (one small bridge-only commit) → owner §6 mock-payload tests 1–5 → push → owner flips `signal_bridge.enabled` engine-side → log-only soak (ergonomics Phase A pipelines during it, per plan).
