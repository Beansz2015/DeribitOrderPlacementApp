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

🚨 **THIS BLOCK IS STALE BY CONSTRUCTION. It has now been corrected three times** — `234131a`
(2026-08-02) → `9ecf40d` (2026-08-07) → the numbers below (2026-08-14). **Do not trust a single
figure here. Run the three commands above and believe those.** The numbers are kept only so a
successor can tell *movement* from *drift*.

- **origin/master `9f041fe`** — last verified 2026-08-14. The owner is the only pusher, so this
  moves only when they push, and the seat's commits sit ahead of it until then.
- **Gate: GATE PASSED, OrderCheck 289/289** — executed at HEAD `1b7ab75` on 2026-08-14, not
  inherited. *(173 → 227 → 264 → 268 → **289**; the last jump is the ws-edge feature's 21 fixtures.
  This bullet claimed 173/173 for five days after the count had moved four times, and 268/268 for a
  week after that.)*
- ⚠ **`git rev-parse --short HEAD origin/master` FAILS here** — `fatal: Needed a single revision`
  on git 2.55.0.windows.3, which refuses multiple revisions under `--short` (`--short=7` fails the
  same way). **Run the two revisions as separate calls.** The fatal is a git-version quirk, not a
  broken repo — worth knowing before it reads as drift in your first sixty seconds.
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
3. ✅ **OWNER RULING 2026-08-10 — NY stays UNCONFIGURED, deliberately. Do not re-raise it.**
   The owner's words: *"NY is to be the most flexible, so don't add an explicit entry yet."* So the
   fall-through below is now a **decision, not an oversight**, and a future seat should stop flagging
   it as a footgun. **The mechanics still matter and are unchanged** — an absent key is not a
   refusal, NY resolves to `HIGH,MEDIUM | any | 1.0`, and it is therefore both the least
   tier-restricted and the only full-size bucket, landing 21:00–07:59 local. *(Note it is not
   "unrestricted": `DefaultRule` still refuses LOW.)* Revisit only if the owner asks.
   *(Original text below, kept because the mechanics are the thing to understand.)*
   **🚨 Session policy is ENABLED, and the third bucket is the one that bites.** Configured:
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
   ⚠ **NOT re-verified on 2026-08-07 — carried on its original evidence, which is exactly the
   condition this document warns about.** Three routes were tried and all failed: `sqlite3` is not
   on PATH; the bin's .NET 9 `System.Data.SQLite.dll` will not load into Windows PowerShell 5.1
   (`ReflectionTypeLoadException` — the PS-5.1-is-.NET-Framework trap); and a throwaway `dotnet 9`
   console against a **copy** of the DB got `CantOpen (14)` from the native interop under the
   sandbox. **If this row matters, read it from the running app's View Trades grid** — that is the
   one reader known to work. The x64 `trades.db` was last written 2026-07-31, so the claim is at
   least not being invalidated by new rows.
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
6b. **✅ C1 defect D1 — FIXED (`cef0b9c`) and VERIFIED. CLOSED.** `WriteAtomic` gained `isFinal`;
   `ShouldWrite(isFinal, disposed) = isFinal OrElse Not disposed` is a pure fixture-pinnable seam.
   The reviewer checked the thing the fix could have broken: `_writeGate → _gate` is the only lock
   nesting in the class and no path holds `_gate` while taking `_writeGate`, so there is no deadlock.
   Gate 268/268. *(Original finding kept below for the record.)*
   ~~**ONE COMMIT OWED, and it comes BEFORE acceptance 7**~~
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
   the first time**.
   **✅ ACCEPTANCE 3 ALSO RAN AND PASSED the same day (§11 of that record) — ALL FIVE ARE CLOSED.**
   ⚠ **It needed NO ARM and NO START**, and that is the finding worth carrying: log-only *runs
   un-started by design* (`TryStart` refuses non-Live modes in those words) and gate 4.5's interlock
   is Live-only, so a payload produces a `would-act` disposition with neither toggle touched.
   **E2 IS NOW CLOSED AT RUNTIME** — `executor.mode` observed as `"LOG_ONLY"`, the pinned string,
   published by trigger (c) before any payload. The disposition matched the `bridge-dispositions.log`
   row character-for-character, and `last_signal` stayed byte-identical across heartbeats (the
   cardinality freeze, observed). The live `verdict_signal.json` was **never touched** — the harness
   `bridge.json` pointed `path` at a scratch file, verified byte-identical before and after.
   **Still unobserved:** `mode: "LIVE"` + an `acted (id …)` disposition (needs ARM/START), the `ws`
   DOWN edge, a `breaker_tripped` flip, a SHORT `size_usd`, and D1's race. §10 + §11.5 are the
   current what-is-NOT-proven list. *(D1 is done — see 6b. Nothing gates acceptance 3 but ARM/START.)*
   ⚠ **The `ws` DOWN edge entry needs care after 2026-08-14, because it is half closed.** The ws-edge
   audit trail shipped and is runtime-accepted (`runtime-record-ws-edge-audit-2026-08-14.md`), and a
   REAL disconnect was captured — so **E6a's DOWN publish site is now proven to execute**, which it
   was not before. But that run used the harness bin with the **emitter DISABLED**, so
   **`executor_feedback.json` carrying `"ws": "DOWN"` is still unobserved** and this entry stays
   open. Two claims, one word: do not let "the DOWN edge was observed" collapse them.
