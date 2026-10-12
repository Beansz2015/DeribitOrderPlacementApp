# Coordinator review — partial fills, Phase 2 (F1–F3)

**Reviewed 2026-10-13 (GMT+8) by the coordinator seat (Opus 5.5, high).**
**Under review:** commits `c7bec38`, `5c850a5`, `fd73951`, against `docs/spec-partial-fill-fix.md` §4–§5 and
`docs/impl-report-pf-phase2.md`.
**Verdict: ACCEPTED at code level; `docs/spec-partial-fill-fix.md` §5 items 1, 2 and 4 accepted, item 3 accepted
except 3c (see `PF-SB2`).** One follow-up seat, §3 below (owner ruling on `PF-SB3`, and the PF-R4 runtime check).
§5 item 5 (a real partial fill) stays the gate before any Amount above 10 USD and before `N2 LIVE`.

IDs: `PF-…` are the partial-fill feature's (`PF-R*` owner rulings, `PF-D1` code defect, `PF-SB*` the seat's
spec-back findings). `TN-F*` are the 2026-10-12 testnet record's findings. `F1`–`F3` are the spec's three parts.

---

## 1. Verified at the artefact

| Check | Result | How |
|---|---|---|
| Gate | **GATE PASSED, OrderCheck 463/463** | `tools/checks/verify-gate.ps1`, executed by this seat at `fd73951` |
| Nine `frmMainPageV2.vb` censuses | **71 occurrences across 67 lines** (12 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · **19**). The +1 is `placedOrderSizeUsd`, read once in `LegAmountForEdit`, as §4 F2 requires | executed; read the line |
| `X2Testnet` removed | 0 references in `*.vb` / `*.vbproj` | grep |
| F2 seam | `LegAmountForEditFor`: position ≠ 0 → `max(legFilled, 0) + |position|`; flat → placed size, else the box | read `:6634` |
| F1 target | `TpResizeTargetFor`: no edit when flat, wrong side or equal; **up only when the position grew** (`allowUp`); target compared with the TP's total `amount` | read `:6674` |
| F3 decision | `LegSizeBreachFor` on the **open part** (`amount − filled_amount`): RestingFlat / WrongSide / Oversize / Undersize / None | read `:6692` |
| A TP part-fill with a stale position does not over-size | TP echo first (filled 100, position still 300): target 400 > total 300 needs `allowUp`, which is False → no edit; F3 waits out its grace; the position echo then makes it equal | traced the two seams |
| Bracket chase after a part-fill | `BracketChaseEditsLegs` = entry unfilled; after any fill the entry moves alone and the function returns False, so `legAnchorPrice` is not advanced for legs that did not move (`:2667`, `:2720`) | read |
| Request ids 42, 43 | New constants; no other use of 40–43 apart from batch 2's 40 and 41 | grep |
| Do-not-touch | TP still without `reduce_only`; payload `first_hit`, full-size legs; emergency send order unchanged | read the diff summary and the reverted X-2 call sites |
| Mutation proofs, the testnet run (items 3a, 3b, 3d, 4), the `private/edit` wording | **Carried from the seat's report, not re-run by me.** The run's raw echo is at `%LOCALAPPDATA%\DeribitOrderApp\pf2-backup-20261013\` | report §1, §3, §4 |

## 2. The seat's findings — dispositions

| ID | Meaning | Disposition |
|---|---|---|
| `PF-D1` | The triggered-SL chase sent `|position|`, which would cut a part-filled stop below the position | ✅ Fixed (`c7bec38`). Runtime: §5 item 5 |
| `PF-SB1` | A position-only F1 trigger misses every first partial fill (the TP echo lands 1–38 ms after the position) | ✅ Accepted: F1 also fires on the first open echo of a **new** TP id. Narrowed so an injected edit still reaches F3 and its page |
| `PF-SB2` | PF-R4 (TP resting while flat) cannot be reached by an API flatten: the app's close path cancels all orders first | **Coordinator ruling:** accept PF-R4 on fixtures **now**, and observe it at runtime in the §3 follow-up with a temporary testnet-only switch that skips the close cancel-all. The owner's stated tolerance (no widening of loss bounds on rarity grounds) argues for seeing the backstop work, and the cost is one small switch |
| `PF-SB3` | A TP resting on the **same** side as the position adds to it if it fills; built as alarm only | **Owner ruling 2026-10-13: cancel, then alert** (as PF-R4). Follow-up §3. Decision-bias tripwire on the coordinator's recommendation: `richer_option_wrong`, stable 5/5, not flagged (`docs/harness-runs/decision-bias-20261013T0115Z-*`) |
| `PF-SB4` | Edit up only on growth; a refused F3 resize pages; one F3 retry per episode | ✅ Accepted. Item (3), the single retry, is the `TN-F3` class: the `TN-F3` protection spec must cover the F1/F3 refusal path too |
| `PF-SB5` | A fill re-anchor also mutes `Triggered SL placed @` (`UpdateFlag`) | Folded into the `TN-F4` hygiene row |

**Not observed, carried to §5 item 5 or later:** any real part-fill (F1 down/up, F2 with `legFilled` > 0, `PF-D1`, the
total reading of `amount`); refusal paths of ids 42/43; the SHORT side; the re-anchor at 20 USD.

## 3. Follow-up seat — `PF-SB3` cancel and the PF-R4 runtime check

- **`PF-SB3`:** `WrongSide` → cancel the TP by id (the PF-R4 path, request id 43), red log, urgent page
  "TP on the position's side: cancelled". Fixture that fails against `fd73951`.
- **PF-R4 at runtime:** a temporary module with one switch, ON only when `AppSecrets.IsTestnet` **and** a flag file
  exists (pattern: `X2Testnet.vb` at `7cfc477`): skip the cancel-all in `CompletePositionClose`. Testnet run: open
  10 USD, flatten by testnet API `private/close_position` → F3 cancels the resting TP within 3 s and pages urgent.
  Then remove the module (grep 0) and gate again.
- Safety and procedure as `docs/review-protection-batch2.md` §5 (testnet key, verbatim API log, placements only
  through `tools/place-and-verify.ps1`, end flat, cancel-on-disconnect OFF).

## §Model and effort (`docs/HANDOVER-6.md` §7b)

- **Follow-up seat (§3):** Opus 5.5, high — order and close paths.
- **Its review:** coordinator, Opus 5.5, high.
