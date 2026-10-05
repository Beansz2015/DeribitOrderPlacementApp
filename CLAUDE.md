# DeribitOrderPlacementApp — instructions for every conversation in this repo

This applies to **every** seat: coordinator, implementer, reviewer.

## Output format

The user's global rules in `~/.claude/CLAUDE.md` apply here in full. The two that matter most in
this repo, because it has many documents and many finding IDs:

- **Never write a bare section number.** Name the document first — `docs/HANDOVER-6.md` §5.5, not
  §5.5 — and repeat the document name on later mentions in the same reply.
- **Never write a bare finding ID.** Say the feature, the kind and the meaning on first use.
  Write: `SB3` (a C1 spec-back finding — the harness set the wrong text box).
  **This repo has had two live `D1`s and two unrelated `F1`s at the same time.** A bare ID is
  genuinely ambiguous here.

## Where to start

- **Entry point: `docs/HANDOVER-7.md`.** Read it, then verify it. `docs/HANDOVER-6.md` stays live
  for its §4–§7 only (bin rules, runtime bite-list, invariants, methodology).
- Then fire the Jev triggers below, and read `docs/outstanding.json`.
- Then `docs/ROADMAP-2026-08.md` §1 (owner queue), §5 (coordinator queue), §6 (not building).
- **Do NOT read `docs/HANDOVER-3.md`, `-4` or `-5`.** They are superseded, and `docs/HANDOVER-4.md`
  §5 carries two runtime instructions that were later reversed.
- Closed work is indexed in `docs/ARCHIVE-closed-milestones.md`, which is deliberately **not** part
  of the read path.

## Finding-ID convention (`docs/HANDOVER-6.md` §7bb)

- `E` = an escalation raised **before** writing the code it concerns.
- `D` = a defect found **in code**.
- `SB` = a spec-back finding **against the docs**, raised after the work.
- Scope every ID to its feature. Do not reuse a prefix across kinds.

## Jev harnesses — fire them on their triggers (owner ruling 2026-10-06)

Jev returns typed probabilities only. It writes no text and no code. It is bad at maths, counting
and dates: never point it at numeric logs, `trades.db` or a `.log` file. Code computes; Jev judges
meaning. Both tools read the key in place from the engine's gitignored `typesafe.local.env`.

| Harness | Fire it when | Command (`powershell -NoProfile -File …`) |
|---|---|---|
| **Doc re-ranker** — the context saver | Any "where was this decided / defined?" question, **before** grepping or reading docs. Then read only the top hits, by line range | `tools/checks/doc-reranker.ps1 -Query "<question>"` |
| **Decision-bias tripwire** | You make a new recommendation on a decision with options. Write the population and **your own label first** (the baseline), then run. **A `gives_up_for_economy` verdict sends the decision to the owner** | `tools/checks/decision-bias.ps1 -Population … -Baseline … -OutPath …` |

- **Read the agreement rate, not the probability.** Both sample 5× by default. A row below 1.0 is
  unstable: read that source yourself.
- **Doc re-ranker:** about 150 calls and 45 s per query. It reads committed docs at `HEAD`, not the
  working tree. It excludes `docs/HANDOVER.md` and `docs/HANDOVER-2.md` through `-5.md`. A low
  no-answer probability means the shortlist lacks the answer — trust that signal.
- **Shadow-mode rule:** `docs/harness-shadow-mode-protocol.md` §2. Look at a harness's answer before
  writing your own read, and the comparison is worthless for good.
- Both are advisory and never part of the gate. Each script's header gives its engine source and
  what was left out.
- ⛔ **Carry this table forward in every handover's first-actions list.** The engine armed six
  harnesses and lost the table between handovers; in the next 8 days only 1 of 6 ran.

## Outstanding pane — `docs/outstanding.json`

- **Update it the moment an item opens, closes, re-dates or is ruled.** The pane reads the working
  tree, so the owner sees the change at once. Commit it with the change that caused it, with
  `updated_utc` bumped.
- An agent you dispatch gets an `in_progress` row at dispatch. Remove it when the agent reports.
- Rules, schema and the pre-commit check: `docs/outstanding-json-seat-instructions.md`. That local
  copy wins over the engine copy that the global instructions name.
- When the push row counts commits ahead, **count the commit that updates the row itself.**

## Safety boundaries — not negotiable

- **A seat never places a trade and never arms the bridge.** The owner drives every trade, ARM and
  START.
- Harness-driven placement is permitted **only** on a TESTNET-titled, harness-launched session, and
  **only** through `tools/place-and-verify.ps1`.
- **The owner is the only pusher.** Commit locally. Never push.
- The engine repo (`C:\Dev\DeribitVerdictEngine`) is **READ-ONLY** from here. Cross-app decisions go
  through the owner.
- **Screenshots capture the APP, never the desktop** — `tools/screenshot-mainform.ps1` or
  `tools/screenshot-full.ps1` only. Note `screenshot-full.ps1` is a full-**form** capture, not a
  desktop capture. Full detail: `docs/HANDOVER-6.md` §5 item 13.

## Method

- **A doc is not evidence about a doc.** Verify against artefacts: the code, the reflog, the
  settings JSON, `trades.db`, the tools script. Full version: `docs/HANDOVER-6.md` §7.
- **Escalate a spec defect to the owner BEFORE implementing.** Do not work around it. The spec gets
  amended and the ruling folds back into it.
- **Say what you did not verify.** Separate it from what you did.
- Run the gate (`tools/checks/verify-gate.ps1`) and the nine `frmMainPageV2.vb` censuses before
  claiming a pass. The censuses are **occurrence** counts — 68 across 64 lines. A count-mode grep
  returns 64 and reads as four missing.

## Model and effort (`docs/HANDOVER-6.md` §7a)

- **Opus, high effort** — anything touching the order, stop-loss, receive, bridge or act paths.
- **Sonnet, medium** — genuinely mechanical work: `tools/` scripts, fixture bundles, docs passes.
- **Coordinator review is Opus regardless of who implemented.**
- Every spec for a new seat must close with a model and effort recommendation
  (`docs/HANDOVER-6.md` §7b).
