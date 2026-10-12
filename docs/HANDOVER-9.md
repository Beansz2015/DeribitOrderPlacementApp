# HANDOVER-9 — coordinator seat, DeribitOrderPlacementApp

**Written 2026-10-13 (GMT+8) by the outgoing coordinator (Opus 5.5).** This is the entry point. It supersedes
`docs/HANDOVER-8.md` for state. Still live: `docs/HANDOVER-8.md` §5 (Deribit behaviour observed on testnet) and §6
(traps); `docs/HANDOVER-7.md` §5 (Max Size rulings) and §6 (traps); `docs/HANDOVER-6.md` §4–§7 (bin rules, runtime
bite-list, invariants, methodology). Do not read `docs/HANDOVER.md` or `-2` to `-5`.

🚨 **The owner is away 2026-10-14 → 2026-11-25** (the pane's `away` block) and is **not live trading**. Rulings and
anything that needs the owner wait for the return. Every ruling this era needed was taken before the absence.

---

## 1. State — verified 2026-10-13, not inherited

| Thing | Value | How checked |
|---|---|---|
| HEAD | `5b526ee` (+ the commit that adds this handover) | `git rev-parse --short HEAD` |
| origin/master | `f926812` | separate `git rev-parse` call |
| Unpushed | **17** before this handover's commit | `git rev-list --count origin/master..HEAD` |
| Working tree | clean | `git status --porcelain` |
| App/tools code | last changed in `c52bf2b`; no app or `tools/` change since | `git diff --stat c52bf2b HEAD -- DeribitOrderPlacementApp tools` empty |
| Gate | **GATE PASSED, OrderCheck 466/466** | executed by this seat at `c52bf2b` |
| Nine `frmMainPageV2.vb` censuses | **71 occurrences across 67 lines**: 12 · 8 · 2 · 3 · 1 · 0 · 13 · 13 · 19 (`placedOrderSizeUsd` +1 in the partial-fill F2 helper) | `Select-String -AllMatches -CaseSensitive`, summed. `CLAUDE.md` carries the same line |
| x64 bin | dll 2026-10-12 20:03 UTC; carries the final code (has `TP on the position's side: cancelled`, no `PFFU_` switch) | UTF-16 string scan of the dll |
| x64 `secrets.json` | `Environment: testnet`; no flag file, no `r1-anchor.json` in the bin | read; listing |
| Deribit cancel-on-disconnect | **OFF on every sub-account** (owner, 2026-10-12); testnet read back OFF 2026-10-12 20:03 UTC | owner; testnet API |

## 2. ⛔ Jev harnesses — fire them on their triggers (carried forward, as `CLAUDE.md` requires)

| Harness | Fire it when | Command (`powershell -NoProfile -File …`) |
|---|---|---|
| **Doc re-ranker** — the context saver | Any "where was this decided / defined?" question, **before** grepping or reading docs | `tools/checks/doc-reranker.ps1 -Query "<question>"` |
| **Decision-bias tripwire** | A new recommendation on a decision with options. Write the population and **your own label first**, then run. **A `gives_up_for_economy` verdict goes to the owner** | `tools/checks/decision-bias.ps1 -Population … -Baseline … -OutPath …` |

- The owner asked (2026-10-12) for Jev **wherever feasible**, not only on the strict triggers.
- Read the agreement rate, not the probability. This era's runs: `docs/harness-runs/decision-bias-20261012T*` and
  `-20261013T*`. **One flag** this era: `SB3-fix/E1` (a fix seat's escalation — a restart lost the M.SL pin). The
  coordinator's "accept" was `gives_up_for_economy` 5/5; the owner overruled it and chose to persist the pin.
- Re-ranker blind spots seen: it missed `docs/ROADMAP-2026-08.md` and the audit triage's rulings table. Query audit
  material with the audit's own terms (`A14`, `S0`).
- Never point Jev at logs, counts, dates or `trades.db`. Code counts; Jev judges meaning.

## 3. What this era did (2026-10-12 → 2026-10-13) — all CLOSED unless stated

| Item | Outcome | Record |
|---|---|---|
| Position protection batch 2 (rejected stop leg + R1 action, live no-stop watchdog, cancel-on-disconnect check, Reduce refusal) | ✅ runtime-accepted on testnet (LONG) | `docs/review-protection-batch2.md`; `docs/review-testnet-2026-10-12.md` |
| `SB3` — after an R1 limit, pin the M.SL cap at the dead stop's trigger | ✅ runtime-accepted | `docs/review-sb3-msl-anchor.md` |
| `SB3-fix/E1` (b) — persist the pin (`r1-anchor.json`) and re-pin at restore | ✅ runtime-accepted (restart observed) | `docs/review-e1-pin-restore.md` |
| X-2 testnet experiment | ✅ `incremental` refused every edit (`11050`) → dropped; no partial fill can be made on testnet (touch ≥ ~720k USD) | `docs/runtime-record-testnet-2026-10-12.md`; its review |
| Partial fills Phase 2 — the TP's open part always equals the position (F1), one leg-amount source (F2), once-a-second leg-size check (F3), `PF-D1` triggered-SL sizing | ✅ accepted at code level; §5 items 1–4 on testnet | `docs/review-pf-phase2.md` |
| Partial fills follow-up — a same-side TP is cancelled (owner ruling); PF-R4 (TP resting while flat → cancel) seen at runtime | ✅ runtime-accepted (LONG) | `docs/review-pf-followup.md` |
| Cutover checklist: cancel-on-disconnect OFF on every sub-account | ✅ owner | `docs/production-cutover-checklist.md` §1 |

**Rulings taken this era** (all in their docs): batch 2 `SB3` pin; `SB3-fix/E1` (b); acceptance items b and d by
testnet API; `PF-R1`–`PF-R6`; `PF-SB3` cancel-then-alert. The owner's M.SL tolerance, now a standing ruling: **never
recommend a path where the M.SL flatten can fire beyond one M.SL past the intended anchor; rarity is not an argument**
(memory `standing-rulings-in-force`).

## 4. OUTSTANDING

**Owner side** (the pane is the live list):
1. **Push** (18 commits with this handover).
2. **After the return — the gate before size:** a real partial fill with the fix in force
   (`docs/spec-partial-fill-fix.md` §5 item 5): try it on testnet from a second sub-account if the spread is wider
   than one tick. Until a real partial fill is seen, the hold on Amount above 10 USD and on `N2 LIVE` stays.
3. `N2 LIVE` (risk sizing in Live) — held, as above.
4. AWS London migration (`docs/production-cutover-checklist.md` §9); re-check cancel-on-disconnect on the AWS key.

**Coordinator side:**
1. **Refused edits — launch the implementer seat** (Opus 5.5, high): `docs/spec-refused-edits.md`, written
   2026-10-13 from `TN-F3` (a testnet finding: a refused stop edit still moves the app's own record, with no alert)
   and `PF-SB4` (a refused TP resize retries once). **All four owner rulings `RE-R1`–`RE-R4` are in.** Fixtures and
   code review can complete during the absence; its runtime check (§4 item 3) waits for the owner. Its §2 keeps the
   M.SL anchor latch untouched on purpose — do not "fix" it.
2. Later protection items: position-level loss cap, quote-silence watchdog, stand-down cancels the entry, bounded
   freshness window; emergency reduce-before-cancel (one observation, under `incremental` only — confirm under
   `first_hit`, `TN-F6`).
3. Remaining audit rows (trailing trigger offset, close during a socket gap, tick rounding direction, payload reader
   share mode, small log items) and hygiene (serialise socket sends; `TN-F4` the missing "Triggered SL placed" line).

**Not yet observed at runtime anywhere:** the SHORT side of every batch-2 and partial-fill path; any real partial
fill; refused-edit and refused-cancel paths of request ids 42 and 43.

## 5. New Deribit behaviour observed this era

Memory `deribit-observed-behaviour` holds the full list. The ones that decide design:
- `incremental` linked orders cannot be edited at all (`11050`). Do not propose it again without new evidence.
- Testnet's touch carries a market-maker quote of about 1,000,000 USD per side: no partial fill at ≤5,000 USD.
- Cancelling either OTOCO leg by request also cancels the other (`oco_other_closed`).
- A TP amount edit under `first_hit` is accepted (`replaced: true`).
- A reduce-only market fills in full with a triggered reduce-only SL limit resting; Deribit cancels the limit
  (seen once, `incremental`).

## 6. Traps that cost this era time

- **The gate builds AnyCPU only.** Rebuild x64 before any runtime check (`dotnet build … -p:Platform=x64`); check
  `secrets.json` and the `— TESTNET` title.
- **Auto-mode's permission check refuses testnet order placement** even through `tools/place-and-verify.ps1`. A seat
  stops, verifies flat, and waits for the owner. With the owner away, no seat can place a testnet order — runtime
  work waits for the return.
- **Mutation restore by byte copy keeps the old mtime** → MSBuild reuses the mutated assembly. Touch the file first.
- **Git Bash `sed -i` turns `frmMainPageV2.vb` to LF**, and `grep -c $'\r$'` then lies. Use the Edit tool; count
  with Python bytes.
- **Amounts print as `5.0e3`** in Deribit echoes: grep for `5000` misses them. Outbound payloads in the raw echo are
  pretty-printed across lines: match on the whole text.
- **PowerShell 5.1** mangles `⚠` and `§` unless files are read and written as UTF-8; its `ConvertTo-Json` writes
  `'` for apostrophes (valid JSON).
- **The push row counts the commit that updates it**, and the owner pushes between turns: re-count every time.
- **Raw-echo logs carry the account e-mail**: they live in `%LOCALAPPDATA%\DeribitOrderApp\*-backup-20261013\`,
  never in the repo.

## 7. Method, unchanged

`docs/HANDOVER-6.md` §7: verify against artefacts; escalate spec defects before code; separate verified from not;
gate + censuses before claiming a pass; every spec closes with a model and effort line; coordinator review is Opus.
Owner rule: anything that changes live order behaviour needs the owner's ruling before it ships — **so with the
owner away, specs can be written and reviewed, but no new live-behaviour change can be ruled until 2026-11-25.**

## 8. Model and effort for the incoming seat

- **Model:** Opus 5.5. **Effort:** high for anything on the order, stop, receive, bridge or close paths; medium for
  docs and pane upkeep.
- Spend is context: `frmMainPageV2.vb` is ~8,900 lines. Read by symbol, review by `git show` and targeted greps, and
  fire the doc re-ranker before reading docs.
