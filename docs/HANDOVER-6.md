# HANDOVER-6 — DeribitOrderPlacementApp coordinator seat

**This is the entry point and it is SELF-CONTAINED.** Read this, then verify it. You do **not**
need `HANDOVER-3/4/5` — their live content is folded in below and ⚠ **two of H-4 §5's "binding"
runtime facts were later reversed**, so reading them as binding is now a hazard. They are history:
`ARCHIVE-closed-milestones.md`. (The one exception: H-3 §12 is still the ntfy server-side poll
recipe.)

**Where things live — one canonical home each, deliberately not duplicated:**

| Need | Read |
|---|---|
| Live state, open items, queue | **this doc** §1–§3, and memory `era-state-checkpoint` |
| Invariants · runtime bite-list · seat rules | **this doc** §5–§7 |
| Settled rulings / do-not-re-propose | memory `standing-rulings-in-force` |
| VB · UIA · PowerShell · census traps | memory `winforms-harness-quirks` |
| Anything CLOSED | `ARCHIVE-closed-milestones.md` (index → the real doc chain) |
| Consumer behaviour (frozen) | `integration-contract-verdictengine.md` — §8 is the binding C1 spec |

## 1. State — verify it, never trust this text

`git rev-parse HEAD origin/master` · `tools/checks/verify-gate.ps1` · re-run the censuses.
A docs commit can never state its own sha, so any number here is at least one short on arrival.

- **origin/master `234131a`** (owner pushed 2026-08-02 01:38 +0800; owner is the only pusher).
  HEAD is ahead by the docs commits since.
- **Gate: GATE PASSED, OrderCheck 173/173** at `2308122`. Everything above it is **docs-only**
  (`git log --name-only 2308122..HEAD` — no `.vb`/`.vbproj`), so it carries.
- **Censuses, `frmMainPageV2.vb`-scoped** — a repo-wide grep inflates several and reads as drift:
  `emergencyFired` 10 · `IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 · `RecordCommandedSLPrice` 3
  · `slUpdateFailures = 0` 1 · `TakerFeeRate` 0 · `isPlacingOrder` 13 ·
  `lastPlacementAdmittedUtc` 13 · `placedOrderSizeUsd` 18. **These are OCCURRENCE counts (68 total
  across 64 lines)** — a count-mode grep returns 64 and reads as four missing.

## 2. Open — ALL owner-side. Nothing is in flight; no implementer seat is running.

**Everything below gates the same thing: ticking `Risk-size` to enable N2.** The three sizing
inputs must be set **together**, because they compose.

1. **🚨 `risk_per_trade_usd` / `max_size_usd` = 25 / 500 — the live footgun.** A realistic $200 stop
   computes ~7875 and caps to 500, so **every signal gets a flat 500** and N2 is "on" while doing
   nothing risk-shaped. Fix before ticking, not after.
2. **Circuit breaker `$10`** — gates the BRIDGE path; could stop a session fast at risk-sized
   notionals. Set deliberately.
3. **🚨 Session policy is ENABLED, and the third bucket is the one that bites.** Configured:
   `LONDON = MEDIUM | CONFIRMED | 0.5`, `ASIA = HIGH,MEDIUM | any | 0.75`. The multiplier applies
   **on top of** the risk size, exactly once. **`NY` (UTC ≥ 13:00) has NO entry, and an absent key
   is not a refusal** — `RuleFor` (`SessionPolicy.vb:143`) falls back to `DefaultRule()` (`:56`) =
   **`HIGH,MEDIUM | any | 1.0`**. So NY is at once the **least restricted** bucket and the
   **largest**: full size, **2× LONDON, 1.33× ASIA**. Owner runs UTC+8 ⇒ **NY is local
   21:00–07:59**, so the biggest, least-gated notional lands overnight.
4. **Journal rows `#95` and `#100`** — testnet-era, named in no deletion list. The x64 `trades.db`
   holds 91 live rows; after `#89` only these two remain. `#100` postdates N1c's #99 and appears in
   no doc. Owner's call, not a defect. *(The named list — #90–#93 / #96–#98 / #99 — is fully
   cleared.)*
5. **Engine relay owed (NEW 2026-08-02, C1/E1) — one line, cross-app.** Contract §8.1's
   `File.Replace` wording was amended here; the engine's `signal-bridge-v1-proposal.md` §10.2
   repeats it verbatim and needs the same correction. **A coordinated docs note, not a schema bump.**
   Neither seat writes cross-repo, so this only moves when you carry it. Full text: contract §9's
   2026-08-02 entry.
