# Implementation Spec — MEDIUM gated housekeeping (frmMainPageV2 plumbing cleanups)

**Severity:** ⚪ Low-risk cleanups (behaviour-preserving). Independent of each other; do anytime after the current runtime testing.
**Source:** `docs/CODE_AUDIT.md`. This file used to also carry #9/#10/#11 — those are now:
- **#9 (parse-once dispatcher)** → `spec-medium-dispatcher.md` (high-risk; consider deferring — see that spec).
- **#10 (decouple via clean API)** → `spec-medium-decouple.md` (highest-value; the contract for the new analysis module).
- **#11 (indicator-stream reconnect guard)** → **dropped.** It lives in `FrmIndicators`, which is being replaced; the new analysis module must own a single guarded connect/reconnect loop from the start (carry-forward requirement for that module's spec, not worth fixing in throwaway code).

**Project:** DeribitOrderPlacementApp — .NET 9 WinForms, VB.NET. `Option Strict Off` on legacy files.
**Start point:** `housekeeping-now` tip. Build green.

> ⚠️ Locate code by symbol; line numbers drift. All in `frmMainPageV2.vb` unless noted.

---

## Items (each an independent commit; all behaviour-preserving)

1. **`throw` followed by dead code** — `AuthorizeWebSocketConnection` has `Throw New Exception("Authorization failed…")` / `"Refresh token not found…"` immediately followed by an unreachable `AppendColoredText`. Keep the throw **or** the log (pick what fits), delete the dead line. Audit the rest of the auth path too (the HIGH #3 rewrite of `RefreshWebSocketAuthentication` likely already removed its versions — verify).

2. **Empty stub `ProcessEstimationData` + the id-890 branch** in `HandleMarginEstimationResponse` — remove if confirmed unused (nothing sends id 890; verify by reference search). **Preserve the id-777 live-position path** (that one is live).

3. **Duplicate functions** `GetAccountSummaryLimits` vs `GetAccountSummaryLimitsWithTimeout` — consolidate to one (keep the timeout version); update both callers (`InitializeRateLimits`, `InitializeRateLimitsAfterAuth`). Verify behaviour parity first.

4. **Heartbeat enabled twice** per connect — `EnableDeribitHeartbeatEnhanced` is called in the connect sequence (`ConnectToWebSocketDirectly`) **and** inside `AuthorizeWebSocketConnection`. Keep one (the connect-sequence call is the clearer home). Confirm heartbeat still starts on connect + reconnect.

5. **Magic numbers → named constants** (`frmMainPageV2` only; same values, no behaviour change): taker fee `0.0005` (`HandleIndexUpdates`), reposition leeways `±3` / `±5` / `0.5` (`HandleQuoteUpdates`). Promote to named `Const`. **Skip the `FrmIndicators` magic numbers** (`4320` bar cap, score `/21`) — that module is being replaced.

> Not here: converting the receive-loop `Async Sub` handlers to `Async Function … As Task` — that belongs with the #9 dispatcher refactor (`spec-medium-dispatcher.md`), not this cleanup.

## Acceptance

- Build green (0 errors) after each item. Items 1/5 are pure no-ops (build-check only). Items 2/3/4 touch the connect/receive path — a quick connect + data-flowing smoke test confirms no regression (no missing heartbeat, rate-limit init still runs, margin/position display for id-777 still updates).
- No cross-thread access reintroduced (keep the field/`UiInvoke` discipline).

## Ground rules

Implement directly; one local commit per item; **do NOT push**; 0-error build per commit; behaviour-preserving. Produce a short **implementation report**: per-item before→after + reference-search evidence for the "confirm unused" items (2, 3), build/commits, and the smoke-test result.

**Suggested model/effort: Sonnet is fine, medium effort** — these are mechanical, low-risk, and independent. (Use Opus only if bundling them with #9/#10 in one conversation.)
