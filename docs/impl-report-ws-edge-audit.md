# Impl report — make the `executor.ws` DOWN/OK edge AUDITABLE

**Spec:** `docs/spec-ws-edge-audit.md`.
**Base:** HEAD `6f6a731`. **Commits:** `090d88c` (pure seam + fixtures) · `f9a9b2d` (writer + call
sites) · this report.
**Diff:** 376 insertions, **0 deletions** — across `DeribitOrderPlacementApp/WsEdgeLog.vb` (new),
`DeribitOrderPlacementApp/frmMainPageV2.vb`, `tools/OrderCheck/Program.vb`, `.gitignore`.

> ## 🚨 HEADLINE — the runtime acceptance was NOT run
>
> `docs/spec-ws-edge-audit.md` §Acceptance item 2 requires a REAL disconnect, and it says: *"If you
> cannot provoke a disconnect, say so and stop. Do not claim the acceptance on the pure seam alone."*
>
> **I could not provoke one, so I am saying so.** Acceptance items 2, 3 and 4 are **NOT RUN**. The
> code is written, gated and committed; it is **not runtime-accepted**. §6 of this report gives the
> owner the exact procedure. Do not read the passing gate as evidence about a disconnect.

---

## 1. The tiering question, answered first — because it decided the design

`docs/spec-ws-edge-audit.md` §Model-and-effort asks the seat to investigate, **before implementing**,
whether the whole change can live inside `ExecutorFeedback.vb`. If it can, nothing touches the
receive path and **Sonnet/medium** is right. It calls that "the better design if it works".

**It does not work. Three independent reasons, each verified in code, and any one is fatal.**

| # | The blocker | Verified where |
|---|---|---|
| 1 | **The gray line needs a form member.** `docs/spec-ws-edge-audit.md` §2.2 requires BOTH the file and a host-log line via `AppendColoredText`. That is a **`Private Sub` on the form** — not reachable from `ExecutorFeedback`. Any "entirely inside the emitter" design still needs a callback registered from `frmMainPageV2.vb`, so the file is touched regardless. | `frmMainPageV2.vb:5349` — read the signature |
| 2 | **`Publish` is on the do-not-touch list.** `docs/spec-ws-edge-audit.md` §Do-not-touch names `ExecutorFeedback.Publish` / `WriteAtomic` / `ShouldWrite` explicitly. The `ws` value only flows through the emitter automatically **inside `Publish`**, so the hands-off design requires editing the one method the same spec forbids editing. | `docs/spec-ws-edge-audit.md` §Do-not-touch, row 3 |
| 3 | **🚨 The decisive one — `Publish` is gated on `IsConfigured`.** It returns on a single Boolean read when `bridge.json` carries no `feedback_output_path`. An audit trail hung off it would **silently not exist on an unconfigured bin**. | `ExecutorFeedback.vb:442` |

**Reason 3 is not hypothetical, and I confirmed it on this machine.** The harness bin
(`DeribitOrderPlacementApp/bin/Debug/net9.0-windows8.0/`) has **no `bridge.json` at all**, so
`ExecutorFeedback.IsConfigured` is `False` there and `PublishExecutorFeedback()` is a no-op in it
today. An emitter-hosted audit trail would have produced **zero rows** in the exact bin an
implementer would use to test it — an instrument that is absent precisely when nobody checks. That is
the failure mode `docs/HANDOVER-6.md` §7 lesson 1 exists to prevent: *verify the instrument can exist
before relying on it.*

**Therefore:** a new self-contained class, `DeribitOrderPlacementApp/WsEdgeLog.vb`, called from the
two sites in `frmMainPageV2.vb`. **The socket goes down whether or not the engine is listening, so
this log is unconditional.**

### 1.1 Was Opus/HIGH right? — **Yes, and not for the reason the spec predicted**

The spec expected the depth to be spent on the receive-path edit. It was not: that edit is four
comment blocks and two one-liners.

**The depth was spent on `docs/spec-ws-edge-audit.md` §Acceptance item 4 — and a lower tier would
have shipped a defect there.** See `SB1` in §5 of this report. The spec states that a graceful close
cannot append a spurious `DOWN` **because the emitter latches itself disposed before the socket
closes**. That reasoning is correct *about `ExecutorFeedback`* and **does not transfer to a writer
outside it**. A seat that implemented the spec as written, trusted the stated mechanism, and could
not run acceptance item 4 — which, as §6 records, **nobody here could run** — would have shipped a
spurious `DOWN` row on every normal shutdown and had a green gate saying so.

