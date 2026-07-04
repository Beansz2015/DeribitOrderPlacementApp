# Handover — implement the triggered-SL reconciliation fix (2026-07-04)

**For:** a fresh Opus conversation continuing this work. **You are picking up mid-stream**; the diagnosis and decisions are done, the implementation is not.

## Your task, in one line

Implement `docs/spec-reconcile-manual-sl-edits.md` (status: APPROVED, decisions locked: **4a commanded-price-set discriminator + P1 keep-chasing**). It fixes a **confirmed, runtime-reproduced bug**: after the SL triggers, the app's `placedStopLossPrice` freezes at the placement value and never follows the live SL (manual exchange-side edits), so the maker chase silently never fires and the taker market-emergency fires instead.

## Read first (in order)

1. `docs/spec-reconcile-manual-sl-edits.md` — the spec of record. §2 = defect, §4a = discriminator, §5 = what an accepted echo does, §10 = locked decisions, **§11 = implementation notes with the exact anchors**. The runtime-confirmation paragraph near the top has the DIAG proof (`sl` frozen at 62656.5 while the live SL moved to 62642; short chase needed `bestBid > 62661.5`, never reached).
2. `docs/spec-back-session-2026-07-04.md` — everything I changed in this whole session (for you AND the orchestrator, since other pipeline specs touch these files). Read the "current state of the touched code" section so you don't fight my changes.
3. This file (ground rules below).

## The fix in brief (all in `frmMainPageV2.vb`)

- `emergencyBaseline` ALREADY follows the live SL correctly (via the ungated open `StopLossOrder` echo, `~:2062`). Model `placedStopLossPrice` on the same idea but **gated by the commanded-price set** so a lagging echo of the app's *own* reposition can't reset it backward (that's why the current seed-if-zero guard at `~:2083` exists — don't just delete it).
- Add a commanded-price set (small ring / ~2s window). Record every SL price the app itself sends (the chase reposition at `~:1627` `placedStopLossPrice = newStopPrice`; consider the emergency `ForceStopLossUpdate` path too).
- Open `StopLossOrder` echo: if the echo `price` is NOT in the set ⇒ manual edit ⇒ set `placedStopLossPrice = price`, mirror `txtPlacedStopLossPrice`, set `emergencyBaseline = price`, log `Manual SL edit: $X` (cyan). If in the set ⇒ ignore (today's behavior).
- Reset the commanded set wherever `emergencyBaseline` resets — grep `emergencyBaseline = 0` for the 7 anchors (4 placement sites + `CompletePositionClose` + nuclear cancel + market-reduce) and mirror.
- **P1:** after this, the chase runs off the corrected `placedStopLossPrice` — no policy code needed beyond making the reference correct.

## Verify

- Build `dotnet build DeribitOrderPlacementApp.sln -c Debug` = **0 errors / 0 warnings** before every commit (VB; note the Debug config quirk writes to `bin\Release\` — harmless).
- Re-add the temporary DIAG from commit `04d708a` (two `[DIAG]` lines in the chase block) if you want to confirm during your own reasoning; **revert it before finishing**.
- Owner runtime-tests on a Deribit test sub-account; **do NOT declare done or push.** Owner is the only pusher.

## Ground rules (binding, from this project)

- Commit per fix to `master` (this repo has only `master`; owner pushes at tested milestones). Local-first; never push.
- End commit messages with `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- Receive-thread touches engine fields only; controls via `UiInvoke`/`Me.Invoke`/`AppendColoredText` (all now `IsHandleCreated`-guarded — see spec-back). Seed-only-when-zero echo writers. Scoped cancel must NOT touch SL/position legs (invariant #3).
- Write an `impl-report-reconcile-manual-sl-edits.md` and a `spec-back-reconcile-manual-sl-edits.md` when done, committed alongside.

## State at handover

- HEAD `2381766` on `master`. Build 0/0, working tree clean, nothing pushed.
- This session's work = `0d078eb..2381766` (16 commits) — all summarized in the spec-back. Owner has runtime-tested and PASSED: restore hardening, TradeMode restore, close completion (Trade #28/#29/#31/#33/#35 recorded), handle-race (no restart crash), emergencyBaseline follow-live-SL. The ONLY open item is this reconciliation fix.
