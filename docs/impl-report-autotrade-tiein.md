# Impl report — Signal-bridge tie-in (consumer, interlock, AutoTradeSettings panel)

**Spec:** `spec-autotrade-tiein.md` (contract `integration-contract-verdictengine.md` FROZEN v1 — canonical; where they disagreed, the contract won and the deviation is reported in §Friction below). **Implementer:** Fable high (in-window). **Base:** pushed `master` HEAD `afca0c2` (verified = `origin/master`, tree clean; the brief's `541f185` plus 5 docs-only commits — `git diff --stat de7d87b..afca0c2` touches `docs/` only, so the coordinator's 2026-07-13 anchor verification still held, re-verified by symbol anyway).

**Commits (local, not pushed):**

| # | Hash | What | Build |
|---|---|---|---|
| 1 | `030788b` | `TimedOut` ack hardening | 0/0 |
| 2 | `ebde3aa` | `SignalBridge.vb` + host glue + config files | 0/0 |
| 3 | `2dcb84e` | AutoTradeSettings SIGNAL BRIDGE panel | 0/0 |
| 4 | `5e5acdf` | FrmIndicators trigger neutralized (R1) + 5-site repoint | 0/0 |
| 2b | `7be69b8` | amendment to commit 2: debounce-kick guarded against shutdown disposal race | 0/0 |

All builds `dotnet build DeribitOrderPlacementApp.sln -t:Rebuild`, net9.0-windows, 0 errors / 0 warnings after every commit.

---

## Commit 1 — `TimedOut` ack hardening (`030788b`)

Exactly the spec §2 change, all three anchors as the brief verified them:

- `PendingPlacement` gains `Public TimedOut As Boolean` (class at what was `:2096`).
- `PlaceAutomatedOrder`'s timeout path: the `TryRemove(reqId, ignored)` (was `:455`) replaced by `TryGetValue` + `lateEntry.TimedOut = True`; still returns `Accepted=False, Reason="timeout"`. Comment expanded to say the entry now stays registered.
- `HandlePlacementResponse`: the `If entry.TimedOut … Return` block inserted directly after the successful `TryRemove` (was `:1145`), spec code verbatim — orange `LATE placement response` line, **no rollback, no TCS completion**.

The 60-s sweep in `RegisterPendingPlacement` (was `:2121-2124`) is untouched and still GCs never-answered entries, timed-out ones included. Normal ack/rejection paths byte-identical (the new block is only reachable when `TimedOut` was set, which only the timeout path does).

## Commit 2 — `SignalBridge.vb` + host glue (`ebde3aa`, amended `7be69b8`)

### The class (new file, `Option Strict On` / `Option Explicit On`, ~700 lines)

Constructor `New(host As frmMainPageV2, log As Action(Of String, Color))` per spec 3b.

