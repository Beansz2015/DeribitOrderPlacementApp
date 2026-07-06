# Session spec-back — 2026-07-07 (SL reconciliation live → post_only edits → review + probe CLOSED)

**For:** the orchestrator and any implementer of a pipeline spec touching the chase/echo/edit regions (`spec-entry-chase-v2.md` queues on exactly these lines). **What this is:** the as-built record of this session's **four code commits**, the owner's **runtime test matrix (all passed)**, the **code review** of those changes (one accepted finding, the rest verified clean), and the **CLOSED `reduce_only` question**. Session range: `17fffee → HEAD`; code commits `968b26d`, `a1d7b58`, `da25365`, `45a8da5` (interleaved orchestrator docs `adb506c`…`c303c19`). File: `DeribitOrderPlacementApp/frmMainPageV2.vb`. Build **0/0** throughout. **Nothing pushed.** Line anchors are as of this HEAD — grep the symbols, they drift.

---

## 0. Code commits

| Commit | What | Docs |
|---|---|---|
| `968b26d` | Triggered-SL reconciliation (spec 4a + P1): commanded-price set, manual-edit discriminator, `emergencyBaseline` moved under it | `impl-report-` / `spec-back-reconcile-manual-sl-edits.md` |
| `a1d7b58` | `already_closed` on edit ids 223344–223350 downgraded red→gray (benign chase race, observed trade #36) | (commit msg) |
| `da25365` | Trigger-flip false-positive fix: flip = silent **adopt**, discriminator only post-flip (observed trades #39/#40) | amendment 1 in both reconcile docs |
| `45a8da5` | `post_only: True` + `reject_post_only: False` added to **all 8** `private/edit` payloads (maker guarantee across the order lifecycle) | `impl-report-post-only-edits.md`, `spec-back-post-only-edits.md` |

## 1. As-built behavior contract (cumulative)

**Open `StopLossOrder` echo — three-way classification** (reconciliation block, `~:2123`, under `Not cancelPending`; `wasTriggered` captured before `SLTriggered = True`):

1. **Trigger flip** (`Not wasTriggered OrElse emergencyBaseline = 0D`) → **adopt** the exchange's triggered price into `placedStopLossPrice` + `emergencyBaseline` + display, **silently**. Covers trigger-only pre-trigger moves (never "manual"); guarantees the baseline seeds even on a null-price flip.
2. **Already triggered + price ≠ reference + NOT in the commanded set** → **manual edit**: follow it (both references + display), log `Manual SL edit: $X` (cyan). P1: the chase keeps running from the corrected reference.
3. **Else** (commanded or unchanged) → ignore (runaway/transition-race protection).

**References move together post-trigger** — exactly three write sites for the pair: the flip adopt, the chase reposition (`placedStopLossPrice`/`emergencyBaseline = newStopPrice`, `~:1665–1666`), and the manual-edit acceptance. *(Orchestrator review note: a grep for writers finds a fourth — the id-778 restore snapshot seeds `emergencyBaseline` when 0 (`HandleOpenOrdersSnapshot`, `~:4240`), for a restart with an already-triggered SL. It's a seed-only-when-zero restore site, not a post-trigger move; listed so nobody thinks the inventory above is stale.)*

**Commanded-price set:** recorded at the single SL-edit send point (`UpdateStopLossForTriggeredStopLossOrder`, after the send — all chase/emergency paths route through it); lock-guarded; ~2 s window, 0.25 match tolerance (half a tick); cleared at the 7 SL-context reset sites (the `emergencyBaseline = 0` anchors).

**Maker lifecycle:** every limit order carries `post_only: True` + `reject_post_only: False` at placement **and** on all 8 edit payloads (223344–223350 across 6 paths). The only takers are the market entry (`btnMarket`) and the market reduce / M.SL emergency — both `private/buy`/`sell`, structurally outside the edit paths.

**Logging:** `already_closed` on an edit id → gray `Order edit skipped…` note; every other edit error stays red.

## 2. Owner runtime matrix (test sub-account — ALL PASSED)

| Test | Trade | Result |
|---|---|---|
| Trigger→chase→fill regression | #36 | Correct maker close; surfaced the benign `already_closed` chase race → `a1d7b58` |
| Trigger-only move pre-trigger | #39/#40 | Initially false `Manual SL edit` → `da25365`; re-test clean (no manual line) |
| Limit moved **down** post-trigger (long) | — | Chase pulled the SL to best ask — **expected P1** (documented downside, accepted) |
| Limit moved **up** post-trigger (long) | #44 | `Manual SL edit` detected, then M.SL emergency market close — **expected** (baseline follows live SL; wrong-side placement arms the gap). **Owner decision: leave the emergency as-is** (clamp considered and rejected) |
| Clean chase + **flags probe** | #45 | SHORT, 5 repositions 63686→63729.5, no spurious manual edits, no backward blips, maker fill; **order carried BOTH `post_only` and `reduce_only` after the edits** |

## 3. `reduce_only` question — CLOSED

Per the orchestrator's decision path (`spec-back-post-only-edits.md` §5, "preserved → no code change… question closed") and the trade-#45 probe (§6 there): **Deribit preserves `reduce_only` across `private/edit` when omitted** (observed across five consecutive chase edits). **No `reduce_only` micro-commit; nothing blocks `spec-entry-chase-v2.md` on this.** Standing rules unchanged: `reduce_only` stays placement-only; never on the entry (223344) or TP (223345 — the `:2855` both-legs-cancelled landmine); it remains load-bearing as the cap for the three over-ask paths (triggered-SL chase, reduce limit, reduce market — all size to `Math.Abs(positionSizeUSD)`).

## 4. Code-review findings (this session's changes; owner-requested review)

**F1 — `emergencyBaseline` phantom-advance on a failed chase edit (LOW, real, accepted).** `emergencyBaseline = newStopPrice` (`~:1666`) runs unconditionally after the awaited send returns, but `UpdateStopLossForTriggeredStopLossOrder` swallows failures (async JSON-RPC rejections don't throw; early-returns don't throw). On a rejected edit both references advance — and `SL repositioned` logs — for a move that never landed. The `placedStopLossPrice` half is the **pre-existing** runaway-fix trade-off; the baseline half is **new** (before `968b26d` the baseline was echo-driven and stayed true on a failed edit). Effect: the emergency measures from one chase-step closer to the market → fires one step later. Self-corrects on the next successful edit or any echo; the rejection itself is now visibly logged. **Accepted as-is; backlog candidate:** state re-sync on edit rejection (rollback class — same deferral family as the Audit2 F2 logging-only rule).

**F2 — commanded-set ordering under fire-and-forget dispatch (VERIFIED, docs nuance).** The receive loop calls `HandleQuoteUpdates` (an `Async Sub`) without await, so post-send continuations (record → advance) run on threadpool threads while the loop keeps reading. The record-before-echo guarantee holds because the record runs at local send-complete while the echo needs a full network round trip after that same send — a ~RTT margin, not a hard synchronization. (The impl-report's "echo can only arrive after the send completes" should be read this way.)

**F3 — reconnect self-heal labeled "Manual SL edit" (COSMETIC, leave).** An edit lost to a disconnect leaves the reference advanced past the true SL; the first post-reconnect echo corrects it via the manual path (commanded set expired) and logs `Manual SL edit` though nobody touched anything. Correct state, misleading label; indistinguishable from a real manual edit, so not worth renaming.

**Verified clean:** flip-adopt race safety (no chase can be in flight at the flip — the chase gates on `SLTriggered`, still False for any pre-flip continuation; flip + adopt are one synchronous `Invoke` block); backward-walk protections (single TCP stream can't reorder; id-778 snapshot is seed-only-when-zero; duplicate flip echo is an equality no-op); reset coverage 7/7; threading (SyncLock both sides, no `Await`/`Invoke` inside the lock, UI writes inside `Me.Invoke`, baseline write in the accepted torn-read class); both taker paths untouched; `already_closed` downgrade correctly scoped (edit ids + exact message; all else stays red).

**Pre-existing observations (NOT from this session, not worsened — orchestrator backlog fodder):** the triggered-SL chase has no `isRepositioning` single-flight guard (overlapping duplicate edits possible when quotes outpace the send RTT; the commanded set absorbs their echoes); `SL repositioned` logs even when the edit fails async (the F1 sibling); `btnEditSLPrice` sends `trigger_price` against a possibly-already-triggered SL via the `PositionSLOrderId` fallback (likely rejected — probe someday); id 223346 is shared by two paths (log-attribution ambiguity only).

## 5. Coordination notes

- **`spec-entry-chase-v2.md` is unblocked** — the reduce_only contingency resolved to no-op; no micro-commit ahead of it. Its target lines (the edit payloads + chase block) now carry the `post_only` flags — any reworked payload must keep them.
- **Any new `private/edit` or limit placement** must carry `post_only: True` + `reject_post_only: False`; never add `post_only` to the two market paths, never `reduce_only` to entry/TP.
- **Backlog candidates raised this session:** F1 edit-rejection re-sync; SL-chase single-flight guard; `btnEditSLPrice` trigger_price-on-triggered probe; P3 hybrid chase policy (owner-deferred, trigger = P1 pullback annoying in live use); TP post-fill manual-move gap (reconcile spec §7, deferred).
- **Owner decisions on record this session:** emergency stays as-is (no manual-edit clamp); P2 rejected outright; P1 confirmed live.

Related: `spec-back-reconcile-manual-sl-edits.md` (+ amendment 1), `impl-report-reconcile-manual-sl-edits.md` (+ amendment 1), `spec-back-post-only-edits.md` (§5 orchestrator decision, §6 probe result), `impl-report-post-only-edits.md`, `spec-back-session-2026-07-04.md` (prior session).