7b. **✅ C1 SPEC-BACK — ALL FIVE UPHELD AND FOLDED IN 2026-08-06**
   (`spec-back-c1-acceptance-2026-08-06.md`). **None was a code defect.** ⚠ **Numbered SB1–SB5, not
   D1–D5** — `D1` was already taken twice in live docs (the C1 review's write-ordering defect, and
   N2b's spec-back trap at §7.4), so a grep for "C1 D1" returned two different things in one
   feature. **Convention from here: `D` = a defect found in code · `SB` = a spec-back finding
   against the docs.**
   **SB1** — §Acceptance 3 needs **no** ARM/START; log-only runs the whole gate chain and yields a
   `would-act`, firing trigger (a). Verified: `TryStart` refuses non-Live (`SignalBridge.vb:308`) and
   gate 4.5 is `_mode = BridgeMode.Live`-guarded (`:726`), so it is skipped in log-only. **Spec
   AMENDED** — log-only is now the default route, Live an optional extension. That over-strict
   reading had deferred E2's runtime evidence behind an owner-only gate for two days.
   **SB2** — the live-payload clobber is avoidable; **§5.2 amended**, scratch path now preferred.
   Touches no ruling: the engine-stop refusal still gates on the **process**, not the path.
   **SB3 — harness defect, and it BIT:** `Test-ElementMatch` set `Trig. O.` instead of `Trig. P.`
   **and reported success**, leaving the SL ~$6 below market for the whole acceptance-4 position —
   the run survived only because price moved up. **Promoted out of the anytime backlog**
   (`ROADMAP-2026-08.md` §5; `tools/` only, Sonnet-medium).
   **SB4** — the harness bin's GATE CONFIG diverges and is *inverted* on which bucket is
   unrestricted; **§4 amended** (it had named only settings/DB/journal).
   **SB5** — a wildcard cleanup deleted `n2-settings.png`, unrecoverable; recorded, not excused, and
   the rule is now in memory `winforms-harness-quirks`.
8. AWS §9 migration (`production-cutover-checklist.md`) · the size ladder.
9. Optional, non-blocking: a physical owner-mouse double-click on `Mkt. BUY` (the SF2 burst
   instrument is UIA-driven, so a human double-click is still unobserved).

## 3. Queue

**NOTHING IS IN FLIGHT. C1 IS COMPLETE — all five acceptances passed 2026-08-06, including 3.**
The coordinator queue is `ROADMAP-2026-08.md` §5; the owner queue is that doc's §1.

### 🔴 STATE AS AT 2026-08-14 — read this before the older paragraphs below

**Both "pending enables" below are DONE. Do not re-raise them.**

| Thing | State |
|---|---|
| **C1 emitter** | ✅ **ENABLED** in the owner's x64 bin 2026-08-10. `feedback_output_path` set; file live. |
| **N2 risk sizing** | ✅ **ENABLED**, `risk_per_trade_usd = 1.0` · `max_size_usd = 2000` applied 2026-08-14. **Log-only verification COMPLETE — all three session buckets, 14/14 sizes exact.** |
| **N2 LIVE** | ❌ Never run. **Owner's decision alone.** Nothing technical blocks it. |
| **ws-edge audit** | ✅ Built, reviewed, **APPROVED** 2026-08-14 (`spec-ws-edge-audit.md` §Coordinator review). |
| **Uncapped Max Size** | 🟢 **SPEC WRITTEN, SEAT NOT LAUNCHED** — `spec-max-size-uncapped.md`. Opus/HIGH. |

🚨 **The owner's x64 bin does NOT carry the ws-edge code.** The gate is AnyCPU-only. Rebuild x64
before relying on `ws-edges.log` in a real session, then re-check the `Environment` key **and** the
`— TESTNET` title (§4).

⚠ *Corrected 2026-08-07: this line read "ACTIVE THREAD: C1 acceptance 3 (owner — the last one)"
while §2.7 of this same document already recorded acceptance 3 as PASSED — the two paragraphs
contradicted each other for a day. **Fourth instance of §7 lesson 6 in this file**, and the same
shape every time: the queue line is written once and then nothing re-reads it against the state
section below it.*

**Two owner actions were created by work FINISHING and were on no list until the 2026-08-07 audit:**
the **N2 enable** (`Risk-size`) and the **C1 emitter enable** (`feedback_output_path` in
`bridge.json`). Both now sit in `ROADMAP-2026-08.md` §1.4/§1.5. *A build that completes silently
manufactures an owner action, and a queue audit that clears what is listed will never find it.*

**INDEPENDENT, not queued behind anything: N2 enable (owner).** It is gated only on §2's three
sizing knobs, and **C1 is now complete** (D1 fixed, all acceptances passed) — **neither ever blocked
the other**, and both ship off/disabled.

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

## 4. The owner's x64 bin — REBUILT 2026-08-10 21:04, and it now carries C1

Verified in the binary, not inferred: `ExecutorFeedback` · `feedback_output_path` · the literal
startup string `Executor feedback: disabled` are all present. `secrets.json` reads
`Environment = testnet`.

🚨 **This heading used to read "CURRENT as of 2026-08-02 01:33" and it was WRONG for six days —
it cost a live debugging session on 2026-08-10.** The C1 emitter landed 2026-08-04/06 and **the x64
bin was never rebuilt**, so the owner edited `bridge.json` correctly, started the app, and got **no
startup line at all** — not "configured", not "disabled". The line is printed unconditionally
(`frmMainPageV2.vb:927-928`), so its *absence* proved the running exe had no emitter in it.

**Two rules out of that:**

1. **"CURRENT as of <date>" is a claim with a silent shelf life.** It expires the moment anything
   lands, and nothing re-reads it. **State what the bin CONTAINS, and re-derive it.** A symbol scan
   is three seconds:
   ```powershell
   $p='...\bin\x64\Debug\net9.0-windows8.0\DeribitOrderPlacementApp.dll'
   $b=[IO.File]::ReadAllBytes($p); ([Text.Encoding]::ASCII.GetString($b)) -match 'ExecutorFeedback'
   ```
2. **A missing log line is evidence about the BINARY, not about the config.** When a feature that
   should announce itself says nothing at all, suspect the build before the settings. The config was
   correct the whole time.

⚠ **`[IO.File]::ReadAllBytes` and friends resolve RELATIVE paths against the process working
directory, which PowerShell's `cd` does NOT change.** A relative path there silently reads the wrong
file — or fails — and on this box it produced a confident `False` for a symbol that was present.
**Use absolute paths with .NET file APIs.**

⚠ **Check the `Environment` key, not the file date.** "Did the rebuild overwrite `secrets.json`?"
is **not decidable from timestamps** — MSBuild `PreserveNewest` stamps the copy with the *source's*
mtime, so a copy and a skipped copy look identical afterwards. **After ANY x64 rebuild, re-read
`Environment` AND the window title before clicking Connect** — this has bitten in both directions.
**The gate does NOT build the x64 bin** (AnyCPU only), and **a harness-bin observation says nothing
about the owner's bin** — separate settings file, DB and journal.

🚨 **…AND SEPARATE GATE CONFIG — the half that decides whether a payload is acted on at all**
(spec-back SB4, 2026-08-06; the clause above named settings/DB/journal and stopped short of this).
Verified divergence:

**Both `orderapp-settings.json` files read 2026-08-07. The divergence is wider than the session
policy** — SB4 named the gate config, and the rest of the file diverges too:

| key | harness Debug bin | owner x64 bin | why it matters |
|---|---|---|---|
| `session_policy.enabled` | **false** | **true** | the policy is structurally silent on one and gating on the other |
| buckets | `NY = HIGH,MEDIUM \| any \| 0.5` | `LONDON = MEDIUM\|CONFIRMED\|0.5` · `ASIA = HIGH,MEDIUM\|any\|0.75` · **NY absent → `DefaultRule`** | **inverted on which bucket is unrestricted** |
| `max_slippage_atr_checked` | **false** | **true** | 🚨 on the harness, **Live START refuses** (§5.5a) and neither chase-abort arm evaluates |
| `risk_per_trade_usd` | `1.0` | `25.0` | any N2 sizing observed on the harness is 1/25th of the owner's |
| `trigger` | `777.0` | `5.0` | harness sentinel values, not trading values |
| `market_stop_loss` | `200.0` | `30.0` | the M.SL loss cap differs by ~7× |
| `risk_size_bridge_trades` | `false` | `false` | the one thing that agrees |

**And the owner's `bridge.json` carries `path` + `slippage_atr_mult` only — NO
`feedback_output_path`**, i.e. **C1 is OFF in the owner's bin**, confirmed at the artefact rather
than inferred from "it ships OFF" (`bin/x64/.../bridge.json`, read 2026-08-07). That key is the
enable in `ROADMAP-2026-08.md` §1.5.

They are not merely different — they are **inverted on which bucket is unrestricted**. The payload
that gives `would-act … size 10` with no clamp line on the harness would, in the owner's bin at the
default `-Confidence HIGH` between UTC 08:00–12:59, come back `refused: policy(LONDON/tier)`: no
would-act at all, and an acceptance that reads as a failure. **Read the gate config off the form
before interpreting any harness bridge disposition.** Two riders: **`-Confidence MEDIUM` is the only
value passing all three buckets** under the owner's config, and **the policy's enabled state is a
CHECKBOX** — reading `txtSessionPolicy` alone tells you nothing about whether it is in force.

## 5. Runtime facts that bite — SUPERSEDES H-4 §5 (two of which were reversed)

1. **The payload must LAND while the bridge is STARTED.** Evaluation is file-change-only; START
   does not re-evaluate the on-disk payload; the staleness tick can only *stop*. Engine stopped ⇒
   write twice (write → START → write); that double write is a harness artefact, not production.
   Freshness = `2.5 × exec_resolution_min`. Mode/ARM/Started reset every app start.
2. **🚨 Bridge payload tests: STOP the engine → run → `restore-payload.ps1` → RESTART the engine.**
   *(H-4 §5's "never stop the engine" is INVERTED — owner ruling 2026-08-01.)* Two footguns, both
   hit for real: forgetting to restart (nothing warns you no signals are arriving), and — engine
   stopped — `write-payload.ps1` defaulting to the **LIVE** payload path, clobbering
   `C:\Dev\DeribitBridge\verdict_signal.json`.
   ✅ **PREFERRED FORM (spec-back SB2, 2026-08-06): point the running bin's `bridge.json` `path` at a
   SCRATCH file and the live payload is never opened at all.** `write-payload.ps1` resolves the
   payload path *from that key*, so with
   `"path": "C:\\Dev\\DeribitBridge\\harness-signal.json"` there is nothing to clobber and nothing to
   forget to restore. Verified on the 2026-08-06 run: `verdict_signal.json` byte- and
   timestamp-identical across it, and `restore-payload.ps1` correctly reported *"nothing to restore"*.
   Backup + restore remains the fallback **when running against the default path** —
   *restore is mandatory, not cleanup, there.*
   ⚠ **This does NOT touch the blunt-kill-rule ruling.** The refusal still gates on the engine
   **process**, not the path (2026-08-01, and the path-aware variant stays rejected), so
   **STOP → run → restore → RESTART is unchanged**. SB2 removes a footgun; it does not remove a step.
3. **Harness-driven placement IS permitted** on a TESTNET-titled, harness-launched session, through
   `tools/place-and-verify.ps1` only. *(H-4 §5's "owner mouse clicks only" is SUPERSEDED — the
   harness was exonerated 53/53.)* The owner still drives every **trade decision, ARM and START**.
4. **🚨 `FormClosing` persists all 11 geometry fields unconditionally** — a runtime test that
   tightens geometry clobbers the owner's real trading values on exit, in the same file that holds
   the session policy and the breaker. Back up, restore, then VERIFY by relaunching and reading a
   box back.
5. 🚨 **ATRSlip must be CHECKED — it has TWO consequences, and this bullet carried only one until
   2026-08-07.**
   (a) **The bridge REFUSES TO START in Live mode while it is unchecked.** `TryStart` is a
   five-condition interlock and this is the fifth: `Not _host.IsMaxSlippageGuardChecked` ⇒
   `"Max Slippage ATR guard (chkMaxSlippageATR) is unchecked"` (`SignalBridge.vb:305-317`). A seat
   debugging *"why won't START take"* will not find that answer anywhere else in this document.
   (b) **NEITHER chase-abort arm evaluates** — both sit under that one switch
   (`If(maxSlippageATRchecked, ChaseAbortReason(...), Nothing)`, four sites). Unchecked yields a
   silent null that reads like a pass. Runbooks have omitted it twice.
   ⚠ **And the harness bin ships it OFF:** `max_slippage_atr_checked: false` in the Debug bin's
   `orderapp-settings.json` versus `true` in the owner's x64 bin (read from both files 2026-08-07).
   So (a) bites on the harness specifically — see §4.
   *(Clause (a) existed in the pre-trim H-6 and was dropped by the 2026-08-02 collapse; it survived
   nowhere else. Recovered by diffing the trim, not by reading. §7 lesson 7, and the first proven
   case of that trim losing a LIVE fact rather than history.)*
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
13. 🚨 **A RUN OF `would-act` LINES IN LOG-ONLY IS NOT A RUN OF TRADES. Cooloff anchors on position
    CLOSE, and log-only never opens a position, so the cooloff gate is STRUCTURALLY UNREACHABLE
    there.** Found 2026-08-13 when a NY log-only session produced **ten consecutive would-acts**
    (#207–#216) against a 5-minute cooloff default.
    - `_lastActionUtc` — the cooloff anchor — is assigned in **exactly one place**:
      `NotifyPositionClosed()` (`SignalBridge.vb:1169`). It is **never** set on `acted` or on
      `would-act`.
    - So in log-only it stays `DateTime.MinValue`, and the gate at `SignalBridge.vb:750` can never
      fire. Every qualifying signal produces a `would-act`.
    - **In LIVE the same stream behaves completely differently:** the first signal opens a position,
      every later one refuses at **`refused: not_flat`** (`:746`) until it closes, and only then does
      the cooloff start. **Ten would-acts in log-only = ONE trade in Live.**
    - ⚠ **The field's own comment is WRONG** — `SignalBridge.vb:154` reads *"cooloff anchor (acted /
      would-act)"*. It anchors on neither. Backlogged; do not trust it while it stands.
    - ⚠ `HANDOVER-3.md:45` (superseded) says *"log-only advances watermark+cooloff"*. **Half right:**
      the **watermark** does advance in log-only — proven, `bridge-state.json` took `10` from a
      would-act — but the **cooloff cannot**. Do not carry that phrase forward intact.
    - **Reading rule: to estimate Live activity from a log-only soak, count would-act CLUSTERS, not
      would-act LINES.**
14. 🚨 **SCREENSHOTS: capture the APP, never the desktop.** Owner ruling 2026-08-07, after a seat
    took a desktop-wide capture while debugging and caught unrelated sensitive content on the
    owner's screen. The seat deleted it and disclosed it, which is the right response — **but the
    rule now exists so the judgement call does not have to be made again.**
    - Use `tools/screenshot-mainform.ps1` (main form only, Win32 `PrintWindow`, works on a
      non-foreground window) or `tools/screenshot-full.ps1`.
    - ⚠ **`screenshot-full.ps1` is NOT a desktop capture despite the name** — it is a full-**form**
      capture through the app's own hotkey, for content clipped off-screen.
    - **Neither repo tool can capture the desktop. Do not reach outside them to do it.**
    - The pre-existing rule inside both scripts — output to git-ignored `verify/out/`, deleted after
      use — still applies and was never the issue. It simply never said *do not capture the
      desktop*, because nobody had thought to.

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
    this repo uses `File.Replace` NOWHERE.** Swept 2026-08-04 at the engine seat's suggestion; **the
    sweep was RE-RUN 2026-08-07 and still holds**: three sites, all conforming —
    `AppUserSettings.vb:208` · `SignalBridge.vb:408` (PersistState) · `ExecutorFeedback.vb:562` (C1).
    A grep for `File.Replace` returns **three hits, all COMMENTS** (`ExecutorFeedback.vb:520`,
    `SignalBridge.vb:431`/`:433`) — check that before reading the count as a violation.
    *(Those line numbers were `:207`/`:407`/`:530` here until 2026-08-07. **Line refs drift; the SITE
    is the claim** — re-grep, never cite this doc's numbers at a reviewer.)*
    **Our pattern is TOTAL where theirs needs a guard** —
    `File.Move(overwrite:=True)` works whether or not the destination exists, which is why E1 was
    never a code defect here. Append-only log writes (`crash.log`, `AutoTradeLog.txt`, the
    disposition log) are a different class and are deliberately not atomic.
    ✅ **The inherited-dependency rider is RESOLVED — and this bullet was stale for three days.**
    The retired sentence, quoted per the house rule: ~~*"`SignalBridge.vb:425`'s comment asserts
    `File.Replace` on the engine side surfaces as rename/change — a claim about another repo's
    internals"*~~. **It was already reworded on 2026-08-04**, the same day the sweep ran. The comment
    now says the opposite and says why (`SignalBridge.vb:425-436`): *the writer's API is not ours to
    assume*, the watcher subscribes to Changed, Created **and** Renamed because that is correct for
    Replace, Move or a plain write alike — and it records that the engine seat is queuing a
    `File.Replace → File.Move` swap **that would have falsified the old comment's stated reason while
    leaving the code correct**. That is the mirror-drift class caught before it fired, not an open
    one. *(Verified by reading the comment 2026-08-07 — this doc had gone on describing the version
    it replaced.)*

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

### 7bb. Finding-ID convention (2026-08-06) — `E` / `D` / `SB`, and they are NOT interchangeable

- **`E`n** — an **escalation** raised by the implementer *before* writing the code it concerns.
- **`D`n** — a **defect found in code**, whoever finds it (review, acceptance, runtime).
- **`SB`n** — a **spec-back finding against the DOCS**, raised after the work.

**Scope IDs to their feature and say which kind they are when citing across features.** C1 briefly
had two live `D1`s — the review's write-ordering defect and the spec-back's first finding — while
N2b's spec-back already owned a third. A bare "C1 D1" resolved to two different things, and the
collision was introduced by numbering a docs-finding set with the code-defect prefix. Renumbering
afterwards is cheap; a stale cross-reference to the wrong `D1` is not.

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
8. 🚨 **DO NOT SPECIFY A FIX FOR A CAUSE YOU HAVE NOT MEASURED. The probe that would have prevented
   it costs one seat-hour.** Earned expensively 2026-08-07 on the harness owned-window item.
   `spec-harness-owned-window.md` §1.2 asserted a *topology* cause — an owned window is not a
   desktop-root child — from one line in an impl report. The fix built on it was correct to the
   letter and made things **worse**: it doubled every settings-window control and every drive script
   refused. A 39-round probe then showed the window is **never** a root child (0/39), so the
   enumeration never mattered and the real cause was render latency.
   **Three separate documents had already stated the wrong cause as fact** before anyone measured it.
   *The implementer's spec-back caught the regression; the probe caught the wrong cause; the review
   caught the fix that did nothing. Each was a different seat looking at the same feature.*
9. 🚨 **A TIMEOUT SMALLER THAN ONE ATTEMPT IS A NO-OP — and it passes every acceptance.** Same item,
   the follow-on defect. The bounded wait shipped with a 500 ms deadline taken from **in-process**
   measurements of 9–22 ms. A real caller is a **fresh process**, where the first UIA query alone
   costs **740–950 ms**. The deadline check ran after that first attempt, so the loop always returned
   at one attempt and `Start-Sleep` was unreachable. **The retry path was dead in every real
   invocation, and a 60/60 live acceptance passed anyway** — because process-start latency already
   exceeded the race the wait existed to cover.
   **Two rules out of it:** (a) **measure in the caller's real process**, never in-process, whenever
   a number is going to become a timeout; (b) **a wait must guarantee its first attempt outside the
   budget**, so the budget means retries.

## 8. Audit record — 2026-08-07, against artefacts at HEAD `8f04c27`

**Method: no claim in this file was cleared by reading another doc.** Config claims were read out of
the two `orderapp-settings.json` files and `bridge.json`; code claims were re-derived by grep or by
opening the site; the gate was executed. This section exists so the *next* audit inherits what was
checked and what was not — an audit that reports only its finds silently implies total coverage.

### 8.1 Corrected — three, each verified in code first

1. **§5.5 was missing half of what it is for.** The pre-trim H-6 said ATRSlip-unchecked also makes
   **`TryStart` refuse in Live mode**; the 2026-08-02 collapse dropped that clause and **it survived
   nowhere else in the repo**. Confirmed at `SignalBridge.vb:305-317`. Restored, with the artefact
   fact that the harness bin persists the guard OFF.
2. **§6.10's rider described a code comment that had already been rewritten** on 2026-08-04 — the
   same day the sweep it sits under was run. The comment now says the *opposite* of what the rider
   quotes. Retired sentence quoted-and-labelled; resolution recorded.
3. **§4's divergence table named the session policy and stopped**, when five other keys diverge —
   including the one that blocks Live START on the harness. Table widened from the files themselves.

Plus, earlier the same day: **§1's state block** (`origin/master`, and a gate count four generations
stale) and **§3's queue line** (which contradicted §2.7 in the same file), and three line-number
drifts in §6.10.

### 8.2 Re-verified and HOLDING — listed so the next seat can skip them

**From the artefacts:** §2.1/§2.2/§2.3 exactly as written (`risk_per_trade_usd` 25 · `max_size_usd`
500 · breaker 10 · `session_policy.enabled` true with LONDON/ASIA configured and **NY absent**) ·
§4's `Environment = testnet` in both bins · §5.11's disposition-log split (x64 223 KB, Debug 2 KB) ·
§6.7's *persisted gate config = breaker + session policy only* — no cooloff, window or tiers appear
in either settings file.

**From the code:** §5.1 freshness `2.5 × exec_resolution_min` (`SignalBridge.vb:259`, `:602`) ·
§5.6's 4-second cancel-pending window (`frmMainPageV2.vb:358`) · §6.1's `DefaultRule` fallback ·
§6.2 *unity passes through untouched* — literally `If mult = 1D Then Return rawSizeUsd`
(`SignalBridge.vb:1027-1029`) · §6.3's unconditional disposition append (`:1211`) · §6.4's debounce
in **exactly six** placement sites and nowhere else · §6.5's notifier inert without `ntfy_url` ·
§6.8's four `ChaseAbortReason` gates and ATR period 7 · §6.10's three atomic sites and no
`File.Replace` in code · §5.4's 11 geometry fields, corroborated by both the handler
(`frmMainPageV2.vb:6037-6058`) and the 11 keys ahead of the risk block in the settings file.

### 8.3 NOT verified this pass — the honest list

- **§2.4's journal rows `#95`/`#100`** — three read routes failed; see the ⚠ on that item. **This is
  the only §2 item still carried on doc-evidence alone.**
- **§5's runtime behaviours that need a running app**: the payload-landing rule (§5.1), the
  stop/restore protocol (§5.2), harness placement (§5.3), the geometry clobber's *effect* (§5.4),
  entry-is-reference-only (§5.6), the emergency fill-vs-trigger gap (§5.7), the placement-log trap
  (§5.8), the min-10 blind spot (§5.9), payload timing (§5.10). Their *code* halves are verified
  where a grep can reach them; their *runtime* halves are carried on the runtime records that
  earned them.
- **§6.6's signal-tag lifecycle** — six transitions across two threads; not traced this pass.
- **§7's lessons** are methodology, not claims about code, and were not re-derived.