The trap is that the *requirement* in `docs/spec-ws-edge-audit.md` §Acceptance item 4 is right while
its *stated rationale* is inapplicable. Reading the requirement and skipping the rationale is exactly
what working quickly looks like.

**Where the depth was NOT needed:** the threading, which `docs/spec-ws-edge-audit.md` §2.4 had
already established and which I only re-verified (`AppendColoredText` does marshal via
`Me.BeginInvoke` and does guard the handle — `frmMainPageV2.vb:5371-5375`). The spec did that work.

---

## 2. What was built

### 2.1 `DeribitOrderPlacementApp/WsEdgeLog.vb` — new, 184 lines

| Member | Kind | Purpose |
|---|---|---|
| `StateWire(wsConnected)` | pure | `OK` / `DOWN`, **reusing `ExecutorFeedback.WsOk` / `WsDown`** rather than re-typing them |
| `ShouldLogWsEdge(lastLogged, current)` | pure | **the seam** — transition, not state |
| `FormatRow(atUtc, state, reason)` | pure | `2026-08-13T13:22:41Z \| DOWN \| server closed connection` |
| `FlattenReason(reason)` | pure | CR/LF → spaces, so one edge cannot become two rows |
| `LogPath` | property | `AppContext.BaseDirectory` + `ws-edges.log` |
| `NoteState(...)` | writer | decides, updates the field, appends, returns the row or `Nothing` |
| `Append(row)` | writer | `File.AppendAllText`, fail-silent |

Four decisions worth naming:

1. **The tokens are the emitter's own constants, not copies.** This log exists to evidence
   `executor.ws`; a row reading `UP` where the file reads `OK` would be evidence about a different
   thing. Sharing the constant makes agreement structural. (E2 — a C1 escalation, the wire string
   that was wrong in one place only — is the precedent.)
2. **`AppContext.BaseDirectory`, not a bare relative name.** See `SB3` in §5 of this report.
3. **The append happens inside the lock**, so rows land in the order the edges were decided. The two
   call sites are cold and rare, the lock is not nested, and `Append` cannot re-enter — so there is
   no ordering hazard being traded for it.
4. **The field is updated BEFORE the write and regardless of whether the write succeeds.** An
   unwritable file must cost one row, not turn one edge into a retry on every later publish.

### 2.2 `frmMainPageV2.vb` — 68 lines added, **0 removed**

- `LogWsEdge(reason)` at `frmMainPageV2.vb:806` — the one call every site uses, shaped exactly like
  `PublishExecutorFeedback()` above it and for the same reason.
- **OK edge** — `frmMainPageV2.vb:1472`, immediately after the publish at `:1464`.
- **DOWN edge** — `frmMainPageV2.vb:1591`, immediately after the publish at `:1581`.
- A local `exitReason` string in the receive loop, set in each of the four exit arms.

**Both publishes are untouched.** `git diff 6f6a731..HEAD` reports **376 insertions and 0
deletions** — there is no line in this change that moved, wrapped or conditioned an existing one.

### 2.3 The `exitReason` column — why the receive loop was touched at all

`docs/spec-ws-edge-audit.md` §2.2's suggested row carries `<reason-if-known>`. Deriving it at the
loop exit is not possible — by then every arm has converged. So a plain local string is set in each
of the four arms:

| Exit arm | `exitReason` |
|---|---|
| server-initiated close (`frmMainPageV2.vb:1516`) | `server closed connection` |
| `WebSocketException` | `WebSocket exception: <message>` |
| `OperationCanceledException` | `receive cancelled` |
| generic `Exception` | `receive error: <message>` |
| the `While` condition going non-Open — **takes no arm** | `socket no longer open` (the initialiser) |

**No control flow depends on it**; every edit is a string assignment. It is worth carrying because
"why did the socket drop" is most of what an audit of a disconnect is for, and the host log that used
to carry it **does not persist** (`docs/spec-ws-edge-audit.md` §1.3).

---

## 3. Acceptance — item by item, honestly

| # | Item | Result |
|---|---|---|
| 1 | The pure seam, no app running | ✅ **PASS** — 21 new fixtures, and **proven failable** |
| 2 | A REAL disconnect, end to end | 🚫 **NOT RUN — could not provoke.** §6 |
| 3 | No flooding (5 min connected) | 🚫 **NOT RUN** — needs a session. §6 |
| 4 | Graceful close is not a `DOWN` | 🚫 **NOT RUN** — needs a session. §6 |
| 5 | Nothing else regressed | ✅ **PASS** — `GATE PASSED`, OrderCheck **289/289**, censuses **unchanged** |
| 6 | `.gitignore` | ✅ **PASS** |

