# ROADMAP — August 2026 era (plan of record; trimmed 2026-08-02, **re-audited against code 2026-08-07**)

**Every row below was checked against the SOURCE or the artefact at HEAD `8f04c27`, not against
another doc** (`HANDOVER-6.md` §7 lesson 6 — a doc is never evidence about a doc). Rows carry the
file:line that establishes them so the next audit can re-derive rather than re-believe. What the
pass changed, what it recovered from the 2026-08-02 trim, and what it could NOT verify statically:
**§8**.

**What this doc is for:** §1 (owner track), §4 (the centerpiece), §5 (the live backlog) and §6 (what
we deliberately do NOT build). Closed milestones are indexed in `ARCHIVE-closed-milestones.md`,
which points at each one's real spec → review chain — they are not re-narrated here. Current state,
open items and the queue live in `HANDOVER-6.md`.

**Standing constraints (binding):** the owner-rejected list stays rejected (session-end
auto-flattening; app-side re-gating of engine logic; P2 SL policy; P3 hybrid chase; the emergency
clamp; auto-arm-on-boot; rate-limiting the loss cap). **R1: the engine decides signal logic — the
app never grows a second brain.** One implementer at a time; reviews execute the gate; the owner is
the only pusher.

## §1 — Owner track (no code)

1. ~~Phase 3: enable the session policy~~ — **DONE**, verified `session_policy.enabled = True`.
   ⚠ NY is unconfigured and therefore *unrestricted at full size* — see `HANDOVER-6.md` §2.3.
2. **Size ladder** at FIXED size: 10 → 20–30 → normal. *(open)* — **and it is NOT superseded by N2.
   It is SCOPED by it.** *(Corrected 2026-08-13 at the owner's challenge; the coordinator had said
   N2 "replaced the ladder", which is too broad and wrong for two of the three sizing paths.)*
   Verified in code — the `Risk-size` checkbox is **bridge-only**:

   | Path | Size source | Affected by `Risk-size`? |
   |---|---|---|
   | Manual order buttons | the **Amount box** (`orderAmountVal`, `frmMainPageV2.vb:517`) | **No — never.** The ladder always governs here. |
   | The manual **SIZE** button | computes a risk size *into* the Amount box on demand (`ApplyRiskBasedSize`, `frmMainPageV2.vb:287`) | **No** — a separate, deliberate action |
   | **Bridge** trades | Amount box when unticked · risk-derived when ticked (`SignalBridge.vb` act site) | **Yes — this is the only path it touches** |

   `frmMainPageV2.vb` contains **no** read of `RiskSizeBridgeTrades` other than the property that
   hands it to the bridge. **So: untick `Risk-size` and the ladder is fully in force again,
   everywhere.** With it ticked, the ladder still governs every manual trade, and
   `risk_per_trade_usd` becomes the rung for bridge trades only.
3. **AWS London migration** — `production-cutover-checklist.md` §9; the key choreography kills the
   two-executor window. *(open)*