- **Watcher:** FSW on the payload path's directory (created if missing) filtered to the file name, `Changed`+`Created`+`Renamed` (the engine's `File.Replace` surfaces as rename/change) → each event resets a 150 ms `System.Threading.Timer` debounce. One retry (100 ms) on `IOException`; `FileNotFoundException`/`DirectoryNotFoundException` return quietly (the staleness timer owns dead-engine alerting).
- **Staleness:** independent 10-s `Threading.Timer` running while mode ≠ Off. A stale check zeroes `_lastSignalAtr`, force-STOPs (no-op unless Started), increments the consecutive counter; **red STAND-DOWN alert once at 3 consecutive** stale checks; any fresh payload resets counter + alert latch but never re-starts.
- **Single-flight:** `Interlocked` flag + rerun flag; a file event during processing supersedes (the loop re-reads the file from disk, so only the latest content is ever evaluated). A supersede racing the flag-clear is re-entered after the loop; anything later is caught by the next FSW event or the 10-s tick.
- **Gate chain** in exact contract §4 order (code is annotated 4.1–4.6): `schema_version` (≠1 → red alert + `refused: schema_version`) → freshness (`2.5 × max(exec_resolution_min,1)` min → `stale` + stand-down) → `SKIPPED` (→ `skipped` + stand-down; never hold the last signal) → de-dupe (→ `duplicate`) → action mapping in spec 3b order (`signal_state`/`direction`/`tier`/`mtf_blocked`/`below_min_move`/`ledger_mismatch`/`ws_down`; tier compares pinned enum strings only, so WEAK-LOW dies at **tier**, never inferred from direction; only ws `DOWN` blocks — `REST`/`DEGRADED` pass) → dual-arm interlock (**live mode only**) → operational gates (see Friction #1 for ordering). Informational fields (`verdict`, `skip_reason`, `cap_reason`, `scores`, `kelly`, `structural`, `settings_version`, `hold_status`) are parsed for logging only, never gated — grep the gate chain: none appear.
- **Status snapshot before gating:** every parsed payload updates `EngineArmed`, freshness anchors, and (fresh + `direction ≠ NONE` + `atr > 0`) `_lastSignalAtr`; a stale payload zeroes it. `engine.autotrade_armed = false` in any payload force-STOPs (contract §6 disarm).
- **Placement (Live, all green):** `manualSL = stop − StopLimitOffset` (LONG) / `stop + StopLimitOffset` (SHORT) — sign convention verified against `ExecuteOrderAsync`'s manual-SL branches (`newTrigSLprice = newSLprice ± stopLossOffset`), so the exchange **trigger lands exactly on the engine's stop** (spec §6 test 3 criterion); then `SetTradeTargets(manualTP:=target, manualSL:=…, sizeUSD:=size_usd)` + `Await PlaceAutomatedOrder(side, "limit")`. `acted (id …)` persists the de-dupe pair + cooloff anchor; `rejected: <reason>` does not.
- **Log-only:** the entire chain runs; `would-act: <DIR> @ <entry>, stop <S>, target <T>, size <N>` (invariant-culture) instead of placing.
- **Dispositions:** one line per consumed payload to the host log (`[BRIDGE]` prefix, color-coded) and append-only `bridge-dispositions.log`: `utc | instance_id | signal_id | verdict | confidence | direction | disposition` — soak-stable.
- **Public surface:** `Mode` (Off/LogOnly/Live), `LocalArmed`, `Started`, `EngineArmed`, `IsFreshNow`, `LastDisposition`, `LastSignalSummary`, `LastSignalAtr` (plain field-backed `Return _lastSignalAtr` — no lock, no controls, allocation-free), `IsLiveStarted` (for the §5 repoint), `TryStart() As String`, `[Stop]()` (VB keyword escaped), `SaveConfig(...)`, `StatusChanged` event (documented raised-on-any-thread), `IDisposable`.
- **Files beside the exe** (AppSecrets convention, `AppContext.BaseDirectory`): `bridge.json` (missing → spec 3a defaults + yellow log), `bridge-state.json` (`instance_id` + `last_acted_signal_id`, written after every acted/would-act, read at construction), `bridge-dispositions.log`. All three git-ignored; `bridge.example.json` + `bridge-state.example.json` shipped; `.vbproj` copies `bridge.json` to output only if present (same pattern as `secrets.json`).

### Host glue (`frmMainPageV2`)

- `Private signalBridge As SignalBridge` beside `_indicators`; constructed at the **end of `Shown`** (after DB init), own Try/Catch.
- `BridgeLog(msg, c)` → `AppendColoredText(txtLogs, "[BRIDGE] " & msg, c)` (self-marshalling + handle-guarded — bridge threads never touch controls).
- `Public ReadOnly Property StopLimitOffset` → `stopLossOffset` (in the automation-API region).
- `Friend ReadOnly Property IsMaxSlippageGuardChecked` → `maxSlippageATRchecked` (**addition**, see Deviations #2).
- `CalculateATRSlippageLimit` repointed exactly per spec 3c: `bridgeAtr` first, `_indicators.CurrentATR` fallback, `$70` constant unchanged, multiplier logic unchanged. Receive-thread safe (both reads are plain fields).
- `signalBridge?.Dispose()` added at the top of the `FormClosing` shutdown path (stops watcher/timers before the sockets go down) — small hygiene addition, see Deviations #8.

## Commit 3 — SIGNAL BRIDGE panel (`2dcb84e`)

New `grpSignalBridge` GroupBox at (18, 630) 482×215 on AutoTradeSettings (existing controls untouched — FrmIndicators still reads them): **ARM AUTOTRADE** checkbox, **START/STOP** button (text/color flips with `Started`), **mode selector** (`ComboBox` DropDownList: Off / Log-only / Live, index = enum value, default Off), two status labels (mode | started | engine ARM | payload freshness; last signal → disposition), and editable gate config (tiers CSV, size $, cooloff min, breaker $, UTC+8 window start/end) with **SAVE** (client-side `TryParse` + `SaveConfig` validation; errors shown orange in the status label; normalized values echoed back).

- Interlock enforcement lives in the **bridge** (`TryStart` refusal chain, non-sticky Started via the Mode/LocalArmed setters and every disarm path); the panel only calls the surface and repaints. Refusals are shown on the panel and logged by the bridge.
- `StatusChanged` arrives on arbitrary bridge threads → handle-guarded `BeginInvoke` marshal (`OnBridgeStatusChanged`), Try/Catch for the teardown race; handler unhooked in `FormClosed`.
- Programmatic combo/checkbox writes during repaint are guarded by `_suppressBridgeUi` so they can't loop back into the setters.
- **Nothing persists:** designer defaults + `cboBridgeMode.SelectedIndex = 0` at Load; explicit code comment forbids adding mode/ARM/Started to the item-A ergonomics persistence pass.
- `Friend Property Bridge` carries `Browsable(False)` + `DesignerSerializationVisibility(Hidden)` — the net9 WinForms analyzer (WFO1000) makes that an **error**, not a style choice.
- Log-only ignores ARM/START by construction: `TryStart` refuses when mode ≠ Live, and the gate chain skips the interlock outside Live.

## Commit 4 — FrmIndicators neutralized + repoint (`5e5acdf`)

- `UpdateSignals`: the auto-trading integration block (`If enableAutoTrading AndAlso CanPlaceAutomatedOrder() … ProcessAutomatedSignal …`, was `:689-698`) **removed**, replaced by an R1 comment. `ProcessAutomatedSignal`/`ExecuteAutomatedTrade`/`CanPlaceAutomatedOrder` left in place, unreachable (no churn in the dying file).
- `btnAutoTrade`: **disabled** with text `AUTO: see Bridge`, DimGray, set at the end of `FrmIndicators_Load` (implementer choice per spec: disabled-not-hidden, so it stays a visible pointer; its Click handler is unreachable).
- `AttachBridgeToSettings(bridge)`: one-liner pass-through to `_autoTradeSettings.Bridge`. Called from `frmMainPageV2.Shown` right after bridge construction (`_indicators?.AttachBridgeToSettings(signalBridge)`) — safe ordering: `_autoTradeSettings` is created in `FrmIndicators_Load`, which `Show()` runs synchronously inside the host's `Load`, before `Shown`.
- `CurrentATR`, the ATR display, `btnATR` paste, settings-form ownership: untouched.

### (a) The §5 `IsAutoTradingEnabled` repoint choice

Took the spec's **second ("cleaner") option**: all **FIVE** `frmMainPageV2` call sites (brief count correction confirmed — grep found exactly `:2515`, `:2549`, `:2614`, `:4287`, `:4295` at base) now read `signalBridge IsNot Nothing AndAlso signalBridge.IsLiveStarted`; post-change they sit at `:2566`, `:2600`, `:2665`, `:4341`, `:4349`. `FrmIndicators.IsAutoTradingEnabled` is left in place, unused, with a comment saying so (grep `_indicators.IsAutoTradingEnabled` → 0 hits). The two `And`-form sites kept their `And` composition (parenthesized) — behavior-preserving; the And→AndAlso cleanup stays housekeeping's. Semantics note: `LogTradeDecision` gating now keys off *bridge live+started* instead of the old FrmIndicators toggle — same intent (autotrade-session trade log), new owner.

---

## (b) Contract-vs-spec friction (contract won)

1. **Operational-gate order.** Spec 3b's parenthetical runs cooloff → breaker → window → flat/working-entry; contract §4.6 lists connected ∧ rate-limit ∧ flat ∧ working-entry ∧ cancel-pending ∧ cooloff ∧ breaker ∧ window. Implemented the **contract order** (which also subsumes the spec's "precise disposition" intent — connected/rate-limit/flat/working-entry get their own `refused:` tokens instead of dying inside the API).
2. **Cancel-pending** has no bridge-side gate: `IsCancelPending()` is host-private and the spec's tiny-API list doesn't expose it. It stays defense-in-depth inside `PlaceAutomatedOrder` and surfaces as `rejected: cancel pending`. (Contract lists it in §4.6; the check exists, just downstream — flagging rather than widening the host API unilaterally.)
3. **Session-window semantics.** Old FrmIndicators check is an *exclusion* range ("restricted time"); contract §4.6 says "**inside** session window (if configured)". Implemented the contract: entries allowed only INSIDE `window_start`–`window_end` (UTC+8), blank = unrestricted, start > end wraps midnight (that wrap math is what I read the spec's "spans-midnight semantics like the old FrmIndicators check" to mean). If the owner wanted an exclusion window, it's a sign flip in `IsInsideSessionWindow` — surface at review.
4. **`slippage_atr_mult` config key** (spec 3a schema) is loaded/persisted but operationally **inert in v1**: the design decision (§1) rides the existing `chkMaxSlippageATR` + `txtMaxSlippageATR` machinery, and the spec wires nothing from bridge config into it. Kept in the file for schema fidelity; noted here so nobody assumes it does something.

## Deviations / judgment calls (all scoped, none touch the invariant regions)

1. **`AttachBridgeToSettings` host call moved from commit 2 (spec 3c) to commit 4** — the method lands in commit 4; calling it in commit 2 would break the per-commit 0/0 gate. Net diff identical.
2. **`Friend ReadOnly Property IsMaxSlippageGuardChecked` added to the host** (not in the spec's member list): `TryStart` needs the checkbox state from non-UI threads; reading the control would violate the thread rule, so it exposes the existing `maxSlippageATRchecked` backing field.
3. **De-dupe uses `signalId <= last_acted`** (same instance), not equality: `signal_id` is monotonic per `instance_id` (contract §3), so an id at-or-below the acted watermark is necessarily a replay; a legitimate new signal can never be refused by this. Protects against replayed old files that might squeak past freshness.
4. **Log-only `would-act` advances the de-dupe pair + cooloff anchor (and persists state)** so the soak's disposition stream is gate-for-gate what live would produce (`refused: cooloff` lines included) and a re-fired file disposes `duplicate` instead of double-counting. Consequence: flipping log-only → live never re-acts on an already-would-acted signal. Flagging because the contract's wording is "acted on".
5. **Exactly-one-disposition guard:** an in-memory last-seen (instance, id) pair suppresses re-dispositions when FSW double-fires on the same payload (status still refreshes). Not persisted — an app restart may re-dispose the file's current payload once (join-visible but harmless; the persisted acted-pair still prevents re-acting).
6. **Initial read on mode Off→on** (`EvaluateNow` in the Mode setter) so status/dispositions don't wait up to a minute for the next engine write.
7. **Circuit breaker is not latched:** evaluated per consumed payload (`SessionPnLUSD ≤ −circuit_breaker_usd`, ≤ 0 disables); a breach trips force-STOP + `refused: circuit_breaker`. Matches the old FrmIndicators behavior (state-derived, re-trips immediately while breached); `TryStart` doesn't check it (spec §4's START list doesn't include it) — a re-START during a breach dies at the next payload's gate.
8. **`signalBridge?.Dispose()` in `FormClosing`** + (commit `7be69b8`) the FSW debounce kick wrapped against `ObjectDisposedException` — an in-flight FSW callback racing shutdown would otherwise be a process-fatal threadpool exception.
9. **Payload parse failures** (torn/malformed JSON, missing ids) log yellow and produce **no** disposition line — can't join a row without (instance_id, signal_id). Atomic writes make this engine-bug territory.
10. **`DateTime`/`Decimal` snapshot fields are read lock-free** by `IsFreshNow`/`LastSignalAtr` (writes are under `_sync`) — same accepted torn-read class as `StopLossTriggerOriginal`/`emergencyBaseline` (field comment cites it). `LastSignalAtr` must be lock-free per spec; the rest follow the house precedent.
11. **VB restructures:** `Await` is illegal inside `Catch` in VB (BC36943) → the IOException retry sets a flag and delays after the Try; `Stop` is a VB keyword → declared `[Stop]()` (call sites read `bridge.Stop()` normally).

## (c) §1 design-decision confirmations (owner may veto)

- **Live START requires `chkMaxSlippageATR` checked** — implemented as a `TryStart` precondition via the Friend property; refusal text names the checkbox. START does not re-verify it afterwards (unchecking mid-run disables the guard but doesn't STOP — same trust level as editing `txtMaxSlippageATR` mid-run; flag if that should disarm too).
- **ATR bridge-first** — implemented exactly (payload `atr` fresh → `_indicators.CurrentATR` → $70).
- **Sizing v1 = fixed `size_usd`**, payload `kelly` never read for sizing (it isn't even parsed) — confirmed.
- **Transition scaffolding** — AutoTradeSettings stays FrmIndicators-owned/positioned; host constructs the bridge and hands it through `AttachBridgeToSettings` → `Friend Property Bridge`. Confirmed.

## (d) Full disposition-token set (soak reviewers join on these — STABLE from here)

Line: `utc | instance_id | signal_id | verdict | confidence | direction | disposition` (UTC `yyyy-MM-ddTHH:mm:ssZ`, invariant culture everywhere).

- `acted (id <exchange_order_id>)`
- `rejected: <reason>` — API-level, after all bridge gates passed; `<reason>` comes from `PlacementResult.Reason`: `not connected` · `rate limiter not initialized` · `rate limit` · `cancel pending` · `position open (flatten first)` · `working entry exists` · `timeout` · `<code>: <message>` (exchange rejection) — the bridge's own pre-gates make the first six rare (they'd need a state flip inside the placement call).
- `would-act: <LONG|SHORT> @ <entry>, stop <stop>, target <target>, size <size_usd>` (log-only)
- `refused: <first-failing-gate>`, gate ∈ `schema_version` · `signal_state` · `direction` · `tier` · `mtf_blocked` · `below_min_move` · `ledger_mismatch` · `ws_down` · `interlock` · `not_connected` · `rate_limit` · `not_flat` · `working_entry` · `cooloff` · `circuit_breaker` · `window`
- `stale` · `skipped` · `duplicate`

## Implementer test-plan items (§6, runnable now) — all pass

- Build 0/0 × 5 commits (table above). New file starts `Option Strict On` / `Option Explicit On`.
- Gate order matches contract §4 — the chain is a single straight-line function annotated 4.1–4.6; no gate reads an informational field.
- No controls touched from bridge threads — grep `SignalBridge.vb` for `txt|lbl|btn|chk|cbo|.Invoke|Controls`: only comments; all UI via the host log delegate + the panel's own marshalled `BeginInvoke`.
- `LastSignalAtr` read path: `Return _lastSignalAtr` — no lock, no allocation, no controls.
- No `RecordCommandedSLPrice` needed: the bridge adds **zero** SL edits (grep `private/edit` in `SignalBridge.vb` → 0). Chase/echo-classification regions untouched — the full-stack diff hunks in `frmMainPageV2.vb` sit at `:23/:389/:471/:536/:1186/:2152/:2566/:2600/:2665/:2718/:4341/:4349/:4870`; nothing inside `HandleQuoteUpdates`, `UpdateStopLossForTriggeredStopLossOrder`, or the open-`StopLossOrder` echo classification.
- `ApplyCloseFill` untouched; no `btnClose`/`btnMark` references; orders placed only through `PlaceAutomatedOrder` (post_only/reduce_only discipline stays inside it).

**Owner §6 tests 1–5** (mock `verdict_signal.json` files; contract §3 example is byte-representative) are ready to run — nothing is gated on the engine. Reminder from the spec: log-only soak starts when the owner flips `signal_bridge.enabled` engine-side; live-at-min-size additionally waits on the engine geometry-pass confirmation.

## Suspicious-nearby (report-only, not touched)

- `frmMainPageV2:21` — `Private _autotradesettings As AutoTradeSettings` (lowercase) looks like a dead legacy field: never assigned, never read. Housekeeping candidate.
- `FrmIndicators_Load:82` — `AUTO_TRADE_COOLDOWN_MS = 60000 * Integer.Parse(_autoTradeSettings.txtCooloff.Text)` throws on non-numeric input at form load (pre-existing; now feeds nothing reachable, retires with the form).
- The five repointed sites still mix `And` with `AndAlso` (pre-existing style, preserved); housekeeping already owns the And→AndAlso pass.
- `HandleOrderPositionUpdates`'s `OrderLog`/`PositionLog` one-shot flags are never reset between trades within a session (pre-existing) — the repoint didn't change their lifecycle.
