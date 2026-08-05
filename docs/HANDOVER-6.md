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
5. **✅ Engine relay DONE 2026-08-04 (engine `5d1bd02`) — mirror and canonical text agree.** Both
   corrections applied to their `signal-bridge-v1-proposal.md` §10.2: the §8.1 atomic-write wording
   (E1) and the §8.5 trigger list (E6 — `ws` into (c), new (f)). Verified read-only, not taken on
   report. **Two findings from their side worth keeping:**
   (a) **E1 was a DOC defect on both sides, not a code defect on theirs** — `SignalEmitter.TryWrite`
   already guarded with `If File.Exists(path) Then File.Replace(...) Else File.Move(...)`, so the
   engine had it right in code while its comment described the unguarded version. The reasoning
   transferred even though the bug did not.
   (b) **E6 landed harder there than "trigger list" suggests:** their §10.4 *renders* `ws`, so a
   drop would have shown a stale `OK` on their interlock strip through the whole disconnect; and (f)
   is what makes their "file absent = OFF, file stale = dead" pair sound at all.
   ✅ **Their §10.2 question is ANSWERED AND ADOPTED — nothing is owed here.** They asked whether
   the mirror should carry the heartbeat-republishes ruling. Answer relayed and taken verbatim:
   **the mirror carries consumption-visible CONSEQUENCES, never emitter mechanics.** The rule itself
   stays canonical in contract §8.5; what travels is its consequence — *a fresh `generated_at_utc`
   proves the process is ALIVE, not that any field was recently observed.* Their §10.2 now carries
   that paragraph and cites §8.5 for the mechanism, and they adopted the general rule in their doc.
   That closes the mirror-drift class: both §10.2 corrections were restated mechanics, and neither
   could have existed under it.
   ⚠ *This bullet said "one open reply owed" for two turns after the reply had been sent and
   adopted — caught by the implementer seat, not by me, while it was editing around it. Third
   instance of §7 lesson 6 in this document.*
6. **✅ C1 escalation E6 — RULED 2026-08-03 and IMPLEMENTED 2026-08-04 (`5c2d6ed`, `dd43e59`).
   CLOSED; nothing owed.** **E6a** — `ws` now publishes at both edges (connect, and receive-loop
   exit above the reconnect branch, so it covers every way out). **E6b** — trigger (f) writes one
   snapshot at start when configured, immediately before the heartbeat is armed. Spec §4 and
   contract §8.5 were amended first; the relay in item 5 covers both. Fixture 8 was rescoped to the
   whole §8.3 domain in the same pass — **OrderCheck 227/227 → 264/264**. ⚠ **The former "`ws` is
   knowingly permanently stale" warning is WITHDRAWN**, but note it has never been *observed* firing:
   §7.4 of the impl report now carries the observation step. Addendum: that report's **§8**.
6b. **🚨 C1 defect D1 — ONE COMMIT OWED, and it comes BEFORE acceptance 7**
   (`review-c1-feedback-emitter.md` §4). A worker pre-empted between taking its id under `_gate` and
   acquiring `_writeGate` can write **after** the graceful-close write, with an older snapshot and a
   **lower** `feedback_id`. So the on-disk id goes backwards (§8.4 requires monotonic) and the last
   state on disk is not the executor's last true state — defeating exactly what trigger (e) exists
   for, and making §8.1's *"silence = dead executor"* dishonest at the one moment it matters.
   **Fix is one parameter:** give `WriteAtomic` an `isFinal` flag; on the non-final path re-check
   `_disposed` after acquiring `_writeGate`. Severity LOW–MEDIUM (narrow window, graceful close
   only), but **do it first** — otherwise an acceptance-7.1 failure reads as a mystery rather than
   as this.
7. **✅ C1 acceptances 4–7 RAN AND PASSED 2026-08-06** (harness-driven, TESTNET, owner-authorised
   position) — `runtime-record-c1-feedback-emitter-2026-08-06.md`. The flat trap is proven on a real
   open→close cycle with **both** conjuncts: trade #75 records entry **64806.50** (the retained
   `positionAvgEntry`) and **not** 64806.00 (`placedPrice`), so the retention was live at the very
   moment the file published `avg_entry: 0`. E6a's `ws` transition and E6b's (f) were **observed for
   the first time**. **⚠ ACCEPTANCE 3 IS THE ONLY ONE LEFT** — it needs mode Live + ARM + START
   (owner-only; `START` is on the harness deny list) and an engine stop, and it is the one that
   matters most now: **`mode` has still only ever been observed as `"OFF"`**, so E2's silent-failure
   string (`"LogOnly"` ≠ `"LOG_ONLY"`) is fixture evidence only. That record's §10 is the current
   what-is-NOT-proven list. **Do D1 first.**
