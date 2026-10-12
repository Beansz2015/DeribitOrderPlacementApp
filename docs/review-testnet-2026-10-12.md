# Coordinator review — testnet session 2026-10-12: batch-2 acceptance, the M.SL pin, X-2

**Reviewed 2026-10-13 (GMT+8) by the coordinator seat (Opus 5.5, high).**
**Under review:** `docs/runtime-record-testnet-2026-10-12.md` (commit `fe89c0a`) and the temporary X-2 code
(`7cfc477`, `DeribitOrderPlacementApp/X2Testnet.vb`).
**Verdict: ACCEPTED.**
- Position-protection batch 2, the `SB3` pin and its restart re-pin are **runtime-accepted** on testnet, LONG side.
- X-2 is accepted as a record. It refutes `incremental`, so `PF-R2` falls back to (b), as the owner pre-ruled.
  Two new owner rulings followed (`PF-R5`, `PF-R6`), now in `docs/spec-partial-fill-fix.md`.

IDs: `TN-…` are the testnet record's findings (its section 6). `PF-…` are the partial-fill spec's decisions.
`R1` is batch 2's rejected-stop ruling. `SB3` is the M.SL pin. `Q0`–`Q7` are the X-2 questions.

---

## 1. Verified at the artefact

The raw-echo logs (`x2-raw-echo.log`, `x2-raw-echo-2.log`) sit in `%LOCALAPPDATA%\DeribitOrderApp\tn-backup-20261013\`,
outside the repo because they carry the account e-mail. Counts below are by code (regex over the whole text), never Jev.

| Claim in the record | Result | How |
|---|---|---|
| The X-2 switches cannot act on live | `TakerEntryOn` and `IncrementalOn` call `FlagOn`, which needs `AppSecrets.IsTestnet` **and** a flag file; raw echo the same | read `X2Testnet.vb` and the `7cfc477` diff |
| X-2 (a): the 5,000 USD taker entry filled **whole** | ✅ One trade, `trade_id 272977670`, `liquidity "T"`, `filled_amount 5.0e3`, `order_state filled`. The request went out with `post_only: false` at 82870 | the 16:20:05.016 echo line. Note: amounts print as `5.0e3`, so a grep for `5000` misses them |
| 14 `11050` refusals ("linked orders with incremental trigger fill condition can't be edited") | ✅ 14; matches 3 in (f) + 5 in (g) + 6 in the extra run | count |
| 7 `incremental` placements; 2 `rejected:price_too_high` pushes; 2 R1 limit sends (id 40) | ✅ 7 · 2 · 2 | count |
| Item b: the SL was cancelled by the user request | ✅ `SLTS-11032646` `cancel_reason "user_request"` | regex |
| Cancel-on-disconnect OFF at the end | ✅ the app's id-41 reads at 16:15:10, 16:27:54, 16:30:01 all `enabled: false`. The API reads at 16:28:19 and 16:31:07 are carried from the record's Appendix B | echo lines |
| Flat, no orders at the end | ✅ id-777 `size 0.0` and id-778 `[]` at 16:30:01. The last entry (16:30:39) was cancelled with `filled_amount 0.0`; its templates `oto_primary_closed` | echo lines |
| Settings restored; no flag files; no `r1-anchor.json` left | ✅ `orderapp-settings.json` hash equals the backup; the bin holds no `X1_*`/`X2_*` flag, no anchor file. `secrets.json` reads `testnet`, as it did before the session | hash, listing |
| Gate and censuses at `7cfc477` | Carried from the record (GATE PASSED 441/441; 70 occurrences) — not re-run by me | — |
| The app-log lines quoted for items a–e (e.g. "re-pinned … (restored)") | **Carried, not verified.** They come from the app's log box, which is not on disk. The echo supports the actions behind them: the R1 limit (id 40), its open echo, the restore snapshot holding the limit, and the emergency cancel-all then reduce | — |

## 2. Findings — dispositions

| ID | Meaning | Disposition |
|---|---|---|
| `TN-F1` | `incremental` makes Deribit refuse every edit of the OTOCO set | ✅ Decisive. `PF-R2` → (b); `incremental` dropped (`docs/spec-partial-fill-fix.md` §0, §4 F1) |
| `TN-F2` | No partial fill on testnet: the touch holds ≥719,830 USD | ✅ Owner ruling `PF-R6` (c), staged acceptance (`docs/spec-partial-fill-fix.md` §5) |
| `TN-F3` | When an edit is refused, the app still advances its chase reference and logs the move; 58 s of refused triggered-SL edits raised no page | 🔴 **Queued as a protection item**, ahead of the later protection items. Under `first_hit` edits normally succeed, but rate limits and other refusals happen. The M.SL emergency is unaffected: it measures from the anchor and the quote, not from the limit. Fix shape for its own spec: on a refused SL edit, keep the reference at the exchange's price; page if a triggered SL has not moved for N s while the price runs |
| `TN-F4` | `Triggered SL placed @ …` is suppressed after a chase edit when the next entry is a market entry (`UpdateFlag` not reset in the market branch) | Queued as hygiene (log only) |
| `TN-F5` | Cancelling the untriggered SL by API also cancels the TP | ✅ Recorded (memory `deribit-observed-behaviour`). The watchdog paged correctly |
| `TN-F6` | A reduce-only market filled in full with a **triggered** reduce-only SL limit resting; Deribit cancelled the limit in the same message. Seen once, under `incremental` | Evidence for the queued emergency reduce-before-cancel item. Confirm once under `first_hit` before relying on it |
| `TN-F7` | `incremental` legs get a new `oco_ref` | ✅ No action; the app reads no `oco_ref` |

**Decision-bias tripwire** (`docs/harness-runs/decision-bias-20261012T1640Z-*`, baselines written first): `TN-F2`'s route
and `PF-R5`'s direction both `richer_option_wrong`, stable 5/5. Not flagged. The owner ruled both.

## 3. Coverage gaps carried forward

- **SHORT side not run** for any batch-2 item (every position was LONG).
- **Item a's run-1 app log was lost** at the restart; run 2 repeats item a live, without the restart. The re-pin
  line itself rests on the seat's reading after the relaunch.
- `Q0`–`Q3` unanswered (no partial fill). The `first_hit` amount edit of a placed TP is unobserved; it is the
  first check of the Phase 2 acceptance (`docs/spec-partial-fill-fix.md` §5 item 3a).

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **Partial-fill Phase 2 implementer:** Opus 5.5, high — order, receive and edit paths.
- **`TN-F3` spec and fix:** Opus 5.5, high — the triggered-SL chase.