### 3.1 Item 1 — the pure seam ✅

**OrderCheck 268/268 → 289/289.** 21 new fixtures, covering the five cases the spec names plus the
ones it implies:

- fixture 1 — the tokens **are** `ExecutorFeedback`'s own `OK` / `DOWN`
- fixtures 2–6 — the five spec cases: first-ever call logged · OK→OK suppressed · OK→DOWN logged ·
  DOWN→DOWN suppressed · DOWN→OK logged
- fixtures 7–8 — an unknown current state is never a row; the comparison is **ordinal**
- **fixture 9 — 30 heartbeat republishes of an unchanged state decide to log ZERO rows.** This is
  `docs/spec-ws-edge-audit.md` §Acceptance item 3's logic, provable without the five-minute wait
- **fixture 10 — one disconnect and one recovery decide to log EXACTLY two rows, DOWN then OK**,
  through a realistic sequence including **five failed reconnects that re-enter the loop-exit path**
- fixtures 11–13 — the row format, under four cultures, and Local→UTC conversion
- fixtures 14a–d, 15 — CR/LF flattening; a missing reason is an empty third field, not a missing one

**Proven failable, twice, and one was not on purpose:**

1. **Deliberate.** Replacing the rule body with `Return True` (state, not transition) failed
   **fixtures 3, 5, 9 and 10**, and fixture 10's diagnostic printed the flooding failure mode
   verbatim — 16 rows where 2 belong. Reverted immediately; `289/289` restored before commit.
2. **Accidental, and more convincing.** The first version of fixture 14 asserted that a CRLF
   flattens to **one** space. It flattens to **two** — one per control character. The suite went to
   `FAILED 1/286` on my own miscount. The fixture was corrected (the code was right) and split into
   14a–d so the next reader does not have to count spaces either.

### 3.2 Item 5 — no regression ✅

- **`tools/checks/verify-gate.ps1` → `GATE PASSED`**, run by this seat. Both builds clean, **0
  warnings**.
- **OrderCheck `OK 289/289`** — 268 pre-existing, all still passing, + 21 new.
- **The nine `frmMainPageV2.vb` censuses — UNCHANGED**, occurrence-counted with `grep -o` per
  `docs/HANDOVER-6.md` §1's warning:

  `emergencyFired` 10 · `IsATRSlippageExcessive` 8 · `NextSlBackoff` 2 · `RecordCommandedSLPrice` 3 ·
  `slUpdateFailures = 0` 1 · `TakerFeeRate` 0 · `isPlacingOrder` 13 · `lastPlacementAdmittedUtc` 13 ·
  `placedOrderSizeUsd` 18 = **68 occurrences across 64 lines.**

  Baseline re-derived at `6f6a731` **before** editing, and identical after. The spec warned these
  could genuinely move because `frmMainPageV2.vb` is edited; **they did not** — the change adds a new
  method and two call sites and touches none of the nine terms.

### 3.3 Item 6 — `.gitignore` ✅

`ws-edges.log` was **already** ignored by the blanket `*.log` rule, and inside the bin by `[Bb]in/`.
It is now **also** listed explicitly next to `bridge-dispositions.log`, for the same reason that one
is listed: an operator reading `.gitignore` should not have to infer it. Verified with
`git check-ignore -v` at three paths — repo root, `DeribitOrderPlacementApp/`, and the Debug bin.
**The gate's repo guards still pass** (all five local-only files untracked).

---

## 4. 🚫 What was NOT established

**This section is the important one.** Read it before treating any of this as accepted.

1. **🚨 NO REAL DISCONNECT WAS OBSERVED. Not one row of `ws-edges.log` has ever been written by a
   running app.** Every claim about runtime behaviour in this report is derived from **reading code**
   and from **pure fixtures**. The file has never been created outside a code path I reasoned about.
   *This is precisely the state `docs/spec-ws-edge-audit.md` exists to end, and it is still true of
   the fix itself.*
2. **The gray host-log line has never been seen on screen.** `AppendColoredText`'s thread-safety was
   re-verified by reading it (`frmMainPageV2.vb:5371-5375`) — **not by watching a line appear from
   the receive thread**.
3. **The five-minute no-flood test was not run.** Fixture 9 pins the *rule*; it does not prove the
   *emitter's heartbeat* fails to reach this code, which is a claim about wiring, not logic.