8. AWS §9 migration (`production-cutover-checklist.md`) · the size ladder.
9. Optional, non-blocking: a physical owner-mouse double-click on `Mkt. BUY` (the SF2 burst
   instrument is UIA-driven, so a human double-click is still unobserved).

## 3. Queue

**ACTIVE THREAD: C1 D1 fix (implementer, one commit) → C1 acceptances 3–7 (owner) →
`ROADMAP-2026-08.md` §5 backlog.**

**INDEPENDENT, not queued behind anything: N2 enable (owner).** It is gated only on §2's three
sizing knobs, and C1 is gated only on D1 — **neither blocks the other**, and both ship off/disabled.

⚠ *Corrected 2026-08-04: this line read "N2 enable → C1 …" for two days, which was the ORIGINAL
plan — contract §8.7 says the emitter is "after N2 unless the trader reorders". **The trader
reordered by launching the C1 seat while N2 enable stayed pending, and nobody recorded it**, so the
line went on describing a sequence that reality had already overtaken. Not stale in the usual way —
it was true when written and no single word of it was ever wrong; what changed was the world.
§7 lesson 6's family, and the reason §8.7's escape clause needs writing down when it is used.*

- N2 is code-APPROVED, UNBLOCKED and ships DISABLED. `risk_size_bridge_trades = False`, verified in
  the bin. The `Risk-size` checkbox **exists** since the 2026-08-02 x64 rebuild.
