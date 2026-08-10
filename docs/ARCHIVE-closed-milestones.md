# ARCHIVE — closed milestones (index only; NOT part of the read path)

**Nothing here is open.** This file exists so `HANDOVER-6.md` and `ROADMAP-2026-08.md` can stay
short: it holds the one-line verdict per closed milestone plus the doc chain that carries the real
detail. **Do not read this file to get up to speed** — read `HANDOVER-6.md`. Come here only when
you need the reasoning behind a specific closed item.

**It carries three things, all backward-looking:** ① the closed-milestone index (below), ② retired
sentences, quoted and labelled rather than deleted, and ③ — added 2026-08-07 — **the trim
archaeology: what each doc collapse removed, where it went, and the two commands that recover
anything ever written here.** ③ is the answer to *"were the spent sections archived or just
deleted?"* — and it is not a rhetorical question: three items were lost by trims and recovered.

Rule of the house: **per-milestone detail lives in the spec → spec-back → impl-report → review →
runtime-record chain**, never re-narrated in a handover or roadmap row. These entries are pointers.

## Superseded handovers — HISTORY ONLY, not binding

`HANDOVER-3.md` · `HANDOVER-4.md` · `HANDOVER-5.md` are **superseded and carry stale instructions**.
`HANDOVER-6.md` is self-contained; it does not require reading any of them.

⚠ **Two H-4 §5 "binding" runtime facts were later REVERSED — do not act on that section:**
- *"Trade-placing steps: OWNER mouse clicks only"* — superseded 2026-08-01. Harness-driven placement
  IS permitted on a TESTNET-titled, harness-launched session, through `tools/place-and-verify.ps1`.
- *"Consumer-only payload tests: the isolated harness `bridge.json` protocol — **never stop the
  engine**"* — **inverted** by owner ruling 2026-08-01: **STOP the engine → run →
  `restore-payload.ps1` → RESTART the engine.**

H-4 §4's invariants were folded (corrected) into `HANDOVER-6.md` §7. H-3 §12 is still the ntfy
server-side poll recipe — the one section of the old handovers with live reference value.

## Closed milestones — 2026-06 → 2026-08

