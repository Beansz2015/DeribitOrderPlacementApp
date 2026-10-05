# HANDOVER-7 — coordinator seat, DeribitOrderPlacementApp

**Written 2026-10-05 for an Opus 5.5 orchestrator.** This is the new entry point. `docs/HANDOVER-6.md`
stays live for its §4–§7 (bin rules, runtime bite-list, invariants, methodology) — this document does
not copy them.

🚨 **READ FIRST: every figure below was verified on 2026-10-05, but the last commit is dated
2026-08-19. There is a SEVEN-WEEK GAP with no repo activity.** Nothing moved in it — same HEAD, same
unpushed count, tree clean. Treat anything runtime-shaped (the app, the engine, the exchange, the
owner's own intent) as possibly stale even though git is not.

---

## 1. State — verified 2026-10-05, not inherited

| Thing | Value | How it was checked |
|---|---|---|
| HEAD | `a0de872` | `git rev-parse --short HEAD` |
| origin/master | `8232e9e` | separate `git rev-parse` call — see the quirk below |
| Unpushed | **10 commits** | `git rev-list --count origin/master..HEAD` |
| Working tree | clean | `git status --porcelain` empty |
| Last push | **2026-08-14 16:36 +0800** | `git reflog show origin/master` |
| Last commit | **2026-08-19 02:11 +0800** | `git log --date=iso` |
| Gate | **GATE PASSED, OrderCheck 294/294** | executed at `a0de872` |
| Nine censuses | **68 occurrences across 64 lines, zero drift** | re-run at `a0de872` |

⚠ **`git rev-parse --short HEAD origin/master` FAILS on this box** — `fatal: Needed a single
revision`, git 2.55. Run the two revisions as **separate calls**. A git-version quirk, not drift.

⚠ **The censuses are OCCURRENCE counts.** A count-mode grep returns 64 and reads as four missing.
Use `Select-String -AllMatches -CaseSensitive` and sum `Matches.Count`. The nine symbols are listed
in `docs/HANDOVER-6.md` §1.

### 1.1 The owner's x64 bin — measured, not assumed

| Probe | Result |
|---|---|
| x64 dll mtime | **2026-08-14 21:04** |
| Carries `WsEdgeLog` | ✅ **True** |
| Carries `MaxSizeLine` | ❌ **False** |

🚨 **The x64 bin does NOT carry the uncapped-Max-Size feature.** `Max Size = 0` will do nothing in
the owner's app until they rebuild. The gate is **AnyCPU-only** and never builds that bin.

```powershell
$b = [IO.File]::ReadAllBytes("DeribitOrderPlacementApp\bin\x64\Debug\net9.0-windows8.0\DeribitOrderPlacementApp.dll")
[Text.Encoding]::ASCII.GetString($b).Contains('MaxSizeLine')
```

⚠ **Scan a TYPE name, never a string literal.** Literals live in the UTF-16 `#US` heap and a naive
offset-0 decode misses any at an odd byte offset — that produced a **false negative** on 2026-08-18.
Full method and the both-alignments fix: memory `gate-does-not-build-x64-bin`.

### 1.2 Config in force in the x64 bin — verified at the artefacts

- `risk_per_trade_usd = 1.0` · `max_size_usd = 2000.0` · `risk_size_bridge_trades = True` ·
  `circuit_breaker_usd = 10.0`
- `secrets.json` → `Environment: testnet` · `bridge.json` → `feedback_output_path` **set**
- ⚠ **Never hand-edit `orderapp-settings.json` while the app runs.** `FormClosing` persists all 11
  geometry fields plus these keys unconditionally and will overwrite the edit on close.

---

## 2. OUTSTANDING — owner side

**1. PUSH. 10 commits unpushed. The owner is the only pusher.** Top item, outstanding since
2026-08-19.

**2. REBUILD x64.** Required before the Max Size feature does anything, and before any runtime
observation of it.

```
dotnet build DeribitOrderPlacementApp\DeribitOrderPlacementApp.vbproj -c Debug -p:Platform=x64
```

Then the **standing post-rebuild check, no exceptions**: confirm `bin\x64\...\secrets.json` reads
`Environment: testnet`, **and read the window title and confirm `— TESTNET`** before Connect or
anything else. Rationale and the live-account near-miss that created this rule: memory
`gate-does-not-build-x64-bin`.

**Free confirmation on launch:** a new startup line. With `max_size_usd = 2000` in force it should
read `Max size: 2000`. Type `0`, tab away, and it should read `Max size: NO CAP (max_size_usd = 0)`
in yellow.

**3. N2 LIVE — a decision, not a task.** Log-only verification is COMPLETE: all three session
buckets observed against real payloads, **14/14 sizes exact**, stop distances 35.7 to 119.2, both
directions, HIGH and MEDIUM tiers. **Nothing technical blocks it. LIVE has never been run and that
is the owner's call alone.**

**4. AWS London migration** — `docs/production-cutover-checklist.md` §9. The key choreography kills
the two-executor window.

**5. Optional, cheap:** the two Max Size line **colours** were never seen. `Color.Yellow` when
uncapped and `Color.Gray` when capped are read-as-text-only, by both the implementer and the
reviewer. One look after the rebuild closes it.

---

## 3. OUTSTANDING — coordinator side

**6. ✅ SETTLED 2026-10-06 by code reading — a failed reconnect never calls `NoteState`.** The single
`DOWN` row means "never called again", not "suppressed". The rule itself is unit-tested. Evidence:
`docs/runtime-record-ws-down-emitter-2026-08-14.md` §5 item 1. *(The original text follows.)*
**6. 🚨 The failed-reconnect `DOWN` suppression — STILL UNPROVEN, AND IT LOOKS PROVEN.**
`docs/runtime-record-ws-down-emitter-2026-08-14.md` §5 item 1. Eight reconnect attempts failed
between the drop and the recovery, and `ws-edges.log` carries **exactly one `DOWN` row**. That is
tempting to read as the no-flood suppression working. **It does not prove it.** A failed
`ConnectToWebSocketDirectly()` never establishes a socket, so it never enters a receive loop and
never exits one — and the DOWN publish site is at the loop **exit**. One row is equally consistent
with "suppressed" and with "`NoteState` was never called again". **Do not tick this off.**

**7. `docs/ROADMAP-2026-08.md` §5 — the hygiene backlog.** Nothing urgent. **Nothing is in flight
and no implementer seat is running.**

Also still unproven on that edge, same record `docs/runtime-record-ws-down-emitter-2026-08-14.md` §5:
the `While`-condition exit arm (both real drops took the `WebSocketException` arm), and `NoteState`
concurrency.

---

## 4. CLOSED in the 2026-08-14 → 2026-08-19 era — do not re-open

| Item | Outcome |
|---|---|
| **ws DOWN edge** | ✅ **FULLY CLOSED.** The emitter published `"ws": "DOWN"` on a real mid-session drop, joined to `ws-edges.log` **to the second** at `2026-08-14T14:09:24Z`, with a trailing `OK` under one continuous `instance_id`. `docs/runtime-record-ws-down-emitter-2026-08-14.md` |
| **Uncapped Max Size** | ✅ Built, reviewed, **APPROVED**, all four review findings closed. `docs/review-max-size-uncapped.md` |
| **Size ladder** | ✅ Owner ruling 2026-08-14 — the ladder is ACTIVE when the `Risk-size` checkbox is UNTICKED for N2. Not an open item |
| **N2 values** | ✅ Confirmed in force by the owner **and** re-derived at the artefact |
| **Journal rows #95 / #100** | ✅ Owner DELETED both, 91 → 89. ⚠ Closed by **deletion, not verification** — they were never read, so the item can never be settled on its merits |

---

## 5. The two owner rulings that govern Max Size — do not re-litigate

Both folded into `docs/spec-max-size-uncapped.md` §2.2c and memory `standing-rulings-in-force`.

- **`max_size_usd` is NOT bridge-only, and the manual SIZE button's uncapping is ACCEPTED.**
  `ApplyRiskBasedSize` (`frmMainPageV2.vb:339`, `:353`) reads the same value and hands it to the same
  `RiskSizedBase` with **nothing guarding it**. One setting, one meaning. **Do not add a guard
  there.** Consequence: the log lines read **`Max size: …`**, never `Bridge max size: …`, and the
  formatter is **`MaxSizeLine`** — the old name is how the misconception comes back.
- **The commit line fires on ANY change to `MaxSizeUsd`**, not only on the change to uncapped. A
  superset of the spec, **kept deliberately**: a line only on the way *in* leaves the way *out*
  silent, so the log's last word would read `NO CAP` while a cap was back in force.
  🚫 **Do not narrow the guard to `If maxSize <= 0D`.** That one-line revert is the obvious-looking
  simplification and it is wrong.

🔴 **`docs/spec-max-size-uncapped.md` acceptance 2c is STRUCK and must not be re-run. It cannot
fail.** Re-seeding fires only from `CommitOnLeave` (`AutoTradeSettings.vb:268`) and
`CommitOnEnterKey` (`:276`), both of which pass the focused box as `sender`, and the form declares
**no `TextChanged` handler at all** — so the box holding uncommitted keystrokes is always the sender.
A blanket re-seed and a sender-only re-seed are behaviourally identical through this UI. **The
sender-scoping still stays**: it now guards a *future* hazard (a `TextChanged` commit, a timer
commit, a second editing surface), not a reachable one.

---

## 6. TRAPS that will cost the next seat time

- **A MISSING LOG LINE IS EVIDENCE ABOUT THE BINARY, NOT THE CONFIG.** A stale x64 bin already cost
  one live debugging session: the emitter was absent entirely and a correct config change produced
  **no startup line at all**.
- **`ws-edges.log` is a history of TRANSITIONS, not of `ws`.** A startup `DOWN` window exists
  (`feedback_id` 1–6, before the first connect) with **no log row**, because "never connected" is a
  state and not a transition. **Never read that log's silence as the socket being up.**
- **`feedback_id` going 2 → 1 on disk across a restart is CORRECT.**
  `docs/integration-contract-verdictengine.md` §8.4 scopes monotonicity **per process**; identity is
  the `(instance_id, feedback_id)` pair, and the GUID changes on restart. **Check the GUID before
  reporting an id regression**, or it reads as C1's `D1` (the write-ordering defect, long fixed)
  returning.
- **A RUN OF `would-act` LINES IN LOG-ONLY IS NOT A RUN OF TRADES.** Cooloff anchors on position
  CLOSE, and log-only never opens a position, so the cooloff gate is structurally unreachable there.
  Ten would-acts in log-only is ONE trade in Live. `docs/HANDOVER-6.md` §5 item 13.
- **The emitter heartbeat is ~10 s and it republishes UNCHANGED content by design.** A fresh
  `generated_at_utc` proves the process is ALIVE, not that any field changed.
- **Recovery budget is ~72 s of backoff plus per-attempt connect time**, 10 attempts
  (`frmMainPageV2.vb:1319`, `:1340`, `:1370`). The 2026-08-14 drop used 8 of 10 over 53 s. **Hold a
  provoked outage to 20–30 s.**
- **Screenshots capture the APP, never the desktop.** Note `tools/screenshot-full.ps1` is a
  full-**FORM** capture despite its name. `docs/HANDOVER-6.md` §5 item 14.
- **Seats never place trades and never arm the bridge.** The owner drives every trade, ARM and START.
  A provoked network disconnect is **owner-only** — the seat does not change network configuration.

---

## 7. Instruments built this era

- **`tools/sample-feedback.ps1`** — read-only sampler for `executor_feedback.json`. **Start it BEFORE
  any provoked drop.** That file is overwritten on every publish, so a mid-session `DOWN` survives
  only from the disconnect to the reconnect — 23 s in one session. Reading by hand finds the
  graceful-close `DOWN` instead, which is the ambiguous artefact. Opens with `FileShare`
  Read+Write+Delete so it can never block the emitter's atomic replace. Archives every distinct
  publish plus an index into the git-ignored `verify/feedback-samples/`.
- **The byte-scan bin probe** — this document §1.1, and memory `gate-does-not-build-x64-bin`.

---

## 8. Method and seat rules

Unchanged and canonical in `docs/HANDOVER-6.md` §7. The three that bit hardest this era:

- **A doc is not evidence about a doc, and the staleness is RECURSIVE.** Verify against artefacts:
  the code, the reflog, `trades.db`, the settings JSON, the bin's own bytes. This era found a doc
  claim ("the emitter publishing `ws: DOWN` is unobserved") that was **false when written**, with the
  contradicting file already on disk for 24 minutes.
- **An acceptance must test the DEFECT, not the fix's theory of it.** An acceptance that cannot fail
  is worse than none, because it reads as evidence. See this document §5 on the struck 2c.
- **A fixture that cannot fail is the same trap in test form.** An invariant-culture pin that never
  switched culture passed identically on this en-MY box whether the code was invariant or not.

**Conventions:** `E` = escalation raised BEFORE the code · `D` = a defect in code · `SB` = a finding
against the docs. **Scope every ID to its feature** — this repo has had two live `D1`s and two
unrelated `F1`s at once. Spec defects escalate to the OWNER before implementing, and rulings fold
back into the spec so future greps are not stale. Every spec for a new seat must close with a model
and effort recommendation (`docs/HANDOVER-6.md` §7b).

**Model and effort:** Opus HIGH for order/SL/receive/bridge/act paths. Sonnet medium for `tools/` and
docs. **Coordinator review is Opus regardless of who implemented.**

**Implementers here are good and are right more often than is comfortable.** This era upheld all six
spec-back findings an implementer raised against a spec the coordinator seat itself wrote. Verify
their claims, then say so plainly when they hold.

---

## 9. Suggested effort for the incoming seat

**Medium.** Raise to **high** for a code review on the order/SL/receive/bridge/act paths or an
adversarial pass.

**The real spend on this repo is CONTEXT, not thinking depth** — `frmMainPageV2.vb` alone is ~67k
tokens. Use the specs and section banners as the map, read regions on demand, and review via
`git show` and targeted greps rather than re-reads.