6. **🚨 C1 escalation E6 — TWO RULINGS OWED, raised 2026-08-04 before the emitter was written.**
   Both are gaps in spec §4's trigger list, both the same shape (a schema field no trigger can
   change), and both only became defects once §4(d) ruled the heartbeat *republishes* instead of
   re-reading. **E6a — `executor.ws` has no trigger at all**, so a WS drop heartbeats `"OK"`
   forever; contract §8.5's list has the same gap, so it is a coordinated docs note like E1's.
   **E6b — nothing publishes at startup**, so a fresh idle flat app writes no file and §Acceptances
   5 and 6 have no instrument (H-6 §7b lesson 1 again). Neither is implemented; both insertion
   points are recorded so either ruling is a one-line change. Full text + recommended arms:
   `impl-report-c1-feedback-emitter.md` §5.
7. **C1 acceptances 3–7 are owner-runtime and unrun** — the run sheet is
   `impl-report-c1-feedback-emitter.md` §7, written to be executable without the implementer.
   Acceptance 4 (the flat trap) is the one that matters and needs a real open→close cycle;
   log-only inspection cannot prove it.
8. AWS §9 migration (`production-cutover-checklist.md`) · the size ladder.
9. Optional, non-blocking: a physical owner-mouse double-click on `Mkt. BUY` (the SF2 burst
   instrument is UIA-driven, so a human double-click is still unobserved).

## 3. Queue

**N2 enable (owner) → C1 E6 rulings + acceptances 3–7 (owner) → coordinator review of C1 →
`ROADMAP-2026-08.md` §5 backlog.**

- N2 is code-APPROVED, UNBLOCKED and ships DISABLED. `risk_size_bridge_trades = False`, verified in
  the bin. The `Risk-size` checkbox **exists** since the 2026-08-02 x64 rebuild.