4. ✅✅ **N2 LOG-ONLY FULLY VERIFIED 2026-08-13 — the SESSION MULTIPLIER is now observed under N2,
   which was the last unproven link in the sizing chain.**
   `[BRIDGE] signal #10 SHORT (MEDIUM/SHORT) -> would-act: SHORT @ 63611.50, stop 63669.48,
   target 63539.50, size 50`. Recomputed at the coordinator seat: dist = 57.98 ⇒ base **100** ⇒
   **× LONDON 0.5 = 50**. Exact match, and both values clear the min-10 clamp so the divergence is
   genuinely visible — the `HANDOVER-6.md` §5.9 blind spot does **not** bite at these sizes.
   🚨 **THE MULTIPLIER SCALES THE RISK, NOT JUST THE SIZE.** That trade risks **$0.046**, not the
   configured $0.10, because the size is halved while the stop distance is not.
   **`risk_per_trade_usd` is the risk BEFORE the session multiplier; effective risk = risk × mult.**
   LONDON delivers half, ASIA three-quarters, NY the full amount.
   ✅ **The session policy REFUSED for the first time in the owner's bin** —
   `refused: policy(LONDON/context)` on signals #7, #8, #33–#36, all `SHORT (MEDIUM/SHORT)`.
   LONDON is `MEDIUM | CONFIRMED | 0.5`, so the tier passed and the **context** gate refused. #10
   passed both, which is why it is the only would-act of the session.
   ✅ **`bridge-state.json` now EXISTS in the owner's x64 bin** (`last_acted_signal_id: 10`),
   created by this run — log-only advances the watermark, exactly as contract §4.3 specifies. Two
   consequences: the next `backup-orderapp.ps1` will be **4/4**, and the deploy hazard is now live —
   losing that file would let signal ≤10 re-act.
   ✅ **An engine RESTART was handled correctly.** Signal ids reset to #1 while the app's restored
   watermark still read `52 (engine e50f3db5…)`. Verified safe: the engine minted a new
   `instance_id` (`0d268b88…`), and de-dupe identity is the **pair**, so nothing was falsely
   suppressed. Four distinct engine instance ids now appear in the disposition log.
   *(Earlier NY run below.)*
   ✅ **N2 LOG-ONLY DRY RUN PASSED 2026-08-10 — the formula is verified against a real payload.**
   `[BRIDGE] signal #52 SHORT (MEDIUM/SHORT) -> would-act: SHORT @ 63894.00, stop 63929.70,
   target 63838.00, size 170`. Recomputed independently at the coordinator seat:
   dist = 35.70 · `0.1 × 63894 ÷ 35.70 = 178.97` · `floor(17.897) × 10` = **170**. Exact match.
   Cap did not bind, min-10 did not clamp, NY's unity multiplier passed through untouched. Risk if
   stopped = **$0.095** against a $0.10 target.
   🚨 **REAL STOPS ARE FAR TIGHTER THAN THE PLANNING TABLE ASSUMED — $35.70, not $70–$400.** So
   sizes run **~100–210**, not 30–90, and **N2 on is ~17× the Amount box (10)**. Anyone reasoning
   about size from the earlier table will be low by a factor of five.
   ⚠ **Both guards are INERT at risk 0.1:** the 500 cap needs a stop under $12.80, and the $10
   breaker measures **session P/L** (`IsBreakerTripped(breakerUsd, SessionPnLUSD)`,
   `SignalBridge.vb:1141`) so it needs ~105 full-stop losses. **`risk_per_trade_usd` is the only
   active control during testing.**
   **More first-observations in the owner's bin:** `executor.mode` = `"LOG_ONLY"` (second pinned
   value) · **`last_signal` POPULATED — trigger (a) observed there for the first time** · the x64
   `bridge-dispositions.log` grew, confirming `HANDOVER-6.md` §5.11 · one row per payload across
   #52–#55 (cardinality freeze holds) · `instance_id` identical in the log and in `last_signal`,
   so the contract's (instance_id, signal_id) join works end to end.
   **Still NOT observed:** the session multiplier under N2 — #52 landed in NY, whose unity
   multiplier is a no-op. ASIA (×0.75 ⇒ 120) needs 08:00–15:59 local; LONDON (×0.5 ⇒ 80) needs
   16:00–20:59 local. At base 170 both clear the min-10 clamp, so the divergence would be visible.
   *(Setup below.)*
   🟡 **Owner chose the values 2026-08-10: `risk_per_trade_usd = 0.1` ·
   `max_size_usd = 500` · circuit breaker stays `$10` · NY stays unconfigured (deliberate ruling,
   `HANDOVER-6.md` §2.3).** *"Will adjust after/during testing."*
   **What those values actually produce** — the formula uses the PAYLOAD's own entry and stop
   (`refPrice = p.Entry`, `dist = |p.Entry − p.StopLevel|`, `SignalBridge.vb:RiskSizedBaseForPayload`),
   not the live market price. At risk `0.1` the shorthand is **`base = floor(entry ÷ dist ÷ 100) × 10`**.
   At an entry near 64,000 that gives **90 at a $70 stop · 30 at $200 · 10 at $400 and wider**, with
   the session multiplier folded in exactly once on top.
   ⚠ **`max_size_usd = 500` is INERT at this risk level** — the cap only binds below a **$12.80**
   stop distance, which no real signal produces. It is a backstop, not a control. **The control at
   these settings is `risk_per_trade_usd`.**
   ⚠ **The min-10 clamp swallows the session multiplier at small sizes** (`HANDOVER-6.md` §5.9): at
   base 20, both ASIA ×0.75 and LONDON ×0.5 floor back to 10, so the multiplier is invisible unless
   the base is 30 or more. Do not read "no divergence" as "the policy is off".
   *(Original entry below.)*
   ~~🚨 **N2 enable — tick `Risk-size`.**~~ *(was: open; the only owner action left on N2, moved here
   from §3 where it was buried in prose.)* **Verified at HEAD:** the knob is `risk_size_bridge_trades` and
   **an absent key is `False`** (`AppUserSettings.vb:142-143`), persisted at `:189`; the control is
   `chkRiskSizeBridge` on the settings form (`AutoTradeSettings.Designer.vb:365`, tooltip `:369`).
   **Three knobs compose and must be set TOGETHER** — `risk_per_trade_usd`/`max_size_usd`, the
   circuit breaker, and the session policy whose NY bucket is unconfigured-hence-largest.
   The full trap: `HANDOVER-6.md` §2.
