# HANDOVER-8 — coordinator seat, DeribitOrderPlacementApp

**Written 2026-10-12 by the outgoing coordinator (Opus 5.5).** This is the entry point. It supersedes
`docs/HANDOVER-7.md` for state. `docs/HANDOVER-7.md` §5 (the Max Size rulings) and §6 (traps; its reconnect
trap was rewritten 2026-10-08) stay live. `docs/HANDOVER-6.md` §4–§7 stay live (bin rules, runtime bite-list,
invariants, methodology). Do not read `docs/HANDOVER.md` or `-2` to `-5`.

🚨 **The owner is away 2026-10-14 → 2026-11-25** (the pane's `away` block). Rulings and runtime checks that
need the owner wait for the return. The owner is **not live trading**; nothing below is urgent in that sense.

---

## 1. State — verified 2026-10-12, not inherited

| Thing | Value | How checked |
|---|---|---|
| HEAD | `fc35469` | `git rev-parse --short HEAD` |
| origin/master | `7f253fc` | separate `git rev-parse` call (a combined call fails on this box's git) |
| Unpushed | **3** (the experiment review, the batch-2 spec, its rulings) | `git rev-list --count origin/master..HEAD` |
| Working tree | clean | `git status --porcelain` |
| Gate | **GATE PASSED, OrderCheck 376/376** | executed at `fc35469` |
| Nine `frmMainPageV2.vb` censuses | **68 occurrences across 64 lines**: 10 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 18 | `Select-String -AllMatches -CaseSensitive`, summed |
| x64 bin | dll 2026-10-10 01:27; no app/tools code change since `9d51e4d`; carries `ArmEmergencyClose` (the latest code) | byte scan; `git diff --stat 9d51e4d HEAD -- DeribitOrderPlacementApp tools` empty |
| x64 `secrets.json` | `Environment: testnet` | read the key |
| Deribit testnet | **cancel-on-disconnect OFF** (owner, 2026-10-10). Live account **not yet checked** | owner |

## 2. ⛔ Jev harnesses — fire them on their triggers (carried forward, as `CLAUDE.md` requires)

| Harness | Fire it when | Command (`powershell -NoProfile -File …`) |
|---|---|---|
| **Doc re-ranker** — the context saver | Any "where was this decided / defined?" question, **before** grepping or reading docs | `tools/checks/doc-reranker.ps1 -Query "<question>"` |
| **Decision-bias tripwire** | A new recommendation on a decision with options. Write the population and **your own label first**, then run. **A `gives_up_for_economy` verdict goes to the owner** | `tools/checks/decision-bias.ps1 -Population … -Baseline … -OutPath …` |

Read the agreement rate, not the probability. This era's runs are in `docs/harness-runs/decision-bias-2026100*`.
Two flags were raised and shown to the owner (batch-1 split; batch-2 R3); the owner ruled on both.

## 3. What this era did (2026-10-06 → 2026-10-12) — all CLOSED unless stated

| Item | Outcome | Record |
|---|---|---|
| Jev doc re-ranker + decision-bias tripwire ported from the engine | ✅ | `CLAUDE.md`; memory `jev-harness-scope` |
| Outstanding pane (`docs/outstanding.json`, schema 1) | ✅ in use; rules in `docs/outstanding-json-seat-instructions.md` | — |
| Settings boxes snap back to the value in force (all eight) | ✅ runtime-accepted | `docs/ROADMAP-2026-08.md` §5 |
| FrmIndicators retired; engine ATR in every mode; switchable Flat ATR (resets unticked each launch); commit-on-close | ✅ runtime-accepted | `docs/review-frmindicators-retirement.md` |
| Trade slippage fields + `AbortedEntries` table | ✅ runtime-accepted | `docs/review-trade-slippage-fields.md` |
| Chase-anchor reset (rejection, close, never-sent seed); bridge reports the real refusal reason | ✅ runtime-accepted (bridge half by code reading) | `docs/review-chase-anchor-reset.md` |
| Adversarial-audit triage | ✅ | `docs/triage-adversarial-audit-2026-10.md` (rulings §7) |
| Protection batch 1: reconnect forever + paging (cause in the page; urgent only with a position), side from the position, restore NO STOP check, honest emergency messages | ✅ runtime-accepted | `docs/review-protection-batch1.md`; `docs/runtime-record-protection-batch1-2026-10-09.md` |
| X-1 testnet experiments | ✅ accepted | `docs/runtime-record-x1-testnet-2026-10-10.md`; `docs/review-x1-testnet-experiments.md` |
| Failed-reconnect `DOWN` suppression question | ✅ settled by code reading | `docs/runtime-record-ws-down-emitter-2026-08-14.md` §5 item 1 |
| **Protection batch 2** | 🟡 **SPEC WRITTEN, all four rulings in; no code** | `docs/spec-protection-batch2.md` |

## 4. OUTSTANDING

**Owner side** (the pane is the live list):
1. **Push** 3 commits.
2. **Launch the batch-2 implementer seat** (Opus, high) — `docs/spec-protection-batch2.md`. May wait for the return.
3. **Before any live use:** confirm cancel-on-disconnect is OFF on the live account and key
   (`docs/production-cutover-checklist.md` §1).
4. `N2 LIVE` (a ruling: risk sizing in Live, `docs/HANDOVER-7.md` §2 item 3) — **held** by the owner until the
   partial-fill fix ships (risk sizing makes 80–90 USD entries; a partial fill can flip the account short with
   no stop). No code cap, by owner ruling.
5. AWS London migration (`docs/production-cutover-checklist.md` §9).

**Coordinator side:**
1. Review batch 2 when its implementer reports; then a harness testnet run of its acceptance
   (`docs/spec-protection-batch2.md` §7), on the experiment seat's procedure
   (`docs/spec-x1-testnet-experiments.md` §0).
2. **The partial-fill fix** (next_up #1). Design constraints, all observed: a reduce-only TP is ruled out (an SL
   trigger then cancels both legs); a partial fill cannot be produced on testnet, nor at 10 USD (one contract).
   Design without observing one.
3. Later protection items (position-level loss cap, quote-silence watchdog, stand-down cancels the entry,
   bounded freshness window); the emergency **reduce-before-cancel** waits for the triggered-SL case to be
   observed.
4. Remaining audit rows and hygiene (the pane's `next_up`).

## 5. Deribit behaviour OBSERVED on testnet — the evidence base

Memory `deribit-observed-behaviour` holds the full list with sources. The ones that decide design:
- **Cancel-on-disconnect** deletes every order on every disconnect when on.
- A crossed OTOCO stop leg never becomes an order; the OTO template is **pushed** as `rejected:price_too_high`.
- `trigger_offset` inside `otoco_config` is **dropped**: the OTOCO stop never trails. The `Trig.O.` box does
  nothing for it.
- A triggered stop is a **new order id**, linked by `trigger_order_id`.
- A reduce-only TP makes every SL trigger cancel both legs.
- `11030 invalid_reduce_only_order` is not specific (seen with a covering reduce-only order and on a flat position).

## 6. Traps that cost this era time

- **The gate builds AnyCPU only.** Rebuild x64 before any runtime check; re-check `secrets.json` and the
  `— TESTNET` title. The owner's exe: `DeribitOrderPlacementApp\bin\x64\Debug\net9.0-windows8.0\DeribitOrderPlacementApp.exe`.
  `tools/launch-app.ps1` starts the wrong (AnyCPU) build.
- **Harness focus:** `tools/set-textbox.ps1 -CommitViaBlur` brings the app to the front and can be refused focus.
  **Stop on its non-zero exit** — one unplanned 10-USD testnet order came from not stopping.
- **The push row counts the commit that updates it.** The owner pushes between turns; re-count before writing.
- **`outstanding.json` must be schema 1** (`waiting_on_trader` / `in_progress` / `dated` / `next_up` arrays of
  objects), or the pane renders blank rows.
- **Screenshots lose the log box's colours** (both `PrintWindow` and the owner's snips showed it white). The app
  is fine; ask the owner to look.
- **The engine checkout is read-only.** Read its branches with `git show <ref>:<path>`; never fetch into it.
- **Python one-liners through bash heredocs mangle backslashes and quotes** in Windows paths and VB strings.
  Write the script to the scratchpad and run it.

## 7. Method, unchanged

Canonical in `docs/HANDOVER-6.md` §7: verify against artefacts; escalate spec defects before code; separate
verified from not; gate + censuses before claiming a pass; every spec closes with a model and effort line;
coordinator review is Opus. Owner rule from the audit: **anything that changes live order behaviour needs the
owner's ruling before it ships.**

## 8. Model and effort for the incoming seat

- **Model:** Opus. **Effort:** high for reviews on the order, stop, receive, bridge or act paths; medium for
  docs and pane upkeep.
- The real spend is context: `frmMainPageV2.vb` is very large. Read regions by symbol, review by `git show` and
  targeted greps, and fire the doc re-ranker before reading docs.
