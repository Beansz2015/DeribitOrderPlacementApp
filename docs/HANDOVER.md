# Handover — DeribitOrderPlacementApp audit & remediation

**Written:** 2026-07-02, for a fresh conversation (Fable 5) continuing this work.
**Read this first, then the referenced `docs/` files as needed.** Auto-memory (`MEMORY.md` + linked notes) also loads each session — this doc is the task-focused complement.

---

## 1. What this project is

- .NET 9 **WinForms**, **VB.NET**. A manual/semi-automated trading front-end for **Deribit BTC-PERPETUAL** over a WebSocket (`wss://www.deribit.com/ws/api/v2`). Uses Skender.Stock.Indicators, System.Data.SQLite, Newtonsoft.Json.
- Owner is an experienced trader + ex-dev (GMT+8). Trades structural swing-to-swing on Deribit perps. This is a **crypto-trading codebase** — the `crypto-trading-context` skill should load; honor the trader profile (structural stops, not ATR-for-stops; MACD/Stoch are on the *rejected* list; spec-first; local-first git).
- `Option Strict Off` on all hand-written files (only auto-generated Designer files set it On). This enabled several latent bugs (implicit `Boolean→Decimal`, nullable comparisons). **New files must start with `Option Strict On`.**

## 2. Git state (as of this handover)

- **Branch:** `housekeeping-now` (all work lives here; **nothing pushed** — remote `master` is untouched).
- **Baseline:** work starts at `09d96d6` (credential fix); HEAD is `e5735ef` (transition-race fix).
- **Build:** green (`dotnet build DeribitOrderPlacementApp.sln` → 0 errors / 0 warnings).
- **Uncommitted:** everything in `docs/` is untracked (specs, impl-reports, this handover, `HANDOVER.md`). `CODE_AUDIT.md` is the only `docs/` file that's *tracked* (committed in H7). The owner manages `docs/` versioning.
- **Local git identity** was set for this repo to match history: `Beansz2015 <straitsettlements@gmail.com>`.