4. **The graceful-close guard was not exercised.** `isClosing` is set at `frmMainPageV2.vb:6108`,
   before the token is cancelled and the socket closed, and the reconnect branch already trusts it
   for the same ordering — but **I did not watch a shutdown produce no row.** This is the single
   place I would look first if a runtime test surprises someone. See `SB1` in §5.
5. **The concurrency is argued, not demonstrated.** Two threads reaching `NoteState` at once is
   handled by a lock I read, not by a race I reproduced. OrderCheck cannot do this and should not
   pretend to (the same limit `docs/impl-report-c1-feedback-emitter.md` records for C1's fixture 9).
6. **`NoteState`, `Append` and `LogPath` have no fixtures** — they are stateful and do IO, and
   exercising them would write a real `ws-edges.log` into OrderCheck's bin and mutate process-wide
   state. The pure seam carries the logic, which is what `docs/spec-ws-edge-audit.md` §2.5 asks for.
7. **A manual re-click of Connect while already connected may write a spurious `DOWN`/`OK` pair.**
   `ConnectToWebSocketDirectly` cleanly shuts the existing connection down first, so the old receive
   loop exits and reaches the DOWN site. **Not verified either way**, and arguably honest rather than
   spurious — the socket really did drop and come back. Flagged so it is not read as a defect if it
   shows up in a real log.
8. **The owner's x64 bin does not carry this.** `docs/HANDOVER-6.md` §4's bin is a separate build;
   `tools/checks/verify-gate.ps1` is **AnyCPU-only** and does not touch it. The x64 bin needs a
   rebuild before any of this reaches the owner's session.

---

## 5. Spec-back findings — `SB` (findings against the DOCS, raised after the work)

Per `docs/HANDOVER-6.md` §7bb. **These are `SB` — docs findings — not `D` code defects; nothing here
is a bug in shipped code.** They are scoped to the ws-edge audit feature.

### `SB1` — 🚨 `docs/spec-ws-edge-audit.md` §Acceptance item 4's stated mechanism does not cover this writer

**The requirement is right; the reason given for it is not transferable.** The spec says shutdown
must not append a spurious `DOWN` *because* "the final write latches the emitter disposed before the
socket closes". True — and it protects **`ExecutorFeedback`'s own publish**, via
`ExecutorFeedback.Publish`'s `If _disposed Then Return` (`ExecutorFeedback.vb:445`).

`WsEdgeLog` is **outside** `ExecutorFeedback` — which §1 of this report shows it has to be — so that
latch does not reach it. Worse, the obvious repair is also wrong: `_disposed` is only ever latched by
`ShutdownWithFinalWrite`, which **returns immediately when the emitter is unconfigured**
(`ExecutorFeedback.vb:578`). Borrowing the emitter's latch would give a guard that silently does
nothing on exactly the unconfigured bin that §1 of this report showed is the common case.

**Implemented instead:** `LogWsEdge` returns early on **`isClosing`** — the form's own shutdown flag,
set at `frmMainPageV2.vb:6108` before the token is cancelled and the socket closed, and already
trusted by the reconnect branch for the same ordering. It is the only assignment of that flag in the
file (verified: `frmMainPageV2.vb:1320` declares it, `:6108` is the sole write).

**Amendment wanted:** `docs/spec-ws-edge-audit.md` §Acceptance item 4 should state the guard as
`isClosing`, and §2.4 should say that a writer outside `ExecutorFeedback` inherits none of the
emitter's latches.

### `SB2` — `docs/spec-ws-edge-audit.md` §Model-and-effort's "entirely inside `ExecutorFeedback`" route is unreachable, and its own §2.2 and §Do-not-touch say so

The spec asks the seat to investigate the Sonnet/medium route "first" and calls it "the better design
if it works". It cannot work, and **two of the three blockers are stated elsewhere in the same
document**: §2.2 requires a gray line via `AppendColoredText` (a form member), and §Do-not-touch
forbids editing `Publish` (the only place the value flows automatically). The third — the
`IsConfigured` gate — is in the code.

**No time was lost** (the investigation is §1 of this report and it is what the spec asked for), but
the option was offered as open when the document already contained its own refutation.

**Amendment wanted:** keep the investigation, and record the answer, so the next reader does not
re-run it.

### `SB3` — `docs/spec-ws-edge-audit.md` §2.2's "beside the exe" precedent is two-thirds wrong

§2.2 cites `bridge-dispositions.log`, `crash.log` and `AutoTradeLog.txt` as house precedent for
append-only logs "beside the exe". Only the **first** actually is:

| Log | Written as | Actually lands |
|---|---|---|
| `bridge-dispositions.log` | `Path.Combine(AppContext.BaseDirectory, …)` (`SignalBridge.vb:363`) | ✅ beside the exe |
| `crash.log` | `AppendAllText("crash.log", …)` (`ApplicationEvents.vb:41`) | ❌ the **working directory** |
| `AutoTradeLog.txt` | `AppendAllText("AutoTradeLog.txt", …)` (`frmMainPageV2.vb:6031`) | ❌ the **working directory** |

Today those coincide, because `tools/launch-app.ps1` sets `-WorkingDirectory` to the exe's folder.
They stop coinciding the moment anything launches the exe from elsewhere.

**Implemented:** `WsEdgeLog.LogPath` follows the **disposition log** — `AppContext.BaseDirectory`.
An audit trail that lands in a different place depending on how the app was started is not an audit
trail. Copying either of the other two would have been "house precedent" and wrong.

---

## 6. 🚨 For the owner — running acceptance items 2, 3 and 4

**Why I did not run them. Two separate blocks, both hard:**

1. **`tools/launch-app.ps1` REFUSED, correctly.** The owner's session — *"Deribit Order Placement App
   V2.2 — TESTNET"*, PID 25800 — was already running, and the harness never drives a pre-existing
   session (`tools/launch-app.ps1` refusal 1). **I did not close it.** Closing the owner's running
   app is not a seat's call, and it may be mid-session.
2. **Acceptance item 2 needs the network adapter disabled and re-enabled.** That is an elevated
   change to the machine's network configuration, it would drop the owner's own connectivity, and it
   is outside what a seat should do to a workstation. **Not attempted.**

**Neither block is a code problem, and neither is fixable from this seat.**

### 6.1 The procedure

**Prerequisite — `tools/checks/verify-gate.ps1` does NOT build the x64 bin.** Rebuild x64 first if
testing in the owner's bin, then re-check the `Environment` key and the `— TESTNET` title.

1. Close the running session. Launch a TESTNET session — `tools/launch-app.ps1` if using the harness
   bin. **No trade, no ARM, no START. This is a connectivity test only.**
2. Connect. **Expect one row already**: `<utc> | OK | connect`. This is the first-ever call, and
   fixture 2 pins it. `ws-edges.log` sits **beside the exe**.
3. **Item 3 first, while everything is calm.** Leave it connected **five minutes**. `ws-edges.log`
   must gain **ZERO** rows.
4. **Item 2.** Disable the network adapter ~10 s, re-enable. Expect **exactly two** new rows —
   `DOWN` then `OK` — two gray `ws edge:` lines in the host log, and the app recovering on its own.
   The `DOWN` row should read `server closed connection`, `WebSocket exception: …` or
   `receive error: …`.
5. **Item 4.** Note the row count. Close the app **normally** (the window's X, or
   `tools/stop-app.ps1`, which calls `CloseMainWindow()` and so runs `FormClosing`). **The row count
   must be unchanged** — no `DOWN` on shutdown. **This is the one `SB1` says to watch.** A `killed`
   message from `tools/stop-app.ps1` invalidates the test: a killed process runs no shutdown code, so
   a passing result would prove nothing.

Paste `ws-edges.log` into a runtime record. **Until step 4 produces two rows, the DOWN edge remains
exactly as unobserved as `docs/ROADMAP-2026-08.md` §5 records it.**

---

## 7. Commits

| # | Commit | Contents |
|---|---|---|
| 1 | `090d88c` | The pure seam + 21 OrderCheck fixtures. **No behaviour change** — nothing called it. |
| 2 | `f9a9b2d` | The writer, the two call sites, `exitReason`, `.gitignore`. |
| 3 | *(this)* | This report + `docs/ROADMAP-2026-08.md` §5 updated. |

`GATE PASSED` at commits 1 and 2. **Not pushed — the owner is the only pusher.**

---

## 8. Model and effort for the follow-up seat (`docs/HANDOVER-6.md` §7b)

The only work left is **runtime acceptance, which the owner drives**. If a seat is wanted to
transcribe the result into a runtime record and fold `SB1`–`SB3` back into
`docs/spec-ws-edge-audit.md`:

> **Model: Sonnet · Effort: medium · fresh conversation.**

**Why:** it is a docs pass plus a paste of observed rows. It touches no code, and specifically not
the receive path. **What would change the answer:** if acceptance item 2 or 4 **fails**, the fix is
back in `frmMainPageV2.vb`'s receive path and it returns to **Opus, HIGH** — item 4 especially, since
`SB1` is the finding most likely to be wrong in a way a green gate cannot see.