- **C1 emitter: IMPLEMENTED 2026-08-04, gate-green, SHIPS OFF — awaiting the E6 rulings (§2.6),
  the owner-runtime acceptances 3–7 (§2.7), and then the coordinator's adversarial review.**
  Five commits `c27951b..`; impl report `impl-report-c1-feedback-emitter.md` (its §3 is the
  what-was-NOT-established list and is the first thing to read). Gate `GATE PASSED`, **OrderCheck
  173/173 → 227/227**; the nine censuses are UNCHANGED at every commit.
  **E3 is now ESTABLISHED, not assumed: `positionSizeUSD` is signed and NEGATIVE on a short**,
  proven from two real testnet shorts (#99, #68) via the reduce direction, which is derived from
  the position sign alone — impl report §3. E1/E2/E5 implemented as ruled; E4's ruling is honoured
  (the heartbeat republishes and never re-reads) and **its revisit trigger is untouched and still
  binding**. E1/E2/E3/E5 were owner-ticked, E4 resolved against the engine's mirror.
  **Contract §8.1 is AMENDED in this repo** (`atomic (temp + atomic replace)`, logged in §9) — the
  first change to the frozen contract since 2026-07-28, and it is wording only: no schema, field,
  enum or behaviour change.
  **Three obligations outlive the rulings and are why §0 was kept in full:** E1's engine relay
  (owner, §2) · **E3 — the `size_usd` sign is still NOT ESTABLISHED**, and the tick approved the
  approach, not the fact: prove it from a real short before writing the mapping · **E4's binding
  revisit trigger** — re-open before ANY consumer *records* the `avg_entry` join rather than
  re-deriving it (T7 / v2.1), because §10.3/§10.4 is what makes accepting it safe today.
  Contract **§8 stays the binding schema** — the spec implements it and never restates it. Phase-2
  actionable exits stay fenced behind a separate signal-schema-v2 amendment — **never parse
  `hold_status`**.

## 4. The owner's x64 bin — CURRENT as of 2026-08-02 01:33

All four era symbols present (`RiskSizedBase`, `placedOrderSizeUsd`, `isPlacingOrder`,
`lastPlacementAdmittedUtc`); `secrets.json` reads `Environment = testnet`, byte-identical to source.

⚠ **Check the `Environment` key, not the file date.** "Did the rebuild overwrite `secrets.json`?"
is **not decidable from timestamps** — MSBuild `PreserveNewest` stamps the copy with the *source's*
mtime, so a copy and a skipped copy look identical afterwards. **After ANY x64 rebuild, re-read
`Environment` AND the window title before clicking Connect** — this has bitten in both directions.
**The gate does NOT build the x64 bin** (AnyCPU only), and **a harness-bin observation says nothing
about the owner's bin** — separate settings file, DB and journal.

## 5. Runtime facts that bite — SUPERSEDES H-4 §5 (two of which were reversed)

1. **The payload must LAND while the bridge is STARTED.** Evaluation is file-change-only; START
   does not re-evaluate the on-disk payload; the staleness tick can only *stop*. Engine stopped ⇒
   write twice (write → START → write); that double write is a harness artefact, not production.
   Freshness = `2.5 × exec_resolution_min`. Mode/ARM/Started reset every app start.
2. **🚨 Bridge payload tests: STOP the engine → run → `restore-payload.ps1` → RESTART the engine.**
   *(H-4 §5's "never stop the engine" is INVERTED — owner ruling 2026-08-01.)* Two footguns, both
   hit for real: forgetting to restart (nothing warns you no signals are arriving), and — engine
   stopped — `write-payload.ps1` defaulting to the **LIVE** payload path, clobbering
   `C:\Dev\DeribitBridge\verdict_signal.json`. Its first-write backup plus `restore-payload.ps1` is
   the only safety net: **restore is mandatory, not cleanup.**
3. **Harness-driven placement IS permitted** on a TESTNET-titled, harness-launched session, through
   `tools/place-and-verify.ps1` only. *(H-4 §5's "owner mouse clicks only" is SUPERSEDED — the
   harness was exonerated 53/53.)* The owner still drives every **trade decision, ARM and START**.
4. **🚨 `FormClosing` persists all 11 geometry fields unconditionally** — a runtime test that
   tightens geometry clobbers the owner's real trading values on exit, in the same file that holds
   the session policy and the breaker. Back up, restore, then VERIFY by relaunching and reading a
   box back.
5. **ATRSlip must be CHECKED** or NEITHER chase-abort arm evaluates — both sit under that one
   switch (`If(maxSlippageATRchecked, ChaseAbortReason(...), Nothing)`). Unchecked yields a silent
   null that reads like a pass. Runbooks have omitted it twice.
6. **Entry is reference-only** — the app enters at top-of-book, so a far `-Entry` cannot make a
   bridge entry rest (it fills in ~1 s). `rejected: position open` is UNREACHABLE (gate 4.6 reads
   the same `positionSizeUSD` as placement, so it stops at `refused: not_flat` first). The
   deterministic post-staging rejection is **`cancel pending`** — a 4-second window from Cancel All.
7. **Fill price ≠ trigger price** on the emergency path — the cap trips, then CancelOrder + the
   market reduce are two more WS round-trips. A gap is not a late cap.
8. **🚨 A placement log line is NOT evidence of position size.** N2's acceptance 3b logged a correct
   `Buy limit order placed For 310` against an actual position of **10**. **The reduce is
   exchange-derived and is the authority.** This is the single most expensive trap of the era.
9. **Divergence tests need the Amount box ABOVE the min-10 clamp** (e.g. 40). At box 10 a `0.5` mult
   clamps back to 10 and there is nothing to observe — the blind spot that hid the N2b defect for
   weeks. Directly relevant to any N2 acceptance.
10. **Harness payload timing:** engine stopped ⇒ freshness is `2.5 × exec_resolution_min` and
    `write-payload.ps1` emits `1`, a 2.5-minute window — shorter than a human round-trip to START,
    after which `[BRIDGE] auto-STOP: stale payload` fires. **Patch `exec_resolution_min` to 15 in
    the payload before START** (~37 min) and the race disappears. Cost two runs.
11. **The real `bridge-dispositions.log` is in the x64 bin** (the owner's VS profile). The AnyCPU bin
    has the harness's own copy — accidental but useful isolation.
12. Worktree gate runs need SHORT paths (`SQLite.Interop.dll` 0x800700CE).

## 6. Invariants — folded from H-4 §4, corrected

1. **Session policy** is evaluated AFTER the whole contract-§4.4 chain; gates the PINNED confidence
   enum (STRONG/WEAK are input aliases) + `verdict_context` as opaque stable identifiers;
   `refused: policy(SESSION/dim)`; disabled ⇒ structurally silent. Buckets are **UTC**
   (ASIA <08, LONDON 08–12:59, NY ≥13) while the Inclusion window stays **UTC+8 local** — two
   clocks, deliberate. An unconfigured bucket ⇒ `DefaultRule()`, not a refusal (§2.3).
2. **`size_mult` / `EffectiveSizeUsd`: unity passes through UNTOUCHED.** Floor + min-10 clamp only
   on real reductions, applied EXACTLY once at the act/would-act site; `refused: size` reads raw.
   `sizeUsdOverride = 0` ⇒ byte-identical legacy paths.
3. **Disposition file cardinality is FROZEN: one row per payload, written at consumption.**
   Post-`acted` outcomes (chase aborts) live in the host-log cancel REASON, never a second row.
   The append is unconditional in every mode/state; the host log stays filtered.
4. **The SF2 debounce lives in the six placement handlers ONLY.** `FlattenPositionAsync` and the two
   emergency sites reach `SendReduceMarketOrderAsync` directly and **must stay that way** — the
   emergency exclusion is the thing to protect in any future change there.
5. **Notifier** is fire-and-forget / fail-silent / self-rate-limited (urgent bypasses), inert
   without `ntfy_url`; the topic URL is a **credential** — never logged or echoed.
6. **Signal-tag lifecycle:** pending at bridge act → promoted at entry fill → cleared in BOTH cancel
   teardowns AND on definitive placement refusals (`TimedOut` carve-out: the tag survives) →
   recorded + cleared at close. Columns are TEXT.
7. **Persisted gate config = breaker + session policy only** (+ the standing item-A set). Cooloff,
   window and tiers reset per start; Mode/ARM/Started never persist.
8. **All four reposition slippage gates measure the OWN-SIDE quote**; ATR fallback period = 7,
   mirroring the engine. The four gates grep as `ChaseAbortReason`.
9. **The loss-cap anchor** `emergencyBaseline` moves only on adopt / manual edit / restore — NEVER
   the chase. Post-trigger, `placedStopLossPrice` is the chase reference. Deliberately divergent.

## 7. Methodology + seat rules

Spec → fresh implementer seat → impl report → **coordinator review = verify the actual code AND
execute the gate AND re-run the censuses AND an adversarial pass**. Owner is the only pusher;
single branch; one implementer at a time; docs tracked and committed with the work; the engine repo
(`C:\Dev\DeribitVerdictEngine`) is **READ-ONLY** and cross-app decisions go through the owner.
**Spec defects escalate to the owner BEFORE implementing**; rulings fold back into the specs so
future greps aren't stale. **Safety boundary: seats never place trades or arm the bridge** — the
owner drives every trade, ARM and START. There is **no second model seat** (Fable dissolved);
items touching a settled ruling go to the OWNER, who is the arbiter.

### 7a. Model + effort tiering (restored from H-4 §6 — the 2026-08-02 trim dropped it)

- **Opus, HIGH effort** — anything touching the **order / SL / receive / bridge / act** paths.
  Non-negotiable: that is the LIVE trading surface, and its failure modes are silent.
- **Sonnet, medium** — genuinely mechanical work: turnkey renames, fixture bundles, tooling scripts
  in `tools/`, docs-only passes.
- **Coordinator review is Opus regardless of who implemented.** The review is the adversarial pass;
  tiering down the reviewer defeats it.

### 7b. 🚨 RULING 2026-08-03 — every spec for a new implementer seat MUST close with a model + effort recommendation

**Binding, and it is not satisfied by a bare tier label.** The recommendation states:

1. **Model and effort** (e.g. "Opus, HIGH effort, fresh conversation").
2. **Why — tied to what the work actually touches**, not to how large it is. A small edit in the
   receive path outranks a large one in `tools/`.
3. **Where the thinking should go** — name the two or three places in the spec that are subtle
   enough to be got wrong by a competent implementer working quickly.
4. **What would change the answer** — the conditions under which a lower tier would be right, so the
   owner can economise deliberately rather than by guess.

**Why this is a ruling and not a nicety:** the owner launches every seat and pays for it, so the
model choice is theirs — but it depends on facts only the spec author holds, having just read the
code. Leaving it implicit pushes a cost/risk decision onto the person with the least context about
it. **The recommendation is advisory; the owner still picks.**

**The lessons that keep earning:**

1. **Verify what is IN FORCE — including that an acceptance's instrument can EXIST.** Twice an
   acceptance named an observable unreachable in the code as shipped; each spawned a follow-up spec.
2. **An acceptance must test the DEFECT, not the fix's theory of it.** SF v1 was a correct
   implementation of a wrong mechanism and read as convincing until runtime contradicted it.
3. **Statement ORDER cannot be checked from a unified diff.** Open the file.
4. **Prove every conjunct or claim nothing** (the N2b D1 trap).
5. **Prose moves censuses.** Run them even for comment-only commits.
6. **A doc is not evidence about a doc — and the staleness is RECURSIVE.** The 2026-08-02 audit
   cleared four already-done items, then left two more in the file it had just corrected. Treat
   "I just fixed this file" as no evidence about that file. Four structural causes, each with a real
   instance here: a review that lands somewhere unusual reads as owed forever · a precondition
   sentence outlives its precondition (no queue-level check catches it) · a satisfied state that was
   never an open item is invisible to a queue audit · **an unconfigured default is invisible to both
   a queue audit and an artefact read** — so **when config is a partial map, enumerate the DOMAIN,
   not the keys.**