5. ✅ **C1 emitter enable — DONE 2026-08-10, and the file is LIVE in the owner's bin.**
   `feedback_output_path` set to the contract §8.2 default; startup line reads
   `Executor feedback: configured - C:\Dev\DeribitBridge\executor_feedback.json`.
   ⚠ **It needed an x64 REBUILD first** — the bin predated C1 by six days and the emitter was simply
   not in the binary, so the correct config change produced *no startup line at all*.
   `HANDOVER-6.md` §4 carries that trap now.
   **First observations in the OWNER's bin** (previously only ever seen on the harness bin):
   trigger (f) initial write · `executor.mode` = `"OFF"` · `executor.ws` = `"OK"` at connect (E6a's
   connect edge) · the 10 s heartbeat republishing (`feedback_id` 6 → 16) · and **trigger (e), the
   graceful-close final write — the file stopped dead on app exit and stayed stopped, which is
   §8.1's "silence = dead executor" property observed for the first time.**
   Schema conformance checked field-by-field against `integration-contract-verdictengine.md` §8,
   including the pinned enums at its lines 176–177: `executor.mode` ∈ OFF/LOG_ONLY/LIVE ·
   `executor.ws` ∈ OK/DOWN · `position.direction` ∈ LONG/SHORT/**FLAT** with zeros. All correct.
   *(Original entry below.)*
   ~~**C1 emitter enable — add `feedback_output_path` to `bridge.json`.**~~ *(was: open, optional,
   and nothing blocks it.)* **Verified at HEAD:** that key is the **only** switch — absent ⇒
   `ExecutorFeedback.IsConfigured = False` ⇒ no timer, no worker, **no file created**
   (`ExecutorFeedback.vb:32`, `:84-88`, `:110-113`; every entry point re-checks it at `:419`,
   `:442`, `:475`, `:578`). Enabling ours does **not** depend on the engine: we publish, they
   consume when their build lands. It is also the precondition for observing the five behaviours
   still unobserved at runtime (§5, last row).
   ⚠ **Standing obligation, not an open item:** C1's **E4 revisit trigger** re-opens before ANY
   consumer *records* the `avg_entry` join rather than re-deriving it. `HANDOVER-6.md` §2.
6. Rulings as they arise; matrix/policy revisions on the engine seat's cadence (evidence discipline:
   revise at regenerations, not week-to-week).

## §2 — Pre-ladder quick wins — **ALL DONE 2026-07-25** (Q1 ntfy · Q2 signal columns · Q3 ops tooling)

Owner-accepted; see the archive. **One optional item was never specced and remains available:**

| # | Item | Notes |
|---|---|---|
| Q4 | *(optional)* **Phase B item F — USE-ENGINE-LEVELS button** (manual trade borrows the latest payload's levels) | **Verified genuinely unbuilt at HEAD** — zero `UseEngineLevels`-shaped symbols in source. Modest value; the tie-in shape is stable, so the old "needs a fresh look" caveat is cleared. Take it if a pass has room. |

## §3 — Pre-normal-size — **ALL CLOSED; N2's owner enable now tracked as §1.4**

N1 · N1b · N1c · EV · SF/SF2 · N2b are closed; **N2 is code-approved, unblocked and ships DISABLED**
(verified above). Verdicts and doc chains: `ARCHIVE-closed-milestones.md`.

## §4 — The era's centerpiece — C1

| # | Item | Status |
|---|---|---|
| C1 | **v2 feedback file** | **PHASE 1 IS COMPLETE ON OUR SIDE — documentation phase closed 2026-07-29, emitter closed 2026-08-06.** Specced (`spec-c1-feedback-emitter.md`), five escalations ruled **E1–E5** plus **E6** after, implemented, reviewed (`review-c1-feedback-emitter.md`), defect **D1 fixed and verified**, **all five acceptances 3–7 PASSED** (`runtime-record-c1-feedback-emitter-2026-08-06.md`), spec-back **SB1–SB5 upheld and folded** (`spec-back-c1-acceptance-2026-08-06.md`). **Contract §8 stays the binding schema** — the spec implements it and never restates it. **Ships OFF**; the enable is §1.5. Gate `GATE PASSED` at every commit, OrderCheck 173 → 268. |

**What remains on C1, and what each thing waits for — the dependencies, stated as dependencies:**

- **Engine consumption** — their queue, behind their net-EV rider. Not ours, not blocking.
- **The owner enable** (§1.5) — unblocked *today*; it does not wait on the engine.
- **Phase 1 display soak** — waits on the enable **and** on their consumption build. It is the gate
  on Phase 2, so Phase 2's gate has moved from "the emitter exists" to "the emitter is switched on
  and something is reading it."
- **Phase 2 — ACTIONABLE EXITS, the largest remaining P/L lever — stays FENCED** as a future
  signal-schema-v2 amendment with a pinned field. **Never parse `hold_status`.**
- ⚠ **E4's revisit trigger** is the binding precondition on any consumer that *records* the
  `avg_entry` join (a CSV column, a card binding, a stored achieved-entry, the T7/v2.1 field) rather
  than re-deriving it. The mitigation then is an immutable snapshot published by reference, not a
  lock. It expires on its own terms; nothing in the implementation or the acceptances went near it.

## §5 — Hygiene backlog (anytime, unscheduled, all on record)

Each row says how it stands **at HEAD `8f04c27`**. *Verified* = re-derived from source this pass.
*Observational* = a runtime/UI behaviour that a static pass cannot settle either way (§8.3).

- **A `PositionModelChanged` seam.** *(Verified.)* An observation from
  `impl-report-c1-feedback-emitter.md` §6, **not** a proposal. The **four snapshot fields** —
  `positionSizeUSD`, `positionAvgEntry`, `placedStopLossPrice`, `manualTPval` — carry **17
  assignments across 10 methods on two threads**, re-counted at HEAD (11 + 2 + 2 + 2; comparison
  forms excluded), so C1's emitter needs 10 hooks and its completeness argument is an *enumeration*
  rather than a structural guarantee. One seam would collapse that to a single hook.
  ⚠ **The receive-loop dispatcher is NOT that seam** — `HandleQuoteUpdates` /
  `HandleOrderPositionUpdates` are `Async Sub`, so a hook there fires at their first `Await`, before
  their post-await writes: it would look complete and be wrong on the two hottest handlers.
- **`Get-ProcessWindows` intermittently misses the OWNED `AutoTradeSettings` window.**
  ✅ **CLOSED 2026-08-07 — fixed, reviewed, `D1` fixed and verified. Nothing owed.** Final shape:
  `Wait-ForMatchingElement` in `tools/harness-common.ps1`, attempt 1 outside the retry budget,
  `Ambiguous` never waited out, loud timeout unchanged. Gate 268/268 and the censuses re-run at the
  coordinator seat; zero `.vb` touched. **The specced cause never existed** — the settings window is
  never a desktop-root child (probe: 0/39), so the flakiness was render latency. Full arc and doc
  chain: `ARCHIVE-closed-milestones.md`, row `OW`. Lessons: `HANDOVER-6.md` §7 items 8 and 9.
  *(History below, kept because the arc is instructive.)*
  **Fix BUILT and REVIEWED; APPROVED with one defect, `D1`, which was the coordinator's own.** `Wait-ForMatchingElement` landed (`2c6cb7b`, `4fe4a33`), the union seam was
  removed (`9341888`) and the `close-popup.ps1` limitation recorded (`689a812`). Coordinator review
  executed the gate (**268/268**) and the censuses (**68/64**) and verified the harness-bin restores
  at the artefact. **`D1` (a defect found in code): the bounded wait can never retry in a real
  caller** — a fresh process's first UIA query costs 740–950 ms, the deadline is 500 ms, so the loop
  always returns after one attempt and `Start-Sleep` is unreachable. Proven structurally with a stub.
  Cause is the spec's parameter choice, taken from in-process figures. Correction and its regression
  test: `spec-harness-owned-window.md` §Coordinator review, R11.4. Seat: Sonnet, medium, continued.
  *(History below.)*
  **Cause ESTABLISHED by probe.**
  **The original description of this defect was wrong.** A 39-round timing probe (`a0e59bf`,
  `impl-report-harness-owned-window-probe.md`) found the settings window is **never** a desktop-root
  child — 0/39 — so `Get-ProcessWindows` has never returned it and never needed to. Control search
  reaches it through the main form's descendants in **9–22 ms**, succeeding 37/38 times. **The
  flakiness was render latency, not window enumeration.** Fix specced as a bounded
  wait-for-condition: `spec-harness-owned-window.md` §Probe verdict, R8 + §Acceptance. Seat: Sonnet,
  medium, continued. *(History below.)*
  **Spec written, IMPLEMENTED, and the first fix WITHDRAWN on its own spec-back.**
  `spec-harness-owned-window.md` §2's union rule was built exactly as specced (`1f4b931`,
  `2c5e986`) and turned out to be a deterministic regression: it doubles every settings-window
  control, so every drive script refuses. **D1 (a defect found in code) upheld 2026-08-07; the
  defect was in the SPEC.** `2c5e986` reverted by `59de8c1`; the pure seam from `1f4b931` stays and
  is currently unused. **Next step is a read-only timing probe, not a fix** — the cause is not
  established. Ruling, probe requirements and outcome table:
  `spec-harness-owned-window.md` §Ruling. Seat: Sonnet, medium, continued.
  *(Verified still present at HEAD `64fd613`: `tools/harness-common.ps1:92` enumerates
  `TreeScope::Children` of `RootElement`.)*
  The window is `Show(Me)`-owned (`frmMainPageV2.vb:6090`), and that enumeration sometimes omits it
  even while Win32 `IsWindowVisible` says it is open — it *is* found under `TreeScope::Descendants`
  of the main form. A retry immediately after opening succeeded. Surfaced by the SB3 acceptance run
  (`review-harness-exact-match.md` §5.2); **pre-existing, not an SB3 regression**, but it makes any
  settings-window drive step flaky.
- **FrmIndicators full retirement.** *(Verified: the form is headless but very much alive.)* It is
  still constructed (`frmMainPageV2.vb:968`) and still runs **its own `ClientWebSocket` and
  `ConnectAndStream` receive loop with an unguarded reconnect** (`FrmIndicators.vb:20`, `:69`,
  `:161` — the old audit item #11), for one purpose: `CurrentATR` (`:38-41`) as the **fallback** ATR
  source. The chain is payload ATR → `CurrentATR` → the `atrFallbackVal = 70D` constant
  (`frmMainPageV2.vb:36`, `:3619-3623`). **So the dependency is now explicit: retiring the form
  means accepting payload-ATR-or-70, nothing else.** More attractive with the engine 24/7 on AWS,
  where the fallback is nearly never exercised. The `AutoTradeSettings` half of the old
  "FrmIndicators/AutoTradeSettings retirement" item is **already done** — that window is owned and
  re-parented by `frmMainPageV2` (`:975`, `:6084-6091`), so only the ATR source is left. The form
  also still carries dead code (`UpdateEmaVwapLabels`, `FrmIndicators.vb:1281`, zero call sites).
- **`backup-orderapp.ps1` fails confusingly when `-TargetDir` cannot be created.** *(Verified
  2026-08-07, hit for real by the owner.)* Line 44 does
  `New-Item -ItemType Directory -Force -Path $TargetDir`, but on some drives that **returns success
  without creating anything** — reproduced on `D:\` on the owner's box: `New-Item` throws nothing,
  `Test-Path` then reports `False`. The failure surfaces two lines later as
  `Resolve-Path : Cannot find path … because it does not exist`, which points at line 47 and reads
  like a bad argument rather than an un-writable target. **Fix:** re-test the path after creating it
  and fail with one clear message naming the target. Tools-only, Sonnet/medium.
- **Edit T.S. post-trigger rework.** *(Verified the home exists: `EditStopLossTo`,
  `frmMainPageV2.vb:6457`, `:6513`, `:6550`.)* 4-trap catalog in
  `runtime-checklist-hybrid-session.md` §C.
- **User-typed fractional / off-tick inputs — loud rejection.** *(Verified the boundary.)*
  `RoundToTick` shipped and covers exactly the three autonomous-fractional sites plus the B.E.
  trigger (`frmMainPageV2.vb:381`, `:3137`, `:6532`; `SignalBridge.vb:810`, `:976`).
  **User-typed offsets on the manual edit buttons are deliberately OUT of that scope**
  (`spec-tick-rounding.md` §2 and its "deliberately NOT changed" note): the exchange rejects them
  loudly (`-32602 "must conform to tick size"`) and the user corrects. The open item is app-side
  pre-validation so the rejection is ours rather than the exchange's.
- **#9 parse-per-handler.** *(Verified still present: 14 `JObject.Parse` sites in
  `frmMainPageV2.vb`, 12 of them `Parse(response)` per handler.)* Dispatcher spec exists
  (`spec-medium-dispatcher.md`); **the deferral recommendation stands** — and its precondition is
  unique JSON-RPC ids per request class (old audit F15), which any response-matching work needs
  first. *(Recovered this pass — it lived only in `CODE_AUDIT_FABLE5.md` §E, which is not in the
  read path.)*
- **`TradeRecord.SlippageATR` / `MaxSlippageExceeded` are declared and never written.** *(Verified:
  `TradeRecord.vb:18-19`, zero writers and zero readers at HEAD — the live slippage machinery is the
  unrelated `maxSlippageATRmult`/`chkMaxSlippageATR` chain.)* Either populate them or delete them;
  today they are two fields that read as data and are not. *(Recovered from the audit's §E.)*
- **`Option Strict Off` on the legacy files.** *(Verified: `Option Strict On` in the seven newer
  files — `AppSecrets` · `AppUserSettings` · `ApplicationEvents` · `ExecutorFeedback` ·
  `RemoteNotifier` · `SessionPolicy` · `SignalBridge` — and absent from `frmMainPageV2.vb`,
  `FrmIndicators.vb`, `TradeRecord.vb`.)* Convention, not a defect; recorded so the split is a
  decision rather than an accident.
- **Item G — stale-SL-box cosmetic.** *(Observational.)* Mechanism documented in
  `runtime-record-live-ladder-2026-07-23.md`.
- **Housekeeping smoke leftovers** — the **8b checkbox-OFF half** (both chase-abort arms sit under
  the one `maxSlippageATRchecked` switch, `frmMainPageV2.vb:3705`; the OFF case was never observed)
  and the **14g long-PnL observation**. *(Observational.)*
- **A2 — manual-freeze best-effort live observation.** *(Observational.)* Recipe in
  `runtime-checklist-hybrid-session.md`; **worst case if it is broken is A1 semantics**, which are
  double-proven on trades #68/#71. *(Description restored — the trim had reduced this to the bare
  label "A2 manual-freeze observation".)*
- **B2 — M.SL-disarmed regression, opportunistic.** *(Observational.)* *(Description restored.)*
- **F1 — state re-sync on edit rejection (rollback class), accepted LOW.** *(Observational.)*
  Origin `spec-back-session-2026-07-07.md` §4, and **half of it is already resolved**: the
  `emergencyBaseline` phantom-advance was **fixed by removal** 2026-07-08
  (`spec-emergency-baseline-fix.md` — the owner found it disabled the M.SL loss-cap outright, not
  merely delayed it). **What remains open is only the `placedStopLossPrice` half**, the pre-existing
  runaway-fix trade-off: on a swallowed rejection that reference advances for a move that never
  landed, self-correcting on the next successful edit or any echo. Same deferral family as Audit2's
  F2 logging-only rule.
  ⚠ Two unrelated `F1`s exist: this one, and `CODE_AUDIT_FABLE5.md`'s F1 (entry-price snapshot,
  fixed long ago). Scope the ID before acting on a grep hit — the `E`/`D`/`SB` convention
  (`HANDOVER-6.md` §7bb) exists because this exact collision already cost a day once.
- **TP post-fill manual-move gap.** *(Observational; deferred.)*
- 🚨 **The `executor.ws` DOWN edge is UNAUDITABLE AFTER THE FACT.**
  🟢 **SPEC WRITTEN 2026-08-13 — `spec-ws-edge-audit.md`. Ready for an implementer seat, which the
  owner launches. Recommended: Opus, HIGH, fresh conversation** (the DOWN site is in the receive
  path, on a threadpool thread) — **but the spec asks the seat to first check whether the whole
  change fits inside `ExecutorFeedback.vb`, which would drop it to Sonnet/medium.**
  *(Found 2026-08-13.)* A real
  disconnect happened during the LONDON log-only session — `Server closed connection - scheduling
  reconnect` → `Successfully reconnected`. Per E6a the emitter must have published `ws: "DOWN"` and
  then `"OK"`, **but nothing can prove it**: `ws` is written only into `executor_feedback.json`
  (`ExecutorFeedback.vb:322`), that file is overwritten on every publish, and **no host-log or
  disposition line records the transition** (`frmMainPageV2.vb:1516` is the publish site, not a
  log). So the one edge that was hardest to provoke happened, and left no evidence.
  **Options:** one gray host-log line at each `ws` transition, or an append-only edge log. Either
  makes E6a's DOWN edge verifiable instead of merely specified. Tools/app-side, needs a spec.
  ⚠ **Do not confuse the two `ws` fields:** `executor.ws` is OUR socket to Deribit; `p.WsHealth`
  (`SignalBridge.vb:701`) is the ENGINE's health from the payload, which drives
  `refused: ws_down`. Different things, same word.
- **C1 runtime behaviours still unobserved** — `mode: "LIVE"` with an `acted (id …)` disposition ·
  the `ws` **DOWN** edge · a `breaker_tripped` flip · a **SHORT** `size_usd` · D1's race. All
  optional, none blocking; all gated on §1.5's enable (and the first on owner ARM/START). Lists:
  `runtime-record-c1-feedback-emitter-2026-08-06.md` §10 + §11.5.
- ~~Harness `Test-ElementMatch` exact-id match~~ — **CLOSED 2026-08-06**, `188bdab` / `3611d49`,
  approved in `review-harness-exact-match.md`. Verified at HEAD: `Test-ElementMatch` is gone;
  `Select-BestMatchIndex` (`tools/harness-common.ps1:116`) ranks exact `AutomationId` → exact `Name`
  → substring and **fails loudly on a tie at the winning tier**, with `Select-MatchingElement`
  (`:161`) as the gather-then-decide seam for all five call sites. Detail and the *why the `-Exact`
  switch was the wrong shape* ruling: `ARCHIVE-closed-milestones.md`.
- ~~Harness `txtTrigger` tree-order nit~~ — same defect as the row above; folded in there.

## §6 — Deliberately NOT building

**App-side exit logic** (partial TP / trailing TP / scale-outs) — C1 v2 is the correct home, with
the engine deciding; building it app-side forks the brain R1 forbids. **Multi-instrument** — the
engine's scope call, not this app's. Anything on the owner-rejected list above.

## §7 — Sequence of record

**Everything through C1 phase 1 is done** — N1 · N1b · N1c · EV · SF/SF2 · N2b · the C1 emitter
(implemented, reviewed, D1 fixed, all five acceptances passed 2026-08-06) and SB3's harness fix.

**Nothing is in flight and no implementer seat is running.** What is left divides cleanly:

| Track | Items | Waits on |
|---|---|---|
| **Owner** | push `8f04c27` · **N2 enable** (§1.4) · **C1 enable** (§1.5) · size ladder · AWS §9 | nothing but the owner |
| **Coordinator** | the §5 backlog | a free pass |
| **Engine seat** | C1 consumption | their net-EV rider |

**The §8.7 reorder, recorded because it was exercised and never written down:** the contract slots
the emitter "after N2 unless the trader reorders". The trader reordered — C1 was specced, built,
reviewed **and accepted** while N2 enable stayed pending — so the emitter was never downstream of N2
in practice. Neither blocks the other; both ship off/disabled.

## §8 — Audit record — 2026-08-07, against HEAD `8f04c27`

### 8.1 What the pass corrected

- **§4 was stale in the way that matters.** It read *"the EMITTER BUILD IS THE LIVE QUEUE ITEM"*
  while the emitter had been implemented, reviewed, fixed, accepted and spec-backed. The same
  sentence survives in `ARCHIVE-closed-milestones.md`'s C1 row and is corrected there in the same
  commit. **Lesson 6's class, third instance in this document stack.**
- **N2's owner enable was tracked only in §3 prose and `HANDOVER-6.md` §2**, i.e. nowhere in the
  owner-track section that exists to list owner actions. Now §1.4.
- **The C1 emitter enable was tracked nowhere at all** — the switch (`feedback_output_path`) appears
  in the impl report's run sheet and in the code, but no plan-of-record row said the owner has an
  action. Now §1.5. *This is the "satisfied state that was never an open item" cause from lesson 6:
  a build finishing silently creates an owner action that no queue audit will find.*
- **§5's bare labels got their content back.** `A2`, `B2`, `F1` and the fractional-offset row had
  been reduced to two- or three-word labels by the 2026-08-02 trim; the descriptions are restored
  from `HANDOVER-3.md` §3 and re-verified. **A label is not a backlog item — nobody can act on
  "B2 regression".**
- **"The four position fields" was wrong wording** carried from a summary: two of the four
  (`placedStopLossPrice`, `manualTPval`) are not position fields. The impl report says *snapshot*
  fields. Corrected, and the 17/10 counts re-derived from source rather than repeated.

### 8.2 Recovered from the 2026-08-02 trim (the check the owner asked for)

`HANDOVER-3.md` §3's backlog paragraph was the richest pre-trim list. Every item traced:

| Pre-trim item | Disposition, verified |
|---|---|
| Edit T.S. post-trigger rework | **carried** — §5, home confirmed at `frmMainPageV2.vb:6457` |
| A2 manual-freeze · B2 regression · F1 re-sync · TP post-fill gap | **carried**, descriptions restored |
| harness `txtTrigger` tree-order nit | **CLOSED** by SB3 |
| user-typed fractional offsets (loud rejection) | **carried**, scope boundary now stated |
| emergency-check hoist above the throttle (*"REQUIRES a single-fire latch first"*) | **CLOSED** — that is N1, done 2026-07-28 |
| **cross-app ATR period 7 vs 14** — *dropped by the trim, on no list since* | **SETTLED app-side, not lost:** `atrLengthVal = 7` with a 7 default on the accessor (`frmMainPageV2.vb:35`, `:40`), mirroring the engine (`HANDOVER-6.md` §6.8). Any change now is an engine-side owner decision, so it is correctly *not* an app backlog row — recorded here so the next grep finds the answer instead of the question. |
| **`_autotradesettings` naming leftovers** — *dropped by the trim* | **RESOLVED by the retirement pass:** the field is no longer the dead never-assigned one the audit flagged — it is assigned at `frmMainPageV2.vb:975` and drives the settings window at `:6084-6091`. Nothing to clean. |

**`CODE_AUDIT_FABLE5.md` §E ("Known items confirmed still present") is itself partly stale** — it is
a dated July snapshot, so it is not rewritten; a re-verification banner is added there. Re-checked
at HEAD: **still present** — #9 parse-per-handler, `Option Strict Off` legacy files, the
`TradeRecord` slippage fields, FrmIndicators' unguarded reconnect (#11). **Gone** — #10's
`PerformClick` coupling and `ExecuteAutomatedOrder` (zero symbols), F14's dead code
(`HandleOrderUpdates`, `ExportTradesToCSV`, `LogFailedEntry`, `MonitorConnectionHealth`,
`GetTradeStatistics`, `ProcessEstimationData`), F16's 500 ms manual-entry delay, F12's missing
crash backstop (`ApplicationEvents.vb:19-41`), F17's unused packages (the `.vbproj` now carries
three), the twice-enabled heartbeat (one `set_heartbeat` at `:1558`), the duplicate account-summary
pair.

### 8.3 What this pass did NOT establish

**Static verification cannot settle a runtime behaviour**, and saying so is the point of the row
tags. The six *Observational* rows in §5 — item G, the 8b checkbox-OFF half, 14g, A2, B2, F1 and
the TP post-fill gap — were **not** re-verified; they are carried on their original evidence and
each needs a runtime pass to close. Likewise, `feedback_output_path`, `risk_size_bridge_trades` and
the session-policy state were verified as **code contracts** (what an absent key means), **not** as
values in the owner's x64 bin: the gate does not build that bin and it has its own settings, DB,
journal **and gate config** (`HANDOVER-6.md` §4). Read them off the running form before acting.