| # | Milestone | Verdict | Chain |
|---|---|---|---|
| Q1 | Remote ntfy notifier | **DONE** 2026-07-25, owner-accepted incl. A-B-A disabled-parity proven server-side | `spec-quickwins-notifier-signalcols.md` → `impl-report-quickwins.md` → `review-quickwins.md` → `handover-quickwins-runtime-acceptance.md` |
| Q2 | Signal columns in the trade DB | **DONE** 2026-07-25 (`SignalId`/`SignalConfidence`, TEXT per ruling a; View Trades grid shows them) | same chain |
| Q3 | Ops tooling (`backup-orderapp.ps1`, `policy-report.ps1`) | **DONE** 2026-07-25 | same chain |
| — | Session policy gate (Phase 3) | **DONE + ENABLED** — LONDON/ASIA configured; NY deliberately unconfigured ⇒ defaults (see H-6 §4) | `spec-session-policy-gate.md` → `impl-report-…` → `review-…` |
| — | Soak + column-level join review | **DONE + CLEAN** 2026-07-22 — 1404 rows / 15 instances, ZERO column mismatches. **Do not re-raise.** | `review-soak-join-2026-07-22.md` |
| — | Live-at-min-size interlock ladder | **DONE** 2026-07-23, CSV-verified | `runtime-record-live-ladder-2026-07-23.md` |
| N1 | Emergency-hoist | **DONE** 2026-07-28, runtime-accepted (#96–#98). *Framing corrected after:* the "~5 s delay" was never reachable (the backoff was dead code); the real pre-N1 exposure was ≤ 333 ms **plus a latent double-fire race, which N1 closed** — the more valuable half. | `spec-emergency-hoist.md` chain + `spec-back-emergency-hoist-acceptance-2026-07-28.md` + `runtime-record-emergency-hoist-2026-07-27.md` |
| N1b | SL-backoff coupling | **DONE** 2026-07-30 — red ids **223346/223348/223350 only**; the backoff's first reachable trigger ever. Its acceptance-2 defect spawned N1c. | `spec-sl-backoff-coupling.md` → `review-…` + `spec-back-sl-backoff-escalation-defect.md` |
| N1c | SL-backoff confirmed reset | **DONE + CLOSED** 2026-07-31, runtime-accepted on trade #99 (backoff climbed 0.7 → 1.3 → 2.7 s; `SL update rate limited` printed with no pump). The optimistic reset had pinned the backoff at 666 ms. | `spec-sl-backoff-confirmed-reset.md` → `impl-report-…` → `runtime-record-sl-backoff-2026-07-31.md` |
| EV | Chase budget (EV floor) | **CLOSED END-TO-END** 2026-08-01 — manual arm AND bridge-act leg. Disposition cardinality held: exactly one row per payload, the abort adding none. Ships OFF (`min_net_move_pct = 0`). | `spec-ev-chase-budget.md` → `spec-back-…` → `review-ev-chase-budget.md` → `runtime-record-ev-chase-budget-2026-08-01.md` |
| — | Fee-comms repoint | **CLOSED** 2026-07-30 — 2024 `TakerFeeRate` gone; comms derives from `taker_fee_bps` (32 → 22 at 64k). ⚠ Its review was appended to the **spec**, not a standalone `review-….md` — which is why it read as owed for three days. | `spec-fee-comms-repoint.md` (review is inside it, `fd2604e`) + `impl-report-fee-comms-repoint.md` |
| WATCH | Triple-placement investigation | **CLOSED** 2026-08-01 — harness EXONERATED (53/53 single invokes → one order); the defect was app-side. Old "leans harness-side" premise was wrong. **⚠ Retired-sentence note below.** | `investigation-triple-placement-2026-08-01.md` |
| SF | Placement single-flight v1 | **SUPERSEDED by SF2, code KEPT.** Failed acceptance 2 with the defect intact — the latch proves its own irrelevance (had any two overlapped, one would have been blocked). **A RATE problem, not an OVERLAP problem.** | `spec-placement-single-flight.md` → `review-placement-single-flight.md` |
| SF2 | Placement debounce | **DONE + CLOSED** 2026-08-01 — 500 ms debounce ahead of the v1 latch. The burst that gave 3 entries + reduce 30 now gives 1 + reduce 10. | `spec-placement-single-flight-v2.md` → `review-placement-debounce.md` |
| N2b | Chase preserve placed size | **DONE + CLOSED** 2026-08-01, all five runtime acceptances PASSED. Pre-existing defect, not an N2 regression: both chase edits re-derived `amount` from the Amount box, so session-policy `size_mult` had been silently reverted since it shipped — invisible only because live-at-min-size clamped every reduction back to 10. | `spec-chase-preserve-placed-size.md` → `spec-back-…` → `review-…` → `runtime-record-chase-preserve-placed-size-2026-08-01.md` |
| N2 | Risk-sized bridge trades | **CODE APPROVED + UNBLOCKED** 2026-08-01, **ships DISABLED** — the only milestone with an owner action left (tick `Risk-size`); see H-6 §4. Acceptances 1/3/4/5 + the §2 screenshot passed; 3b failed on the N2b defect above and was re-run green as N2b's acceptance 2. | `spec-risk-sized-bridge-trades.md` + `spec-back-risk-sized-bridge-defects.md` (five defects, all upheld) → `review-risk-sized-bridge-trades.md` |
| C1 | v2 feedback file — **documentation phase** | **CLOSED both sides** 2026-07-29 — proposal → engine ACCEPT (+3 refinements) → ack → trader tick T1–T8. **Contract §8 is the binding spec**; engine mirror is their §10. ~~*The two implementation builds remain (ours = the emitter, still the live queue item).*~~ **RETIRED 2026-08-07 — our emitter closed 2026-08-06 (row below); only the engine's consumption build remains, on their queue.** | `proposal-c1-v2-feedback-file.md` → `ack-c1-v2-feedback-file.md` → `integration-contract-verdictengine.md` §8 |
| C1 | v2 feedback file — **the emitter (our build)** | **DONE + CLOSED** 2026-08-06 — five escalations ruled (E1–E5) plus E6 after, implemented, reviewed, defect **D1 fixed and verified**, **all five acceptances 3–7 PASSED**, spec-back **SB1–SB5** upheld and folded. Gate `GATE PASSED` at every commit, OrderCheck **173 → 268**. **Ships OFF** — `feedback_output_path` absent from `bridge.json` ⇒ no timer, no worker, no file. Two live riders that outlived the build: **E4's revisit trigger** (re-open before any consumer *records* the `avg_entry` join) and the **owner enable** (`ROADMAP-2026-08.md` §1.5). | `spec-c1-feedback-emitter.md` → `impl-report-c1-feedback-emitter.md` (read §8 before §3) → `review-c1-feedback-emitter.md` → `runtime-record-c1-feedback-emitter-2026-08-06.md` → `spec-back-c1-acceptance-2026-08-06.md` |
| SB3 | Harness exact-match element selection | **DONE + CLOSED + APPROVED** 2026-08-06 (`188bdab`, `3611d49`, `9ecf40d`). `Test-ElementMatch` matched by `IndexOf >= 0`, first-in-enumeration wins — `txtTrigger` ⊂ `txtTriggerOffset`, so asking for `Trig. P.` silently set `Trig. O.` **and reported success**, leaving the SL ~$6 below market for a whole live position. Replaced by `Select-BestMatchIndex`: exact `AutomationId` → exact `Name` → substring, **any tie at the winning tier fails loudly** (exit 3, every candidate named). All five drive scripts gather-then-decide via `Select-MatchingElement`. ⚠ **The `-Exact` switch the old backlog row asked for was the WRONG SHAPE** — a switch is only correct when the caller remembers to pass it, converting a silent wrong-control into a silent wrong-control-unless-you-remembered. Exact-first-by-default requires nothing of any caller. | `spec-harness-exact-match.md` → `impl-report-harness-exact-match.md` → `review-harness-exact-match.md` |

| OW | Harness owned-window / settings-window flakiness | **DONE + CLOSED** 2026-08-07. The arc is the point: spec → fix built exactly to spec → **implementer spec-back showed the fix was a deterministic regression** → 39-round probe showed **the specced cause never existed** (the window is *never* a desktop-root child, 0/39; the real cause was render latency) → bounded-wait fix → **coordinator review found `D1`, a defect in code: the wait could never retry** (500 ms deadline vs a 740–950 ms first attempt in a fresh process) → fixed and verified. Final: `Wait-ForMatchingElement`, attempt 1 outside the budget, `Ambiguous` never waited out. Gate 268/268 at every step; zero `.vb` touched. **Lessons live in `HANDOVER-6.md` §7 items 8 and 9, not here.** | `spec-harness-owned-window.md` (carries the ruling, the withdrawn §2, and R6–R12) → `spec-back-harness-owned-window-duplicate-controls.md` → `impl-report-harness-owned-window-probe.md` → `impl-report-harness-owned-window-fix.md` → `impl-report-harness-owned-window-d1-fix.md` |

## Retired-sentence note — the WATCH row's live-exposure warning

`ROADMAP-2026-08.md` §5 carried this warning on the triple-placement WATCH paragraph:

> *"this exposure is live on LIVE — a double-click places two orders — until
> `spec-placement-single-flight.md` lands (Opus HIGH, owner tick pending)"*

**It outlived the fix by a day.** SF2 closed the exposure 2026-08-01; the sentence was cleared by
the 2026-08-02 queue audit, and the whole WATCH paragraph moved here when §5 was reduced to the
open hygiene items.

**Recorded deliberately, because the string still exists in git history.** Anyone who greps it —
in an old ROADMAP revision, `HANDOVER-5`, or the investigation doc — is looking at a **retired**
claim, not a live one. The exposure is closed: SF2's 500 ms debounce, `550d673`. Owner-mouse-only
no longer applies either; harness placement goes through `tools/place-and-verify.ps1`.

*(Kept at the owner's instruction: a retired claim is safer quoted-and-labelled than silently
deleted, because deletion is indistinguishable from "never written" to the next grep.)*

## Trim archaeology — what the collapses removed, and how to get any of it back

**Added 2026-08-07, because "was anything deleted rather than archived?" turned out to have a
two-part answer.**

**Part one — nothing was deleted from the REPO.** Every superseded handover (`HANDOVER.md`,
`-2`…`-5`), the prior-era `ROADMAP-2026-07.md`, and all ~140 per-milestone
`spec-` / `spec-back-` / `impl-report-` / `review-` / `runtime-record-` docs are **still tracked
files**. The collapses re-pointed the read path; they did not remove history. Verified by listing
`git ls-files docs` on 2026-08-07.

**Part two — but text removed FROM a doc lives only in git history, and that has cost us three
times.** A file that is rewritten in place (H-6 lost 173 lines to `24423dd`) leaves nothing behind
in the working tree.

**Recovery recipe — anything ever written in this repo is two commands away:**

```bash
git log --oneline --follow -- docs/HANDOVER-6.md
```

Then `git show <commit>^:docs/HANDOVER-6.md` for the file as it stood *before* that commit, or
`git show <commit> -- docs/HANDOVER-6.md` for exactly what changed. To hunt a remembered phrase
across all of history: `git log -S'the phrase' --oneline -- docs`.

**Known losses and their dispositions — the ledger, so this is a pattern rather than three
anecdotes:**

| Removed by | What was lost | How it surfaced | Disposition |
|---|---|---|---|
| `24423dd` (H-4 §6 → H-6 §7) | The **model/effort tiering** table | The owner asked which model to use, a day later | Restored as H-6 **§7a** |
| `24423dd` (live traps filed to this archive) | Three **runtime testing traps** needed *during* a run | Next runtime pass needed them | Pulled back into H-6 **§5** (`95e8658`) |
| `24423dd` (H-6 §5's ATRSlip bullet) | *"…or **the bridge refuses to START in Live mode**"* — **it survived nowhere else** | 2026-08-07, by **diffing the trim**, not by reading | Restored as H-6 **§5.5(a)**, re-derived from `SignalBridge.vb:305-317` |
| `24423dd` (ROADMAP §5) | The **descriptions** behind `A2` / `B2` / `F1` / fractional-offset — reduced to bare labels | 2026-08-07 ROADMAP audit | Restored from `HANDOVER-3.md` §3 |
| `24423dd` (ROADMAP §5) | **cross-app ATR 7-vs-14** and the **`_autotradesettings` leftovers** — dropped from every list | 2026-08-07 ROADMAP audit | Both verified **settled in code**; recorded as answers in `ROADMAP-2026-08.md` §8.2 |

🚨 **The rule this ledger earns: a trim commit MUST enumerate what it removed and say where each
piece went — and "nothing was lost, the detail is in the chain" is a claim to be checked item by
item, not asserted.** `24423dd`'s own message says *"with nothing lost — closed detail was already
in the spec/review chains and is now indexed rather than re-narrated."* That was **true of the
closed detail and false of at least one live clause**, and the gap went unnoticed for five days
because the sentence sounded like a verification and was actually a prediction.

**Corollary for the next trim: diff for IMPERATIVES, not for length.** The lost items were all
sentences that told a future seat what to *do* — a tier to pick, a trap to avoid, a checkbox that
blocks START. Prose compresses safely; instructions do not.

## Where each kind of closed thing lives (the map, so nothing has to be hunted)

| Kind | Home |
|---|---|
| Closed-milestone verdict + its doc chain | **this file's table**, above |
| The real per-milestone detail | the `spec-` → `spec-back-` → `impl-report-` → `review-` → `runtime-record-` chain, all still tracked |
| Superseded handovers | `HANDOVER.md`, `-2`…`-5` — still tracked, banner-marked, ⚠ two reversed facts in H-4 §5 |
| Prior-era plan of record | `ROADMAP-2026-07.md` |
| Retired *sentences* (quoted and labelled) | this file — the WATCH note above and the ledger here |
| Text removed by a trim | git history, via the recipe above |

## Where the reusable lessons went (deliberately NOT duplicated here)

The traps these milestones paid for are **live guidance**, so they live in the read path, not the
archive: the placement-log/reduce-authority trap, the min-10 clamp blind spot and the harness
payload-timing race are `HANDOVER-6.md` **§5**; the N2b D1 "prove every conjunct" trap and the rest
of the methodology are **§7**.
