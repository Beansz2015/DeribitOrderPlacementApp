# Impl report — D1 fix verification (`docs/spec-harness-owned-window.md` §Coordinator review, R11.4)

**Seat:** Sonnet, medium — same conversation, continued. **Date:** 2026-08-07.
**Scope:** `tools/` only. No `.vb` file touched. No trade. Per the reviewer's instruction, the
60-round live acceptance was **not** re-run — only items 2, 3, 4, the new check script, the gate and
the censuses.

## 1. The fix

Commit `290af9e`. `Wait-ForMatchingElement` restructured so attempt 1 always runs unconditionally,
and the deadline stopwatch starts **after** it returns, not before. Full reasoning in the commit
message and in the function's own comment — this report covers verification only.

## 2. `tools/checks/test-wait-for-matching.ps1` — new, 5/5

Mocks `Select-MatchingElement` via scope-shadowing (dot-source `harness-common.ps1`, then redefine
the function — confirmed this shadows correctly for `Wait-ForMatchingElement`'s call, per the
reviewer's own verification). Cases: attempt-1 match (no sleep) · retries to attempt 3 · `Ambiguous`
on attempt 1 (no sleep) · timeout without hanging · **D1's own regression case**, a 600 ms first
attempt that still permits a retry. **All 5 pass.**

**Verified the regression case actually discriminates, not just that it passes:** stashed the D1 fix
(`git stash`), ran the check script against the prior commit's code, and case 5 **failed** —
`calls=1 Kind=None`, exactly D1's described failure. Restored the fix (`git stash pop`), re-ran: all
5 pass again, case 5 now `calls=2`. The test is proven to catch what it's named for, not just to be
green.

## 3. Acceptance re-verification (live app, TESTNET confirmed)

**Item 2 — `Ambiguous` still refuses instantly.** `set-textbox.ps1 Trig 999` → exit 3, same three
tied candidates as before. Instrumented directly against `Wait-ForMatchingElement`: still resolves on
attempt 1, `Kind=Ambiguous`, unaffected by the fix (the `Ambiguous`-returns-immediately path was
never inside the part of the function D1 touched).

**Item 3 — genuine not-found still fails loudly, and now genuinely retries.** `set-textbox.ps1
NoSuchControlXYZ test` → exit 2, full candidate list printed, same as before. Instrumented the real
`Wait-ForMatchingElement` (not a hand-copy of its logic — my first instrumentation attempt
accidentally re-implemented the OLD loop inline and had to be redone against the actual function):
elapsed rose from ~740–950 ms (pre-fix, one attempt only) to **~1289–1310 ms** (3 trials), consistent
with attempt 1's cold cost plus at least one genuine retry inside the post-attempt-1 budget before
timing out. This is the fix working, not a regression — a genuine not-found now costs more wall-clock
time than before, because it is actually using the retry window D1 restored.

**Item 4 — deny check and TESTNET/PID gate untouched.** `click-button.ps1 "Limit BUY"` → exit 3,
same refusal text. `git diff 689a812 HEAD -- tools/click-PLACES-ORDER.ps1 tools/click-button.ps1` →
**empty** — neither file has changed since before D1; the fix is confined entirely to
`harness-common.ps1`.

## 4. Gate and censuses

`tools/checks/verify-gate.ps1` → **GATE PASSED**, OrderCheck **268/268**. Censuses on
`frmMainPageV2.vb` (still untouched): `emergencyFired` 10 · `IsATRSlippageExcessive` 8 ·
`NextSlBackoff` 2 · `RecordCommandedSLPrice` 3 · `slUpdateFailures = 0` 1 · `TakerFeeRate` 0 ·
`isPlacingOrder` 13 · `lastPlacementAdmittedUtc` 13 · `placedOrderSizeUsd` 18 = **68**, unchanged.
Counted by occurrence (`-o`), not by line, per the trap recorded last pass.

`tools/checks/test-select-best-match.ps1` → 7/7, unaffected.

## 5. What this pass did NOT do

- Did not re-run the 60-round live acceptance (item 1) — instructed not to; D1 does not invalidate
  it, since the shipped behaviour before this fix was at worst the pre-fix code's single-attempt
  path, which is what acceptance-1 exercised and passed against.
- Did not re-measure the cold-process single-query cost (~740–950 ms) itself — taken as established
  from the prior pass and from this pass's own item-3 timings, which are consistent with it.

## 6. Repo state

Commit `290af9e` (the fix + check script) precedes this report. App launched fresh for this
verification pass and stopped cleanly afterward (`tools/stop-app.ps1`). Nothing pushed.
