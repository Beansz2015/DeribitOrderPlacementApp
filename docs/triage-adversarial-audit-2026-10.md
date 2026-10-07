# Triage — the adversarial audit's order-app findings (coordinator, 2026-10-07)

**Sources (read, not copied):**
- Order-path audit: branch `claude/gracious-fermat-w8sacs`,
  `docs/audits/2026-09-24-signal-to-exchange-order-path.md` — findings 1–12, here "M9 F*n*".
- Stop-lifecycle audit: branch `claude/zen-cray-wy33a6`, `docs/audits/2026-09-25-order-app-gap.md` —
  findings G1–G20, here "G*n*". Proof harness at `docs/audits/proofs/order-app-gap/` on that branch.
- Ranking and options: engine repo, branch `claude/great-keller-s5f4gp`,
  `docs/adversarial-audit-2026-09-24.md` §A, §B Band A (rows A1–A22), §C (C-1, C-7, C-15 to C-18).
  Row IDs "A*n*" and decision IDs "C-*n*" below are that report's.

**Pin:** the audits pin the app at `8232e9e`. HEAD is 26 commits later. Line numbers moved; the code
paths did not, except where §2 says so.

**Owner rule (relayed by the audit's orchestrator):** anything that changes live order behaviour
needs the owner's ruling before it ships.

---

## 1. Re-verified at HEAD (2026-10-07, by grep and read)

| Row | Finding | Still present? | Evidence at HEAD |
|---|---|---|---|
| A12 | G1 — emergency cancels first, logs "Executed" unconditionally | ✅ yes | `frmMainPageV2.vb:4895`, `:4897`, `:4905`, `:4907` |
| A13 | G2 — no `rejected` branch on the order-state chain | ✅ yes | chain at `:3263` open / `:3494` untriggered / `:3538` filled / `:3649` cancelled — no `rejected` |
| A14 | G3 — `first_hit`; TP leg not reduce-only | ✅ yes | `:4277`; TP `reduce_only` commented out at `:4311` |
| A15 | G4 — reconnect gives up after 10; no remote page on disconnect | ✅ yes | `maxReconnectAttempts = 10` `:1407`; `RemoteNotifier.Post` only at startup and the emergency |
| A16 | G6 — side from `TradeMode` | ✅ yes | 18 `If TradeMode` sites |
| A19 | G9 — no quote-silence watchdog | ✅ yes | no last-quote timestamp exists |
| A20 | G10 — empty order list read as a flat restart | ✅ yes | `:5959` |
| others | | carried from the audits, **not re-checked** | |

## 2. Changed since the pin

| Row | Status now |
|---|---|
| A21 / G11 (fallback ATR understated) | ✅ **Gone.** FrmIndicators is retired (`docs/review-frmindicators-retirement.md`). The fallback is now the Flat ATR. |
| M9 F11 (in-app placement failures reported as `rejected: timeout`) | 🟡 **Specced:** `docs/spec-chase-anchor-reset.md` §2.3. The audit adds that the staged signal tag survives and can attach to the next manual trade; folded into that spec as §2.4 on 2026-10-07. |
| A10 / M9 F5 (risk sizing uncapped when `max_size_usd` ≤ 0) | ⚠ **Owner-built feature.** Max Size = 0 means NO CAP by owner ruling (`docs/spec-max-size-uncapped.md`). It announces itself in yellow. Owner to confirm it stands as accepted risk. |
| A21 / G14 (gate config fails open) | Partly: the eight settings boxes now snap back to the value in force. The persistence and session-rule parts are unchanged. |

## 3. Two interactions the audit did not weigh

1. **`A14` blocks `N2 LIVE`.** The engine report's C-17 says a one-contract cap "costs nothing at the
   shipped 10 USD". Not here: risk sizing (`N2`) produced would-act sizes of **80 and 90 USD** in the
   log-only soak (`bridge-dispositions.log`, 2026-08-14). Any size above 10 USD reaches the partial-fill S0.
2. **C-17(a)'s fix may re-open a worse hole.** The TP leg was made non-reduce-only on purpose.
   `frmMainPageV2.vb:4312`: *"putting reduce_only in take profit will cause cancellation of both take
   profit and stop loss orders when stop loss trigger is hit, leaving a position open with no stop
   loss."* That claim must be tested on testnet (X-1(f) below) before any reduce-only TP ships.

## 4. Proposed sequence

| Batch | Contents | Needs | Model, effort |
|---|---|---|---|
| **0 — now, no code or one guard** | Live size cap of 10 USD (C-17(b)), **or** an owner hold on `N2 LIVE` and Amount > 10 | Owner ruling | Opus, high, if coded |
| **0 — experiments** | X-1(a)–(e) from the audit, plus **X-1(f)**: does a reduce-only TP cancel both legs when the SL triggers? | Testnet; owner-driven, or a harness session through `tools/place-and-verify.ps1` | Opus, high |
| **1 — targeted guards** | A12 (reduce first, cancel on confirmed flat, re-arm on failure, log only what is confirmed) · A13 (`rejected`/`cancelled`-leg branch + alert + drop the dead leg from the feedback) · A15 (retry forever, capped back-off, remote page on disconnect and while down; auth-reply timeout, G16) · A16 (side from `positionSizeUSD` sign) · A20 (restore checks the position has a stop; alert) · the anchor-reset spec already written | Owner ruling (live order behaviour) | Opus, high |
| **2 — position-level protection** | Position-keyed loss cap scaled to the signal's stop or ATR (C-16(b), A18) · 1–2 s quote-silence watchdog that stands the bridge down and alerts (A19) · stop-side check at the fill (C-1 app half, A1) · stand-down cancels the working entry (C-18(a)) · bound the freshness window (C-7 app half, A7) · legs track filled quantity (C-17(a), after X-1(d),(f)) | X-1 answers; engine coordination for C-1, C-7, C-18(c) | Opus, high |
| **3 — the rest** | A9 trigger_offset (after X-1(b)) · A17/G7 close-in-gap · A11 tick rounding direction · M9 F10 reader share mode · A21/A22 small items | — | Opus, high (order path) / Sonnet, medium (log text) |

**Decision-bias tripwire** (`docs/harness-runs/decision-bias-20261007T0130Z-*`, baseline written first):
- The size-cap recommendation: `no_richer_option`, stable 5/5. Not flagged.
- **The batch-1 split: `gives_up_for_economy`, stable 5/5 — FLAGGED.** The split ships the targeted
  patches before the position-level cap the audit prefers (C-16(b)). The owner decides between the
  split and building everything after the experiments.

## 5. For the engine orchestrator (owner relays)

The engine owns C-1's engine half, C-2 (the 2-USD stop floor) and C-7's data-age field. C-18(c)
(publishing the working entry) changes `executor_feedback.json`, a shared contract. The app will not
change either shared file until both sides agree.

## 6. The proof harness

It compiles verbatim line ranges at `8232e9e` and refuses any other commit, so it cannot be a
regression test. Its scenarios (`trace`, `emergency-rejected`, `q1`, `partial`, `standdown`,
`mode-flip`, `close-in-gap`, `restore`) become the acceptance list for each batch's own OrderCheck
fixtures. Not run from this seat.
