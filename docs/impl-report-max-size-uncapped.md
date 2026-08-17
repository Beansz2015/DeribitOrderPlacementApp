# Impl report — `Max Size = 0` means NO CAP, and an uncapped state announces itself

**Spec:** `docs/spec-max-size-uncapped.md`.
**Seat:** implementer, Opus HIGH, fresh conversation, 2026-08-18.
**Commits:** `8831f62` (the three coordinated edits + the §2.2b re-seed) · `d1d17ad` (the two
visibility lines + fixtures) · this report.
**Branch state:** committed locally on `master`. **NOT pushed — the owner is the only pusher.**

**Finding IDs in this report** are `SB1`–`SB6`: **spec-back findings against
`docs/spec-max-size-uncapped.md` and against `tools/`**, raised after the work
(`docs/HANDOVER-6.md` §7bb). **No `D` (code defect) and no `E` (pre-implementation escalation) was
raised** — nothing in the spec needed amending before the code could be written.

---

## 1. Provenance check, before anything was edited

`docs/spec-max-size-uncapped.md` says *"every fact below verified in code on 2026-08-14 at HEAD
`af86a3e`"*. **HEAD at seat start was `44dddd1` — nine commits later.**

- `git log af86a3e..HEAD -- AutoTradeSettings.vb frmMainPageV2.vb SignalBridge.vb` returns
  **nothing**. None of the nine commits touched a scope file.
- Every line number the spec cites was re-opened and matched exactly:
  `SignalBridge.vb:1109`/`:1123-1129` (the `maxSizeUsd > 0D` guard), `:1069`/`:1072` (the two yellow
  lines), `:1092-1095` (the `-1` fallback to the Amount box) · `AutoTradeSettings.vb:284-289`
  (breaker), `:368-369` (seed), `:384-386` (commit), `:412`/`:416-418` (warnings), `:143` (the single
  `SeedRiskSizingFromHost` call site), `:168` (`Leave` wiring) · `frmMainPageV2.vb:76` (`500D`),
  `:95-99` (the `> 0D` guard), `:958-966`/`:984` (the startup-line family).

**No stale-citation risk. The spec was safe to implement as written.**

---

## 2. What was built

### 2.1 The three coordinated edits (`8831f62`) — they land together

| # | Site | Before | After |
|---|---|---|---|
| 1 | `AutoTradeSettings.CommitToolingConfig` | a parse failure collapsed to `0D` | max size follows the **breaker's** contract — only a **parse failure** keeps the last good value, and whatever parsed is committed |
| 2 | `frmMainPageV2.SetRiskSizingValues` | `If maxSize > 0D Then …` | max size takes **any** value, `0` included. **Risk / Trade keeps its `> 0D` guard** |
| 3 | `AutoTradeSettings.ShowGateConfigWarnings` | flagged on parse failure **or** `<= 0` | max size flagged on a **parse failure only**. **Risk / Trade keeps its `<= 0` arm** |

**The one design decision that needed thinking, and it is not in the spec.**
`SetRiskSizingValues(riskPerTrade, maxSize)` is **one setter for both boxes**, so the breaker's shape
at `AutoTradeSettings.vb:284-289` — *guard the call* — is **not available**. Three options existed:

| Option | Verdict |
|---|---|
| Split the setter | A contract change. `docs/spec-max-size-uncapped.md` says **no contract change**. Rejected. |
| Keep `maxSize = 0D` on parse failure | 🚨 **Catastrophic.** After edit 2 that makes a **typo mean UNCAPPED**. This is precisely the "hands the bridge an unbounded size" outcome the spec's §Model and effort calls the worst available. Rejected. |
| **On a parse failure, re-send the value already in force** (`_host.MaxSizeUsd`) | **Chosen.** The host sees no change, keeps the last good value, and stays silent — so a typo produces no §2.2 (a) line either. |

### 2.2 The §2.2b re-seed (`8831f62`)

`ReseedSenderFromHost(sender)`, called from **both** commit paths. **Only the sender re-seeds**, and
scope is the `SetRiskSizingValues` pair (`txtMaxSize`, `txtRiskPerTrade`) only. `txtSessionPolicy`
also reaches `CommitOnLeave` and is correctly untouched.

### 2.3 The two visibility lines (`d1d17ad`)

One formatter — `frmMainPageV2.BridgeMaxSizeLine` — feeds both, so they cannot drift.