- **C1 emitter: REVIEWED 2026-08-04 — code APPROVED with ONE defect, D1 (§2.6b).**
  `review-c1-feedback-emitter.md`. Gate re-executed by the reviewer (`GATE PASSED`, 264/264) and the
  nine censuses re-run; both independently, not on report.
  **The nominated claim — no `Return` between first field write and tail hook — HOLDS**, re-derived
  by a stricter method than the report used: the reviewer's first scan anchored on `^\s*Return` and
  would have missed VB's inline `If … Then Return`, which is the commonest form and exactly the one
  the claim is about. The corrected scan found one candidate (`:3327`), adjudicated as sitting
  inside `Me.Invoke(Sub() … )` and therefore returning from the lambda. **Fixture 8b's failability
  was also re-derived** rather than taken on report — dropping `ws` from `Serialize` takes the suite
  from 264/264 to a non-zero exit.
  Eight commits `c27951b..`; impl report `impl-report-c1-feedback-emitter.md` — **read its §8
  addendum first, then §3** (the what-was-NOT-established list; §8 supersedes two of its entries).
  Gate `GATE PASSED` at every commit, **OrderCheck 173/173 → 264/264**; the nine censuses are
  UNCHANGED at every commit.
  **E3 is now ESTABLISHED, not assumed: `positionSizeUSD` is signed and NEGATIVE on a short**,
  proven from two real testnet shorts (#99, #68) via the reduce direction, which is derived from
  the position sign alone — impl report §3. E1/E2/E5 implemented as ruled; E4's ruling is honoured
  (the heartbeat republishes and never re-reads) and **its revisit trigger is untouched and still
  binding**. E1/E2/E3/E5 were owner-ticked, E4 resolved against the engine's mirror.
  **Contract §8.1 is AMENDED in this repo** (`atomic (temp + atomic replace)`, logged in §9) — the
  first change to the frozen contract since 2026-07-28, and it is wording only: no schema, field,
  enum or behaviour change.
  **Of the three obligations that outlived the rulings, TWO ARE NOW DISCHARGED and one is not:**
  ~~E1's engine relay~~ **DONE** (§2.5, engine `5d1bd02`) · ~~**E3 — prove the `size_usd` sign from
  a real short**~~ **DONE 2026-08-04** (established negative-on-short from trades #99 and #68; the
  paragraph above is the record) · **⚠ E4's binding revisit trigger is the one still LIVE** —
  re-open before ANY consumer *records* the `avg_entry` join rather than re-deriving it (T7 / v2.1),
  because §10.3/§10.4 is what makes accepting it safe today. *(This paragraph contradicted the one
  above it for two days — the recursive-staleness class in §7b lesson 6, caught on re-read.)*
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
10. **Every atomic write is `File.WriteAllText(tmp)` + `File.Move(tmp, path, overwrite:=True)`, and
    this repo uses `File.Replace` NOWHERE.** Swept 2026-08-04 at the engine seat's suggestion: three
    sites, all conforming — `AppUserSettings.vb:207` · `SignalBridge.vb:407` (PersistState) ·
    `ExecutorFeedback.vb:530` (C1). **Our pattern is TOTAL where theirs needs a guard** —
    `File.Move(overwrite:=True)` works whether or not the destination exists, which is why E1 was
    never a code defect here. Append-only log writes (`crash.log`, `AutoTradeLog.txt`, the
    disposition log) are a different class and are deliberately not atomic.
    ⚠ One inherited dependency, benign but worth knowing: `SignalBridge.vb:425`'s comment asserts
    *"`File.Replace` on the engine side surfaces as rename/change"* — a claim about **another
    repo's** internals. It is safe either way because the watcher subscribes to Changed, Created
    **and** Renamed, but it is the mirror-drift class pointing inward.

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

**The spec must also ask the implementer to answer it in hindsight** — "was this tier right?" — as a
numbered impl-report section. C1's §6 is the pattern: it found no `PositionModelChanged` seam, so
Opus-HIGH was confirmed correct with the three near-miss candidates recorded and why each fails.
That is how the tiering table earns corrections instead of drifting.

### 7c. 🚨 RULING 2026-08-03 — the coordinator matches effort to the task in hand

**The seat modulates its own depth per task rather than running one gear all session.** The tiering
in §7a and in `CODE_AUDIT_FABLE5.md` §H applies to the coordinator's own work, not only to
implementer seats:

| Coordinator task | Depth |
|---|---|
| Reviewing code on the order/SL/receive/bridge/act paths · adversarial passes · rulings that touch the frozen contract | **high** |
| Spec writing · impl-report review · log diagnosis · escalation rulings | **medium** (the default) |
| Doc edits, memory updates, census runs, mechanical housekeeping | **low** |

⚠ **State the limit honestly: the seat cannot change the harness's own effort setting.** What it
controls is *depth of work* — how much it independently verifies, whether it re-derives a claim from
the artefact, whether it runs an adversarial pass. **When a task genuinely warrants a different
session effort than the one set, say so and let the owner change it** — do not silently do
high-effort work at a low setting, or the reverse.

**And the real spend on this repo is CONTEXT, not thinking depth** (`CODE_AUDIT_FABLE5.md` §H):
`frmMainPageV2.vb` alone is ~67k tokens. Use the section banners, the audits and the specs as the
map; read regions on demand; review via `git show` and targeted greps rather than re-reads. A
high-effort pass that re-reads the god-form has spent its budget in the wrong place.

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
7. 🚨 **A SUMMARY SILENTLY DROPS THE CAVEAT ITS SOURCE CARRIED — and nothing looks wrong.**
   Contributed by the engine seat 2026-08-04, from the E1 arc: their original spec
   (`audit-cleanup-pass-proposal.md:65`) stated the `File.Replace` existence-guard fallback
   explicitly and correctly. **Three separate summaries of it each dropped the guard** — a code
   comment, a method `<summary>`, and `architecture.md`, the last being the worst because it is read
   in full at every session start, so a seat inherits the wrong model *before opening any code*.
   The source was right the whole time; every derivative was wrong.
   **This is distinct from lesson 6.** There, a claim goes stale. Here nothing is stale and no
   single copy looks defective — the qualifier is simply absent, and absence reads as "there was
   never a qualifier." **It applies directly to this document**: H-6 summarises the specs, the
   archive summarises milestones, memory summarises H-6. Each layer is a chance to drop a caveat.
   **Mitigation: when you summarise a rule, carry its exception or do not carry the rule. And grep
   every copy before calling a correction done — code comments included, not only docs.**
