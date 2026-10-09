# Coordinator review — X-1 testnet experiments (2026-10-10)

**Record:** `docs/runtime-record-x1-testnet-2026-10-10.md`. **Commits:** `cd15c8d` (temporary switches),
`7f253fc` (results; switches removed). **Reviewer:** coordinator seat, Opus 5.5. **Date:** 2026-10-10.

## Verdict: ✅ ACCEPTED. The results stand as the evidence base for batch 2.

## 1. Verified at the artefact

| Claim | Result | How |
|---|---|---|
| Temporary code fully removed | ✅ no app or `tools/` diff between `a367eb4` and `7f253fc`; `X1Testnet.vb` absent; 0 grep hits; 0 hits for `X1Testnet` in the x64 dll; no `X1_*` flag file in the bin | `git diff --stat`, grep, byte scan, `ls` |
| x64 bin still testnet | ✅ `secrets.json` → `testnet` | read the key |
| (h) trade #136 | ✅ `RequoteCount` 1, `SignalPrice` 82520, `SlippageATR` 0.1273 | read-only query on a copy of `trades.db` |
| (i) `AbortedEntries` row 3, and row 4 (the unplanned 10-USD order) | ✅ match the record | same copy |
| Echo-based results (a)–(g), (k) | Accepted from Appendix A's verbatim echoes; not re-observed | — |

**Abort row 1** (2026-10-09 15:34:40Z, `ATR slippage at placement`, anchor 82994.5), which the record does
not explain: a placement made 23 s after the owner's third check-1 reconnect, while the long opened at 15:33
was still open. The anchor belonged to that open position's entry, and the chase-anchor fixes deliberately
never touch an anchor set on entry (`docs/review-chase-anchor-reset.md` §3). Pre-existing semantics, not a
regression.

## 2. Notes on the record

- **The unplanned 10-USD placement** (record §1) was caught by `tools/place-and-verify.ps1` failing loudly,
  as designed; the size was the standard one. The cause — continuing after `tools/set-textbox.ps1` refused —
  is a seat-procedure slip, already corrected in the run. Hygiene item: `tools/place-and-verify.ps1` cannot
  check the Amount it placed; worth an `-ExpectAmount` guard later (Sonnet, medium).
- **Correction to record §5 item 4:** "observe it live at 10 USD" cannot work. 10 USD is one contract and
  can never partially fill. A live observation needs a larger size — exactly the exposure the owner holds.
  The partial-fill fix must be designed without observing a partial fill.

## 3. What the results change

| Fact (record §3) | Consequence for batch 2 |
|---|---|
| (a) The dead leg is pushed: `rejected:price_too_high` on the OTO template | A13's alert has a hook: branch on `order_state` starting with `rejected` for the stop leg |
| (b) `trigger_offset` in `otoco_config` is dropped | **The SL never trails.** The `Trig.O.` box has no effect on an OTOCO stop. The audit's trailing-offset row is re-scoped; the false comment at `frmMainPageV2.vb:3770` is corrected in batch 2 |
| (c) A crossing post-only stop limit is repriced by the exchange | The chase need not rescue a crossed limit |
| (e) A triggered stop is a new order id, linked by `trigger_order_id` | Every stop-tracking feature must follow the link |
| (f) A reduce-only TP makes every SL trigger cancel both legs | **Rules out a reduce-only TP** as the partial-fill fix |
| (g) A reduce-only market fills while an UNtriggered reduce-only SL rests | Reduce-before-cancel is viable for that case; the triggered-SL case is still unobserved, so the emergency reorder stays parked |
| (h)–(j) | The slippage fields and the chase-anchor reset are runtime-accepted |
