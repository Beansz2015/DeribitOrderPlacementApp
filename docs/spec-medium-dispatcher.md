# Implementation Spec — MEDIUM #9: parse each inbound message once, then dispatch

**Severity:** 🟡 Medium — **but read §1 first: this is now the highest-risk, lowest-urgency item in the set.** It's a cleanup/perf refactor of the hot path that the HIGH, cross-thread, and transition-race fixes just carefully tuned. No bug is being fixed here.
**Source:** `docs/CODE_AUDIT.md` #9.
**Project:** DeribitOrderPlacementApp — .NET 9 WinForms, VB.NET. `Option Strict Off` on legacy files.
**Start point:** `housekeeping-now` tip. Build green.

> ⚠️ Locate code by symbol; line numbers drift.

---

## 1. Recommendation before you start: consider deferring

Since the audit, three fixes have layered carefully-tuned logic onto the exact handlers this refactor restructures:
- **HIGH:** #3 token-refresh id-guard, #5 reposition single-flight, #6 hot-path parse safety.
- **Cross-thread fix:** engine fields + `UiInvoke`; no control access on the receive thread.
- **Transition-race fix:** `cancelPending` gates, seed-only writers, echo suppression.

#9's payoff is **perf + tidiness** (parse each message once instead of ~9×, remove silent empty `Catch` blocks) — not a correctness fix. Its cost is **real regression risk** across code that now works. Given the app is functioning and the analysis module is being replaced, **the sensible order is: do #10 and the housekeeping first, finish runtime testing, and treat #9 as optional / last** — or defer it until the receive loop is otherwise stable. If you (owner) decide the 9× parse isn't worth the risk right now, skipping #9 is a legitimate call. This spec is here so it's ready when/if you want it.

## 2. The change

`ReceiveWebSocketMessagesAsync` currently passes the raw response **string** to nine handlers, each of which calls `JObject.Parse(response)` itself:
`HandleHeartbeat, HandleQuoteUpdates, HandleIndexUpdates, HandleBalanceUpdates, HandleOrderPositionUpdates, HandleTokenRefreshResponse, HandleAccountSummaryResponse, HandleRateLimitError, HandleMarginEstimationResponse`.

Refactor to: **parse once** in the receive loop, then **dispatch** the parsed `JObject` to a single router that routes on `params.channel` / `id` / `method` and calls only the relevant handler. Convert handlers to accept the parsed `JObject`. Remove the now-redundant per-handler `Parse` and the empty/silent `Catch` blocks (replace with one logged catch at the dispatch level).

## 3. Hard constraints — preserve everything (this is a plumbing change, NOT a logic change)

The handler **bodies** must keep their current behaviour exactly. In particular, do not disturb:
1. **Cross-thread discipline** — handlers read engine **fields**, never controls; all display writes stay inside `UiInvoke`/`Me.Invoke`. Don't reintroduce any control access on the receive thread.
2. **`HandleTokenRefreshResponse` null-safe id-guard** — keep `If Not (messageId.HasValue AndAlso messageId.Value = 3) Then Return` (a naive `<> 3` early-return re-breaks on id-less messages — that regression already happened once).
3. **`HandleQuoteUpdates`** — the `isRepositioning` single-flight, the `Not IsCancelPending()`-before-`Interlocked.Exchange` ordering (must short-circuit before acquiring the guard), the connection guard, and the runaway-safe `placedPrice`/`placedStopLossPrice` field advances.
4. **`HandleOrderPositionUpdates`** — the `cancelPending` echo suppression (`If Not cancelPending` wrapping + `untriggered` `If cancelPending Then Return`), seed-only field writes, and the `cancelled`-branch clear.
5. **Routing semantics** — several handlers check an `error` field; keep those. Match today's routing exactly (same channel/id/method → same handler). A message that matches nothing is a no-op (as today), not an error.

**Dispatch keys (current):** `params.channel` ∈ {`quote.BTC-PERPETUAL`→quotes, `deribit_price_index.btc_usd`→index, `user.portfolio.btc`→balance, `user.changes.BTC-PERPETUAL.raw`→orders/positions}; `method = "heartbeat"`→heartbeat; `id` ∈ {3→token refresh, 999→account summary, 777→position (margin resp handler), 890→margin est}. Verify each against the code — don't trust this list blindly.

## 4. Acceptance / test plan (owner, test sub-account, under the debugger)

Because this touches the hot path, re-run the **full** cross-thread + transition-race + #5/#6 test set, plus:
- Quotes, index, balance/equity/session, order/position updates, heartbeat indicator, rate-limit handling, token refresh path — all still work.
- Place → reposition → cancel/slippage → SL trigger: no cross-thread errors, no reposition runaway, no duplicate repositions, no post-cancel repositions (i.e. none of the earlier symptoms return).
- CPU on the receive path is no worse (ideally better — one parse per message).
- Build green (0 errors) per commit.

## 5. Ground rules

Implement directly; commit locally per logical step (the dispatcher scaffold, then handler-by-handler signature change, so each commit builds and is bisectable); **do NOT push**; preserve all behaviour per §3; note anything else for later. Produce the standard **implementation report** — dispatcher design as built, the routing table, per-handler before→after signature, explicit confirmation that each §3 item was preserved (quote the key lines), build/commits, and the full re-test results.

**Suggested model/effort: Opus 4.8, high (or xhigh).** Highest-risk refactor in the set — it must preserve three layers of prior fixes on the busiest code path. Not a place to economise.