- **(a) commit line**, fired when `MaxSizeUsd` actually changes.
- **(b) startup line**, unconditional, beside `"Trade defaults restored…"`. Reads the **property**,
  not the box, so it states what is in force.

Observed strings: `Bridge max size: NO CAP (max_size_usd = 0)` · `Bridge max size: 2000`.

---

## 3. Acceptance results — all eight

| # | Result | Evidence |
|---|---|---|
| **1** | ✅ **PASS** | Typed `0`, committed, closed the app cleanly via `tools/stop-app.ps1`. The settings file read `"max_size_usd": 0.0`. Relaunched: the box read **`0`**, not blank (§2.4's round-trip). |
| **2** | ✅ **PASS, both conjuncts** | Set `2000`, committed. Cleared the box, tabbed away. Stored value still `2000` **and the box READ `2000`, not blank**. Hid and reopened the settings window: still `2000`. |
| **2b** | ✅ **PASS** | Typed `0`, tabbed away. Box still read `0`; commit line fired `Bridge max size: NO CAP (max_size_usd = 0)`. |
| **2c** | ⚠️ **PASS as worded — but see `SB4`. The discriminating case is NOT reachable in this UI.** | Typed `1234` into Max Size, committed Risk / Trade, Max Size still showed `1234`. Every attempt to build a version that a *blanket* re-seed would fail was defeated by the UI itself. Detail below. |
| **3** | ✅ **PASS, both conjuncts** | Typed `abc`, tabbed away. Stored value unchanged at `2000`, box **resynced to `2000`**, warning read `Ignored (keeping last good): max size 'abc'`. |
| **4** | ✅ **PASS, all four** | max size `0` → **no** flag (status line clean) · max size `abc` → **flags** · risk `0` → **flags** `risk/trade '0'` · risk `abc` → **flags**. Plus risk `1` → clean. **§1.5's invariant holds in both directions.** |
| **5** | ✅ **PASS — and on a payload that genuinely discriminates.** | Detail below. |
| **6** | ✅ **PASS, all three parts** | Detail below. |
| **7** | ✅ **PASS** | `GATE PASSED` at **both** code commits. OrderCheck **289/289** at `8831f62`, **294/294** at `d1d17ad`. The nine `frmMainPageV2.vb` censuses: **68 occurrences across 64 lines at both commits — unmoved.** |
| **8** | ✅ **Already satisfied; strengthened anyway.** | See below. |

### 3.1 Acceptance 5 — the cap is provably gone

The spec warned that no captured payload had a stop under about **$32**, so an uncapped run might
look identical. **`tools/write-payload.ps1` takes `-Entry` and `-Stop`, so one was crafted:**
entry `64000`, stop `63980` — **distance 20**.

Hand-computed, `risk_per_trade_usd = 1.0`: `floor(1 × 64000 ÷ 20 ÷ 10) × 10 = 3200`.

```
max_size_usd = 0     -> would-act: LONG @ 64000.00, ..., size 3200      (no cap line at all)
max_size_usd = 2000  -> [BRIDGE] risk size 3200 capped to 2000 by max_size_usd 2000
                     -> would-act: LONG @ 64000.00, ..., size 2000
```

**Same payload shape, both directions, one session.** The logged uncapped size matches the hand
computation exactly, and the counterfactual proves the cap machinery is intact. **This is not a
payload that would have passed either way.**

### 3.2 Acceptance 6 — both lines, and only those

- **Startup line, capped:** `Bridge max size: 500`.
- **Startup line, uncapped, after a restart:** `Bridge max size: NO CAP (max_size_usd = 0)`. **A
  restart cannot hide it.**
- **Commit line** fired on `500 → 2000`, on `2000 → 0`, and on `0 → 2000`.
- **Soak: 5.08 minutes, log-only, 10 payloads, 10 would-acts.** `Bridge max size` lines added:
  **0**. `capped to` lines added: **0**. **No per-signal cap line exists.**

### 3.3 Acceptance 8 — the fixture already existed

`tools/OrderCheck/Program.vb` already pinned **both** uncapped arms of `RiskSizedBase`
(`max_size_usd = 0 means NO CAP (320)` and the negative-cap twin). **Nothing was owed.** That is
`SB3`.

What was **not** pinned is the seam this spec introduces, and it is the half that regresses
silently: when nothing binds, those two lines are the *only* evidence an uncapped state exists.
**Five fixtures added** on `BridgeMaxSizeLine`: the two states, a negative cap, invariant-culture
rendering, and that the two renderings are distinct strings. `289/289 → 294/294`.

---

## 4. Spec-back findings (`SB1`–`SB6`)

**`SB1` — `docs/spec-max-size-uncapped.md` §2.2b names only `Leave`; there is a SECOND commit path.**
That section justifies re-seed safety with *"commits fire on `Leave`, not `TextChanged`
(`AutoTradeSettings.vb:168`)"*. True but incomplete: **`CommitOnEnterKey` (`:270-276`) is wired to
the same ten boxes** and commits too. Re-seeding only on `Leave` would have left exactly the
divergence §2.2b exists to close — press Enter on garbage and the box keeps showing garbage while
the last good value is in force. **Implemented on both paths.** The section-2.2b data-loss
prohibition is untouched: still sender-only.

**`SB2` — acceptance 7's OrderCheck figure is four generations stale.**
It asks for **268/268**. `docs/HANDOVER-6.md` §1 already recorded **289/289** at `8232e9e`, and the
spec was written after that. Not a defect in the change; it would read as a regression to a seat
checking the number literally.

**`SB3` — acceptance 8's fixture already exists.** See §3.3 above.

**`SB4` — acceptance 2c cannot discriminate, and this is structural, not a harness gap.**
2c exists to prove *only the sender re-seeds*. To fail it, a **blanket** re-seed would have to wipe a
box the owner is mid-typing in. **In this UI that state is unreachable**, and it took three
constructions to establish it:

1. **Type a parsing value, commit another box.** The box shows what you typed — but so would a
   blanket re-seed, because the neighbouring commit has already committed *your* text through the
   shared `CommitToolingConfig`. **Vacuous.**
2. **Type a NON-parsing value, then commit another box.** Defeated: `ValuePattern.SetValue`
   **moves keyboard focus to the target** (`SB5`), so the box under test is focused; moving focus
   away makes it **its own sender**, and the re-seed that follows is acceptance 3's behaviour, not a
   blanket re-seed.
3. **Commit without a focus change** via `btnRiskSize_Click`, which calls `CommitToolingConfig`
   directly and has no `CommitOnLeave`. Defeated: `InvokePattern.Invoke` **also takes focus** —
   observed, focus landed on `btnRiskSize` — so the box blurred first again.

**The general result:** with a single keyboard focus and commit-on-`Leave`, the box being typed into
is always the sender of the next commit. **A blanket re-seed and a sender-only re-seed are
behaviourally indistinguishable through this UI.** The sender-scoping is still correct and is
retained — it is now known to be a *structural* guarantee, verified by reading the code, **not one
this acceptance can demonstrate.** 🚨 **Do not let a future pass read acceptance 2c's ✅ as runtime
proof that a blanket re-seed was ruled out. It was not, and it cannot be.**

**`SB5` — two `tools/` harness findings, for `ROADMAP-2026-08.md` §5.** Both cost real time.

- **`ValuePattern.SetValue` focuses the box it writes.** So the harness **cannot express
  "typed but not left"** for a WinForms TextBox. Any acceptance whose wording depends on that
  distinction is untestable as written. `tools/set-textbox.ps1`'s *"value NOT committed — focus
  untouched"* message is **wrong on the second clause**.
- **`tools/set-textbox.ps1`'s `Get-FocusSink` picks the first focusable Edit**, which was
  **`txtMaxSize` itself** on two runs — i.e. the blur sink was the box under test. Harmless for
  ordinary use, fatal for a test about which box committed. Worked around with a scratch driver
  that takes an explicit sink; **no `tools/` file was changed** (the spec forbids it).

**`SB6` — a decision the owner may want to reverse in one line.**
§2.2 (a) asks for a commit line *"when the setting changes to uncapped"*. **Implemented as: any
change to `MaxSizeUsd`.** Reason: a line only on the way *in* leaves the way *out* silent, so the
log's last word on max size would read `NO CAP` while a cap was back in force — the
display-versus-reality divergence §2.2b exists to kill. It is still bounded (change-guarded; the
soak added zero lines). **Flagged because it is a superset of what was specified, not because it is
in doubt.** Reverting is one condition.

---

## 5. Was Opus-HIGH right? (`docs/HANDOVER-6.md` §7b requires this section)

> **Yes — but the depth went somewhere the spec did not predict.**

**Where it was actually needed:**

1. **The shared setter (§2.1 above).** `docs/spec-max-size-uncapped.md` §2.1 says *"commit only on a
   successful parse"* and points at the breaker as the model — but the breaker **guards its own
   call**, and max size **cannot**, because `SetRiskSizingValues` carries risk in the same call. A
   seat working quickly takes the spec's "follow the breaker's shape" literally, finds it does not
   fit, and reaches for the nearest thing: leave `maxSize = 0D` on parse failure. **After edit 2
   that makes every typo mean UNCAPPED.** This is the single place the change could have gone
   catastrophically wrong, and the spec does not mention it.
2. **`SB1`'s second commit path.** Believing §2.2b's *"commits fire on `Leave`"* is enough to write
   a re-seed that looks complete and leaves a live divergence.
3. **`SB4`.** A quick seat reports 2c as a clean pass on the vacuous construction, or as a **failure**
   on the garbage construction — the second is what happened here first, and it looked like a real
   data-loss defect for two runs. Neither reading is right.

**Where it was NOT needed:** the three edits themselves. Once the shared-setter question is settled
they are near-mechanical, and §2.3's asymmetry is well enough argued in the spec to need no
re-derivation. **The spec's own stated reason — "the diff is about six lines, that is exactly why
the tier is high" — is right about the tier and slightly wrong about why.** The risk was never that
six lines are hard; it was that the setter they must go through serves two settings with opposite
contracts.

---

## 6. 🚨 What was NOT established

1. **Nothing ran on the OWNER's x64 bin.** Everything ran on the **harness AnyCPU Debug bin**
   (`bin/Debug/net9.0-windows8.0`), harness-launched, TESTNET-titled. The owner's
   `bin/x64/Debug/net9.0-windows8.0` was **never launched, never rebuilt, and its
   `orderapp-settings.json` was never opened.** **Acceptance 1 names the x64 path; the file actually
   asserted was the harness bin's.** Deliberate — the harness bin is the isolated one
   (`docs/HANDOVER-6.md` §4), and `FormClosing` persists 11 standing fields (§5.4), so running these
   tests in the owner's bin would have put the owner's real trading values at risk for no gain.
   **The x64 bin does not carry this change and will not until the owner rebuilds it.**
2. **No LIVE run. No ARM. No START. No trade placed.** Log-only throughout, per SB1 of
   `spec-back-c1-acceptance-2026-08-06.md`. The uncapped size `3200` is a **`would-act`** number, not
   a filled order. `docs/HANDOVER-6.md` §5.8 applies: **a log line is not evidence of position size.**
3. **The Enter-key re-seed path was NOT exercised at runtime.** `CommitOnEnterKey` re-seeds by code
   inspection only — no key was pressed in a box during the run. `Leave` was exercised repeatedly.
4. **Line colour was not verified.** The lines were read as text through UIA. `Color.Yellow` when
   uncapped and `Color.Gray` when capped are **unconfirmed visually**; no screenshot was taken.
5. **`SB4`'s blanket-vs-sender distinction is unproven at runtime** and, per §4, unprovable through
   this UI.
6. **`SB6`'s superset was not owner-approved** — it is reported, not ruled.
7. **The other eight `CommitOnLeave` boxes still diverge.** Out of scope by §2.2b, filed in
   `ROADMAP-2026-08.md` §5. Untouched.

## 7. State left behind

- **Restored and verified by hash:** the harness bin's `orderapp-settings.json` is **byte-identical**
  to its pre-run backup (`3F52307C…`). `risk_size_bridge_trades` is back to `false`.
- **Removed:** the `bridge.json` created in the harness bin for the SB2 scratch-path form, and the
  scratch payload `C:\Dev\DeribitBridge\harness-signal.json` plus its backup.
- **`C:\Dev\DeribitBridge\verdict_signal.json` is byte-identical** (`C4205DBE…` before and after).
  **The live payload was never opened** — the SB2 preferred form worked exactly as documented. The
  engine was **not running** at any point, so no stop/restart was owed.
- **Changed and left changed:** the harness bin's `bridge-state.json` de-dupe watermark advanced to
  harness signal `610`. Git-ignored, harness-local, isolated from the owner's x64 bin
  (`docs/HANDOVER-6.md` §5.11). Log-only advancing the watermark is expected (§5.13).
- **No screenshots taken**, so nothing in `verify/out/` to delete.
