# Impl report — `post_only` rejected on pre-fill OTOCO secondary edits (fix)

**Origin:** owner runtime trades #53/#56 (2026-07-08). **Implementer:** Fable, direct fix (owner said fix, not spec). **Commit:** see git. **Build:** 0/0.
**Not a spec-driven change** — a clear Deribit-rejection bug with an obvious fix; the owner authorised the direct patch.

## The bug

Deribit rejects `post_only` as a `private/edit` parameter on a **PRE-fill OTOCO child (secondary) order**:
```
API ERROR (id 223344 order edit): code -32602 - Invalid params | {"reason":"post_only not allowed for secondary orders","param":"post_only"}
API ERROR (id 223346 order edit): code -32602 - Invalid params | {"reason":"post_only not allowed for secondary orders","param":"post_only"}
```
It fires on the **full-bracket entry re-anchor** (`UpdateLimitOrderWithOTOCOAsync`), which runs when the entry chases *beyond* the drift bound `Min(TP, Trig)/2` and edits the TP + SL child legs. Arithmetic confirms it: trade #56 (Trig 20 → bound 10) errored on the 11.5-drift reposition; trade #53 (Trig 60 → bound 30) errored only past drift 30. Ids: **223344** = TP edit, **223346** = SL edit; the primary **223344 main** edit succeeds (the reposition still happens), which is why only the two secondary edits error.

**Pre-existing:** introduced by `45a8da5` (post_only on all edits); entry-chase v2's entry-only default hid it until a chase drifted past the bound. **Impact:** error spam + the pre-fill TP/SL legs silently don't re-anchor on a far chase (they stay at placement geometry — drift-bounded-safe; the TP is fixed post-fill anyway). No money bug.

**Why `post_only` is accepted elsewhere:** at *placement* it's a valid `otoco_config` field; on *post-fill / triggered / primary* edits it's accepted (trade #45 = triggered SL 223350 OK; trades #52/#55/#56 = post-fill TP re-anchor 223344 OK). It is specifically the **pre-fill secondary edit** that Deribit refuses.

## The fix

`SendRateLimitedUpdate` gained `Optional postOnly As Boolean = True`; when False it omits both `post_only` and `reject_post_only` from the payload (also refactored to build `params` once instead of two near-duplicate blocks). Callers:

| Edit | id | Order role | post_only |
|---|---|---|---|
| `UpdateLimitOrderWithOTOCOAsync` main | 223344 | primary entry | **keep** (default) |
| `UpdateLimitOrderWithOTOCOAsync` takeprofit | 223344 | pre-fill secondary | **omit** (`postOnly:=False`) |
| `UpdateLimitOrderWithOTOCOAsync` stoploss | 223346 | pre-fill secondary | **omit** (`postOnly:=False`) |
| `UpdateStopLossForTrailingOrder` main | 223347 | primary (trailing entry) | **keep** |
| `UpdateStopLossForTrailingOrder` SL | 223348 | pre-fill secondary | **omit** (inline payload, flags removed) |
| `ReanchorTPToFillAsync` | 223344 | post-fill TP | **keep** |
| `SendReduceRepositionEdit` | 223349 | standalone reduce (not OTOCO child) | keep |
| `UpdateStopLossForTriggeredStopLossOrder` | 223350 | triggered/active SL | keep |

**Maker guarantee preserved:** the legs are created `post_only` in `otoco_config`, and Deribit preserves flags across an edit that omits them (trade #45). The TP/SL sit far from the book so they can't take regardless. So omitting the flag on the edit is both **required** (else -32602) and **safe**.

## Docs amended (same change)

The "all 8 edit payloads carry `post_only`" maker-lifecycle invariant is now false for pre-fill secondary edits. Updated: `HANDOVER-2.md` §3 STATE-DELTA-2026-07-07 and `spec-back-session-2026-07-07.md` §"Maker lifecycle" — both now state the pre-fill-secondary exception explicitly.

## Residual to watch (NOT fixed — not observed)

`btnEditTPPrice_Click` (223345) and `btnEditSLPrice_Click` (223346) — the manual TP/SL edit buttons — still send `post_only`. In practice they're used **post-fill** (in position), where it's accepted. If a user ever edits the TP **before** the entry fills, that edit would hit the same -32602. Not fixed here (out of the observed paths, user-initiated, rare); flagged for the orchestrator if it wants full coverage.

## Owner runtime test

Re-run a **far-chase** entry (small Trig so the bound is tight, or place well off the market): the entry repositions past the bound with **no** `-32602`, and the TP/SL legs re-anchor. Then a normal in-position lifecycle (chase, trigger, exit) with no unexpected taker fills.
