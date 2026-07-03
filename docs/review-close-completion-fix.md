# Review — spec-close-completion-fix.md (coordinator verdict: APPROVED with amendments)

**Date:** 2026-07-03. Reviewed against the code at `4e1d751`.

## Verdict

**The diagnosis is verified and the design is correct — implement it.** I confirmed the structural claims in the code: the orders gate (`If orders IsNot Nothing AndAlso orders.Count > 0`, :1967) wraps the *entire* close-accounting path including the `size = 0` branch, and the fill's P/L lives in per-echo locals — so a closing fill and a flat-position update arriving in separate echoes lose the close silently. This failure mode was flagged as a theoretical edge in the original audit ("if they arrive in separate messages… not recorded") and has now been observed live; it is pre-existing and independent of restore hardening, as the spec states. The pendingClose-fields + transition-trigger design handles all four orderings correctly (co-echo, split, duplicate-flat, reduce-then-later-flatten), and putting the completion call *after* the orders block guarantees the co-echo case reads freshly-written fields.

## Amendments (implement with these)

1. **`OpenPositions = False` cannot move into the helper.** It is a per-echo *local* of `HandleOrderPositionUpdates` — unreachable from `CompletePositionClose`. It has no reader after the positions loop, so **drop the line entirely**; do **not** promote it to a field (it would confuse the per-echo protocol).
2. **Fields-only, not dual-write.** `ApplyCloseFill` should write the `pendingClose*` fields and **drop its five output ByRef parameters** (`porLAmt`, `porL`, `label4DB`, `closedAmountUSD`, `closedWasLong`) — their only readers move into the helper, and keeping both copies invites drift. `execPrice` stays as an input parameter. Remove the five now-dead locals from the per-echo `Dim` block (keep `ExecPrice`).
3. **Message/record gating:** `If pendingCloseValid` replaces `If label4DB IsNot Nothing` (equivalent by construction — `ApplyCloseFill` always set `label4DB` and now always sets the flag). Preserve the fix-7 three-way structure: profit / loss on `pendingClosePorLAmt > 0`, scratch on `pendingCloseValid` with amount 0, all followed by the DB record; the fallback "Position closed." (no record) only when `pendingCloseValid = False`.
4. **Reverse-split** (flat echo before its fill echo) is causally implausible on the raw channel (the fill causes the flat) and its residue is already neutralized by the 0→≠0 stale-capture clear — no code change; record it as known-theoretical in the impl report.
5. **Test plan addition:** after this lands, **re-run restore-hardening test 1 end-to-end** (restart with position → SL trigger → trailing → close) — that scenario exposed this bug, and the two changes must be validated together. Keep the spec's tests 1–6 as written.

## Also recorded

Restore-hardening as-built (`0d078eb`/`310de5f`/`c6a893c`/`4e1d751`) spot-verified on its safety-critical claims: zero-baseline guards present at both `UpdateStopLossForTriggeredStopLossOrder` branches (:3236/:3242, stacked with the audit-2 checkbox gate) and both quote-handler force-paths (:1566–:1607); `SetTradeMode` restore at :4095; position-sized SL edits at :3275. The spec-back reconstruction matches the code everywhere probed; its threading deviation (engine fields written directly on the receive thread, displays-only marshalled) is invariant-stricter than the legacy echo handler — approved.