**Commit chain (newest→oldest, `09d96d6..HEAD`):** transition-race `e5735ef`; reword `a77ea75`; cross-thread `96eb781…b01f40e` (9 commits); housekeeping comment-fix `e467d1a`, redact `9ea1915`, `H8 ebbf2f7`…`H1 1b89f08`; HIGH `d06f919`(#3 follow-up), `1b3c169`(#7), `e76a3f3`(#5), `3fb1743`(#6), `02abecf`(#3), `6d30538`(#2), `bb8e6e7`(#4); credentials `09d96d6`.

## 3. What's been done — status

| Work | State |
|---|---|
| **Critical: API secret externalized** | ✅ Implemented + reviewed. Secret moved to git-ignored `secrets.json` via `AppSecrets.vb`; removed from all tracked source. See §7. |
| **HIGH bugs #2–#7** (+ #3 follow-up) | ✅ Implemented + reviewed (code-verified). ⏳ Runtime-tested: #3 effectively done, #4 tested (reconnect), #5/#6 **pending**, **#2 & #7 ON HOLD** (see §6). |
| **Housekeeping (H1–H8)** + redact + comment-fix | ✅ Implemented + reviewed. Includes deleting the obsolete `frmMainPage.*`. |
| **Cross-thread receive-loop fix** | ✅ Implemented + reviewed (code-verified, thorough). ⏳ Runtime test pending. |
| **Transition-race fix** | ✅ Implemented + reviewed (code-verified). ⏳ Runtime test pending. |
| **MEDIUM set (#9/#10/#11 + housekeeping)** | 📝 **Specced only** — not implemented. Reprioritized — see §8. |

**Right now:** the owner is **runtime-testing** the current bundle (cross-thread + transition-race + reword + #5/#6) under the VS debugger on a **test sub-account**. Expect them to return with test results. Pass → proceed to MEDIUM. Fail → diagnose (paste logs).

## 4. HOW WE WORK (important — follow this)

- **Spec → implement → report → review loop.** The owner runs *separate* implementer conversations from written specs (`docs/spec-*.md`). Each spec ends with an "implementation report" requirement. The implementer produces `docs/impl-report-*.md`; the owner pastes it back **here** for review.
- **Reviewing = verify the actual code, NOT the report.** Always: `dotnet build`, `git log`/`git show` the commits, and read/grep the real source to confirm every claim. This has caught real bugs the reports missed (e.g. the #3 null-safe-id-guard regression: `If messageId <> 3 Then Return` falls through for id-less messages under VB nullable semantics — verified empirically). Reports are a starting point, not truth.
- **Git discipline (owner's local-first workflow):** commit locally as work progresses; **never push** — the owner pushes tested milestones to `master` themselves. Every fix compiles cleanly before it's considered done; the owner runtime-tests before pushing.
- **Specs are handed to fresh conversations** — write them self-contained, with model/effort recommendations. Established pattern: Opus for safety/logic-critical fixes, Sonnet for mechanical cleanups.
- **Cross-thread testing note:** the cross-thread exceptions only surface under the **VS debugger** (which turns on WinForms' illegal-cross-thread check). A no-debugger run silently tolerates the UB — so tell the owner to test *under the debugger*.

## 5. CRITICAL INVARIANTS — every future change to `frmMainPageV2.vb` must preserve these

The receive loop (`ReceiveWebSocketMessagesAsync` + all `Handle*`/`Update*`/`Cancel*`/`SendReduce*` it calls) runs on a **thread-pool thread**. Hard-won rules:

1. **No WinForms control access on the receive thread.** Decisions read **engine backing fields** (`placedPrice`, `placedStopLossPrice`, `orderAmountVal`, `marketStopThreshold`, mirror fields, etc.); all display writes go through `UiInvoke(...)` / `Me.Invoke(...)`. Reading/writing a control off-thread throws (debugger) or is UB (no debugger). `AppendColoredText` self-marshals — logging is safe.
2. **Fields are the single source of truth.** The lagging exchange echo in `HandleOrderPositionUpdates` **seeds only when the field is 0** (`If placedPrice = 0D Then …`) — it must not overwrite an actively-managed price (that caused a reposition runaway/flood).
3. **`cancelPending` lifecycle** (`IsCancelPending()` self-clears after 4s): set in `CancelOrderAsync` (which also nulls `CurrentOpenOrderId/TP/SL`), cleared on the `cancelled` echo + at placement. The reposition gates check `Not IsCancelPending()` **before** `Interlocked.Exchange(isRepositioning,1)` — order matters (short-circuit before acquiring the single-flight guard, or it leaks and wedges repositioning).
4. **`HandleTokenRefreshResponse` id-guard must stay null-safe:** `If Not (messageId.HasValue AndAlso messageId.Value = 3) Then Return` — never `If messageId <> 3 Then Return`.
5. **`isRepositioning` single-flight guard** + connection guard (`IsWebSocketConnected AndAlso …`) on the reposition/edit blocks; `BackoffStopLossRetry` (not `lastStopLossUpdate = MinValue`) on SL-update failure (except `ForceStopLossUpdate`'s intentional bypass).

These are documented in memory `[[receive-loop-threading]]` and `[[ws-handler-id-guards]]`.

## 6. Component status

- **`frmMainPageV2.vb`** — the LIVE trading form (~4,300+ lines). God-form: WebSocket/auth/rate-limit/order-placement/SL-management/margin/trade-history. **Kept.** All fixes land here.
- **`frmMainPage.*`** — obsolete backup form. **DELETED** (housekeeping H2).
- **`FrmIndicators.vb`** — signal/auto-trade + backtest. **Being REPLACED** by a new analysis engine (separate effort; it has its own realtime backtest). Auto-trade / analysis / backtest are **kept only to preserve main-form connections** until the replacement lands. Scoring uses MACD(6,13,5)+Stoch(8,3,3) (rejected-list) + double-counts — the new engine drops these.
- **`AutoTradeSettings.vb`** — settings form; being **RE-CODED** for the new engine.
- **`TradeDatabase/TradeRecord/TradeAnalytics.vb`** — cleanest code (SQLite). Kept.
- **`AppSecrets.vb`** — new; loads credentials (§7).

**Why #2 & #7 testing is ON HOLD:** #2 (circuit breaker) and #7 (backtest) live in the to-be-replaced auto-trade/analysis/backtest modules. The code fixes are reviewed-clean, but runtime-testing them now is wasted effort — they wait for the replacement.

## 7. Credentials / secrets

- Loaded at runtime from git-ignored **`secrets.json`** (project dir, copied to output) via **`AppSecrets.vb`** (`AppSecrets.ClientId`/`ClientSecret`). Template: committed `secrets.example.json`. Loaded in `frmMainPageV2_Shown`.
- The key points to a **throwaway Deribit TEST sub-account**; repo is **public**; the secret is still in git *history* (pre-fix). Owner accepts this and will delete the sub-account at production cutover. **Production account must never be committed** — always use `secrets.json`.
- **Token lifetime = 1 year** (`expires_in=31536000`), so the in-app token-refresh path is effectively dormant in normal sessions — don't re-investigate "why no token refresh." (Memory `[[credentials-secrets-setup]]`.)

## 8. Outstanding work — the MEDIUM set (specced, not implemented)

Reprioritized for the replacement context (do NOT just implement all three blindly):

1. **#10 decouple — `spec-medium-decouple.md` — HIGHEST VALUE.** Define a clean public API on `frmMainPageV2` (`PlaceAutomatedOrder`, `SetAtrTargets`, `PlacedPrice`/`SessionPnLUSD` read-only, existing `IsWebSocketConnected`/`CanMakeAPIRequest`) + remove VB **default-instance** coupling (a real latent bug). It's the contract the new analysis module will target — best done before/with that module. Scope is the `frmMainPageV2` side only; **don't rewire the doomed FrmIndicators.** Opus, high.
2. **Gated housekeeping — `spec-medium-housekeeping.md` — low-risk, anytime.** `frmMainPageV2`-only cleanups: throw-then-dead-code, empty `ProcessEstimationData`/id-890 branch (keep id-777), duplicate account-summary fns, heartbeat-enabled-twice, magic-numbers→consts. Sonnet, medium.
3. **#9 dispatcher — `spec-medium-dispatcher.md` — HIGH-RISK, LOW-URGENCY, consider deferring/skipping.** Parse-once + dispatch is perf+tidiness only (no bug), and it rewrites the exact hot path that §5's invariants protect. The spec leads with "consider deferring" and lists every invariant to preserve. Opus, high/xhigh. Do last, on a stable receive loop, with full re-test — or skip until after the replacement.
4. **#11 stream-guard — DROPPED.** It was a reconnect fix in FrmIndicators (being replaced). Carried forward as a requirement for the new module (own a single guarded connect/reconnect loop).

**Also pending:** owner runtime testing of #5/#6 + cross-thread + transition-race (in progress).

**Two known edge cases deferred to #10** (from the transition-race review, harmless — rejected by Deribit): a very-late `open` echo after `cancelPending` clears could set a stale order ID for one tick; the triggered-`StopLossOrder` case sets `SLTriggered`/`PositionSLOrderId` unconditionally.

## 9. `docs/` map

- `CODE_AUDIT.md` — the full end-to-end audit (Critical/HIGH/Medium/Housekeeping). Secret redacted. *Tracked.*
- `spec-*.md` — implementation specs (high-bugs, housekeeping-now, cross-thread-fix, transition-race-fix, medium-decouple, medium-dispatcher, medium-housekeeping).
- `impl-report-*.md` — implementer reports (high-bugs, housekeeping-now, cross-thread-fix, transition-race-fix). `spec-back-high-bugs.md` was the (pre-impl) HIGH plan.
- `HANDOVER.md` — this file.

## 10. Immediate next actions for the new conversation

1. **Wait for the owner's runtime test results** on the current bundle. If something fails, ask for the `txtLogs` output and trace it (respect §5 invariants when diagnosing).
2. If testing passes: the owner will likely hand off **#10 (decouple)** to an implementer conversation, or start the new analysis module against that API. Support with review (§4 methodology).
3. Keep the memory files current (component status, any new invariants). Don't push. Don't reintroduce §5 violations.
