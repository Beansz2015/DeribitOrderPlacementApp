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

- **Entry point: `docs/HANDOVER-6.md`.** It is self-contained. Read it, then verify it.
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

## Jev doc re-ranker — the context saver (owner ruling 2026-10-06)

- **Fire it BEFORE grepping or reading docs** for any "where was this decided / defined?" question.
  Then read only the top hits, by line range.
- `powershell -NoProfile -File tools/checks/doc-reranker.ps1 -Query "<question>"`
- About 31 Jev calls and 10 s per query. It reads committed docs at `HEAD`, not the working tree.
- It excludes `docs/HANDOVER.md` and `docs/HANDOVER-2.md` through `-5.md`. It is advisory only and is
  never part of the gate.
- Jev returns probabilities. Never ask it for maths, counts or dates.
- Ported from the engine repo. The header of `tools/checks/doc-reranker.ps1` gives the source and
  what was left out.

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
