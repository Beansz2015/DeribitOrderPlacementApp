# Kickoff brief — Signal-bridge tie-in implementer (`spec-autotrade-tiein.md`)

**Pre-staged by the coordinator 2026-07-08. Fire this into a fresh Opus-high conversation the moment the current stack (`44cd51e..HEAD`) is pushed. Uncommitted draft until then.**

---

## Your role
You are the implementer for the **signal-bridge tie-in** — the execution-side consumer of the VerdictEngine signal bridge. This is a new subsystem (`SignalBridge.vb`) + threading + a light touch on a dying module (`FrmIndicators`). **Model/effort: Fable high while the restored window lasts (~Jul 19), else Opus high** (new threading, receive-path-adjacent, order-API caller — the spec's own header allows either). Loop = spec → implement → impl-report → coordinator review; I review your report against the actual code afterward (`git show` per commit, build, re-grepped invariants — I do not trust reports).

## Read first, in this order — before writing any code
1. `docs/spec-autotrade-tiein.md` — your spec. 4 commits, test plan §6 (runnable without the engine), rollout §7.
2. `docs/integration-contract-verdictengine.md` — **FROZEN v1**, canonical for consumer behavior. **Where the spec and the contract disagree, the contract wins and you report the deviation.** Focus: §3 emitter notes (the informational fields you must NEVER gate on — `settings_version`, `kelly` zeros, `SKIPPED`'s `ledger_mismatch`, `signal_id` gaps, `health.ws` precedence), §4 the exact gate-chain order, §6 the dual-arm interlock, §7 the rollout addendum.
3. `docs/spec-decouple-v2.md` — the in-process API you call: `PlaceAutomatedOrder`, `SetTradeTargets`, read-only `OpenPositionSizeUSD`/`OpenPositionAvgEntry`/`SessionPnLUSD`/`IsWebSocketConnected`/`IsFlat`/`HasWorkingEntryOrder`. **Exact member names verified in code at `541f185` (`frmMainPageV2.vb:355-430`); there is NO `CanTrade` member — the rate gate is `CanMakeAPIRequest`, and `PlaceAutomatedOrder` runs its own strict gate chain internally.** `SetTradeTargets`' named args (`manualTP:=`, `manualSL:=`, `sizeUSD:=`) match the real signature — the spec §3b call compiles as-is; targets flow textbox → TextChanged → engine mirrors synchronously inside the marshal, so calling it then `PlaceAutomatedOrder` is race-free. Bridge bonus (landed after the spec was written): a bridge trade sets `manualTP` (absolute), and the fill-reanchor fix (`326cbaf`) deliberately skips manual-TP trades — the engine's `target` is placed and never re-anchored, exactly contract R2.
4. `docs/HANDOVER-2.md` §4 — the binding invariants.
5. `docs/spec-back-session-2026-07-04.md` — context for the ~617-line `frmMainPageV2.vb` delta since the old anchors. **Locate strictly by symbol; pre-2026-07-04 line anchors are stale.**

## Base commit
Start from the pushed `master` HEAD (`541f185` once the owner pushes the current stack). **Verify `git rev-parse HEAD` matches the pushed head and `git status` is clean before you start.** Do NOT start on an unpushed base.

## Non-negotiable invariants (violating any is a review-blocker)
- **Do NOT touch the triggered-SL chase or the echo-classification regions.** This spec deliberately stays out of them (coordination note, spec §Coordination). Keep it that way.
- **Every new programmatic SL edit MUST call `RecordCommandedSLPrice`** — otherwise its echo is misread as a *manual* edit and the SL-reconciliation + M.SL loss-cap logic breaks. (This spec should add none; if a revision does, this rule binds.)
- **SL legs are native trailing stops — never re-anchor them.** `placedStopLossPrice` (chase ref) and `emergencyBaseline` (loss-cap anchor) deliberately diverge post-trigger — never re-tie them.
- **Receive-thread code touches engine backing fields only, never WinForms controls.** Your `LastSignalAtr` property is read from `CalculateATRSlippageLimit` on the receive thread → plain field-backed property: no locking, no controls, allocation-free.
- **All UI marshals handle-guarded** (`UiInvoke` / guarded `AppendColoredText`). Bridge threads never touch controls directly — UI goes via the settings form's own `Invoke` and the host log delegate.
- `ApplyCloseFill` is fields-only (no ByRef); `btnClose`/`btnMark` no longer exist — don't reference them.
- Don't bypass the decouple-v2 API to place orders — `post_only`/`reduce_only` discipline lives inside it.

## Ground rules
- **New files start `Option Strict On` / `Option Explicit On`.**
- Build **0 errors / 0 warnings** after every commit (MSBuild `/t:Rebuild`, net9.0-windows).
- **Commit locally per the 4-commit structure; NEVER push** — the owner is the only pusher.
- Scope discipline: suspicious-nearby findings go in the impl report, not the diff.

## What's testable now vs gated
- **Fully testable WITHOUT the engine** via hand-crafted `verdict_signal.json` files (spec §6). The contract §3 example payload is byte-representative — copy it, mutate fields, drop it on the watched path.
- The engine emitter is **already live-ready and pushed** (engine `23fd8b9`), shipping behind `signal_bridge.enabled: false`.
- **Log-only soak** starts when the OWNER flips `signal_bridge.enabled` on the engine — nothing else gates it.
- **Live-at-min-size** additionally waits on the owner's confirmation that the engine's placed-geometry (structural-first) pass is live (contract §7 addendum). Log-only does not wait.

## Owner-vetoable design decisions (spec §1 — surface if you have concerns)
- Live mode requires `chkMaxSlippageATR` checked as a START precondition (rides the existing slippage guard rather than new plumbing).
- ATR source becomes bridge-first (fresh payload `atr` → `_indicators.CurrentATR` → $70 const).
- Sizing v1 = fixed `size_usd` from bridge config (`kelly` ignored per contract).
- Transition scaffolding: `AutoTradeSettings` stays owned by `FrmIndicators` until the post-soak retirement spec.

## Deliverables (4 commits, spec §2–§5)
1. **`TimedOut` ack hardening** (decouple-v2 review addendum) — late-response log-only, no rollback.
2. **`SignalBridge.vb` + host glue** — FSW + debounce + independent 10-s staleness poll; gate chain in exact contract §4 order; `CalculateATRSlippageLimit` bridge-first repoint; `StopLimitOffset` property.
3. **AutoTradeSettings SIGNAL BRIDGE panel** — ARM / START-STOP / mode / status / gate config; interlock enforcement (contract §6); nothing persists.
4. **Neutralize FrmIndicators' autotrade trigger** (R1) + `AttachBridgeToSettings`; repoint `IsAutoTradingEnabled` call sites. **Count correction (verified at `de7d87b`): the spec §5 says three call sites — there are FIVE in `frmMainPageV2.vb` (`:2515`, `:2549`, `:2614`, `:4287`, `:4295`; the last two grew in the close path during the 07-04 session). Repoint all five; locate by symbol.**

**Anchor freshness (coordinator-verified 2026-07-13 at `de7d87b`):** commit 1's insertion points are current — the timeout-path `pendingPlacements.TryRemove` at `:455` inside `PlaceAutomatedOrder`, `HandlePlacementResponse` at `:1137` with the success `TryRemove` at `:1145`, the 60-s sweep at `:2123`; no `TimedOut` member exists yet. FrmIndicators: the trigger block is `:689-691` (`If enableAutoTrading AndAlso CanPlaceAutomatedOrder() … ProcessAutomatedSignal`), `IsAutoTradingEnabled` at `:56`, `_autoTradeSettings` created at `:66`. None of the unpushed stack's commits touched these regions.

Then the impl report — `docs/impl-report-autotrade-tiein.md`, standard format + explicitly: (a) the §5 `IsAutoTradingEnabled` repoint choice, (b) any contract-vs-spec friction (contract wins), (c) the §1 design-decision confirmations, (d) your **full disposition-token set** (log-only `would-act:…` + API `rejected:<reason>` beyond the contract-§4 set — the soak reviewers join on these).

I review commit-by-commit against code before the owner runtime-tests.
