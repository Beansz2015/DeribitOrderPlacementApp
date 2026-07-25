# Handover — quick-wins runtime acceptance ✅ **COMPLETE 2026-07-25**

> **STATUS: acceptance-complete.** All of spec §Acceptance is closed (§4). Nothing is owed but the
> owner's push of `2b77b75..HEAD`. Read §4 before the next bridge runtime test — the sequencing rule,
> the session-policy tier trap, and the two corrected Trade-3 recipe defects are all there.

**Written:** 2026-07-25 by the Opus 4.8 coordinator seat · **For:** the incoming **Opus 5** seat.
**Scope:** finish the owner's runtime acceptance of the pre-ladder quick wins (Q1 ntfy notifier,
Q2 signal-tag lifecycle) plus two follow-ups already shipped this session. This is a **task**
handover — the standing era checkpoint is `docs/HANDOVER-3.md` + the auto-loaded memory index; read
those first, this second.

---

## 0. Read-first
1. `docs/HANDOVER-3.md` (era checkpoint) + the memory index (auto-loads).
2. `docs/spec-quickwins-notifier-signalcols.md` — the spec **and the two RATIFIED rulings in its
   header** (a: item-C TEXT columns kept, no migration; b: definitive refusals clear the pending
   tag, `"timeout"` carve-out stands).
3. `docs/impl-report-quickwins.md` (Fable implementer) → `docs/review-quickwins.md` (this era's
   coordinator review — **APPROVED**, incl. the grid-columns addendum).

## 1. Git state  (verify: `git rev-parse HEAD origin/master`)
- **HEAD = `2346fad`**, origin/master = `2b77b75` → **8 ahead, NOT pushed** (owner is the only pusher).
- The 8 ahead (oldest→newest): `f24c383` Q1 · `a0648ff` Q2 · `e750525` Q3 tools · `95dd83a` impl
  report · `4262785` rulings + N1/N2 specs · **`f49659b` review (this session)** · **`57db236` grid
  columns (this session)** · **`2346fad` settings-window fix (this session)**.
- Owner pushes `2b77b75..HEAD` when they call acceptance complete.

## 2. Code shipped this session (all gate-green, reviewed)
- **`57db236` — View Trades grid** now shows **Signal ID / Confidence** columns (display-only, bound
  to existing `TradeRecord` props; DB/lifecycle/fixtures untouched; form widened 1180→1340). This is
  how Q2 is verified now — one click, no SQLite tool needed. Addendum in `review-quickwins.md`.
- **`2346fad` — settings window** opened `Show(Me)` instead of `Show()` (was unowned since retirement
  B `cdc7ce4`, 2026-07-15) so it now hides/restores with the main form. `frmMainPageV2.vb:5550`.
- Gate EXECUTED after each: **GATE PASSED, 104/104**.

## 3. Runtime acceptance — PASSED (owner-confirmed)
| Test | Priority | Evidence |
|---|---|---|
| Q1 startup ping | default | ✅ phone `OrderApp started — TESTNET, breaker $1` |
| Q1 auto-STOP | **urgent** | ✅ phone `auto-STOP: STOP pressed` (owner arm→STOP) |
| Q1 tracked close ×2 (#90, #91 scratch) | high | ✅ phone `Position closed at … : scratch` |
| Q2 manual = NULL (#90, #91) | — | ✅ grid: both **empty** Signal/Confidence (incl. #90 armed-but-mode-Off) |
| **Q1 disabled-parity** (Opus 5 seat, 2026-07-25) | — | ✅ **A-B-A, server-verified — see §12** |

**Q1 disabled-parity — PASSED 2026-07-25 09:08–09:15 UTC** (this seat; no owner time, no trade).
`ntfy_url` removed from the x64 `secrets.json` (JSON round-trip, byte-identical restore after) and the
app relaunched:
- log line was exactly `Remote notifier: disabled (no ntfy_url)` — the other **five** startup lines
  were identical to the configured run, in the same order (behaviour otherwise byte-identical ✓);
- **zero posts proven server-side**, not merely assumed: the ntfy topic held 1 message before the
  disabled launch and still 1 after it (poll recipe in §12);
- restored → `Remote notifier: configured` and a fresh ping landed 09:15:26 UTC. A-B-A closed.

## 4. Runtime acceptance — ✅ **COMPLETE 2026-07-25** (Trades 2–4 owner-driven, this seat read back)

**Every item in spec §Acceptance is now closed.** 1 (gate) ✅ re-run at HEAD 104/104 · 2 (Q1) ✅ ·
3 (Q2) ✅ · 4 (Q3 tools) ✅ implementer, read-only run + archive opened · 5 (greps) ✅ implementer,
re-verified in `review-quickwins.md`.

| Trade | What it proved | Evidence |
|---|---|---|
| **2** | Q1 `acted` + Q2 bridge-tag | disp `10:00:32 acted (id 109534252220)`; ntfy `10:00:33 p=3`; grid **#92 = Signal 1 / MEDIUM** |
| **3** | Q2 step-3 clear | disp `10:19:54 rejected: cancel pending`; ntfy `10:19:56 p=3`; next manual **#93 = empty** |
| **4** | external close | log `Position closed.` (yellow); ntfy `10:30:36 **p=5 urgent**`; **no grid row** (correct — nothing to record) |

**Full ntfy topic audit (18 messages, server-side):** priorities match the spec table exactly —
startup `p=3` ×8, auto-STOP `p=5` ×5, acted `p=3`, rejected `p=3`, tracked close `p=4` ×2, external
close `p=5`. The disabled-parity gap is visible in the same record: **nothing between 09:08:58 and
09:15:26**, though the app ran at ~09:14 with `ntfy_url` removed.

### ⚠ Two Trade-3 recipe defects found and corrected (§7 was stale — fixed here)
1. **`-Entry 30000` does NOT make the entry rest.** `SignalBridge.vb:734`: *entry is a reference only
   — the app enters at top-of-book under its own slippage cap*. Trade 2 filled in ~1 s (chase crosses
   the spread), so "rest a limit → Cancel All Open" is an unwinnable sub-second race. The ATR-slippage
   guard doesn't use the payload entry either (`IsATRSlippageExcessive` seeds `originalSignalPrice`
   lazily from the live quote at the first chase evaluation).
2. **The documented fallback "open a position first → `rejected: position open`" is UNREACHABLE.**
   Bridge gate 4.6 tests `IsFlat`, which reads the *same* `positionSizeUSD` field as
   `PlaceAutomatedOrder:658` — so it stops at `refused: not_flat` **before** `SetPendingSignalTag`.
   Nothing staged ⇒ nothing cleared ⇒ the test proves nothing.

**The route that works (deterministic, use this next time):** `cancel pending` is the ONLY rejection
reachable *after* staging — the bridge deliberately leaves it to `PlaceAutomatedOrder`
(`SignalBridge.vb:679`). Cancel All Open sets `cancelPending = True` with a **4-second** self-clearing
timeout (`frmMainPageV2.vb:321`); the only early clear is the raced-abort repair (id 31), which cannot
fire while flat. So: bridge STARTED + flat → click **Cancel All Open** → land the payload within 4 s →
`rejected: cancel pending` → `ClearPendingSignalTag`. Give the owner a command that fetches the price,
**sleeps 2 s**, then writes (pass `-BinDir` so the UIA lookup can't eat the window); they press Enter
and click the button during the sleep. Worked first try.

### Sequencing rule that cost two failed attempts — READ BEFORE ANY BRIDGE TEST
**The payload must LAND while the bridge is STARTED.** Evaluation happens only on the file-change
event (FSW → 150 ms debounce). START does **not** re-evaluate the payload already on disk, and
`OnStalenessTick` (`SignalBridge.vb:440`) returns early when fresh — it can only ever *stop* the
bridge, never re-assess. So the payload is written **twice**:
1. write payload #1 → satisfies START's freshness precondition (dispositions `refused: interlock` — expected);
2. click **START** (within the freshness window);
3. write payload #2 → lands while started → acts.

Freshness = `2.5 × max(exec_resolution_min, 1)` = **2.5 min** for harness payloads
(`SignalBridge.vb:239`). Mode/ARM/Started all reset to Off/unticked/stopped at every app start.

### Session policy is ON and it bites
`LONDON = MEDIUM | CONFIRMED | 0.5` means LONDON accepts **MEDIUM only** — a HIGH payload is
`refused: policy(LONDON/tier)`. Buckets are **UTC**: ASIA <08:00, LONDON 08:00–12:59, NY ≥13:00
(`SignalBridge.vb:913`). Expect `size_mult 0.5 clamped to contract min 10` on every LONDON act
(harmless). Use `-Confidence MEDIUM` in London, or untick Policy.

- **Owner side-tasks — BOTH DONE 2026-07-25:** (a) settings-window minimize fix ✅ **owner-verified
  at runtime** (hides/restores with the main form); (b) ntfy **Instant Delivery** ✅ **enabled**
  (real-time push, no more FCM batching).
- **Cleanup done:** `tools/restore-payload.ps1` run — payload back to the engine's own
  (`signal_id=2`, direction `NONE`, instance `72bf33d9…`), `.harness-backup` consumed. Note that
  backup captured an engine payload from ~09:39 today (the engine ran briefly then: the two
  `NO TRADE [WEAK LONG] | refused: direction` rows), not the 07-24 one.
- **Testnet rows to delete when convenient:** #92, #93 (+ #90/#91 from the earlier seat).

## 5. ⚠ Runtime environment — VERIFY BEFORE ACTING
- **Owner's runtime bin = x64:** `…\DeribitOrderPlacementApp\bin\x64\Debug\net9.0-windows8.0\`.
  - `secrets.json` here is **testnet** + **`ntfy_url` set** (host ntfy.sh; the topic is a CREDENTIAL —
    never log/echo it). Confirm with a JSON parse that outputs only Environment + a present-bool.
  - **Already rebuilt to HEAD `2346fad`** (has all quick-wins + grid + settings fix).
  - `trades.db` here is the **live journal** — testnet test rows (#90/#91 + any new) pollute it;
    owner can delete them via View Trades right-click when done.
- **⚠⚠ THE VERIFY GATE DOES NOT BUILD THE x64 BIN.** `verify-gate.ps1` builds **AnyCPU Debug +
  Release only**. After ANY code change the x64 runtime bin is STALE until you rebuild it:
  ```
  dotnet build DeribitOrderPlacementApp\DeribitOrderPlacementApp.vbproj -c Debug -p:Platform=x64
  ```
  This bit us this session: the first launch ran a **07-23 pre-Q1 build** and the startup ping
  silently never fired — caught by **reading the log** (`Remote notifier: configured` was absent),
  not by assuming. Always confirm the running build (dll mtime vs the commit, or the `configured`
  line). `secrets.json` survives the rebuild (`CopyToOutputDirectory=PreserveNewest`, the bin copy
  is newer) — but back it up first anyway (`Copy-Item …\secrets.json $env:TEMP\…`).
- **launch-app.ps1 targets the AnyCPU bin (`bin\Debug`), NOT x64**, and refuses if any app window
  exists. The AnyCPU bin's `secrets.json` environment is UNKNOWN (could be **live**) — do **NOT**
  blindly run launch-app.ps1. Launch the **x64 exe directly** (see §8), after confirming no instance.
- **Engine is STOPPED** (no `DeribitVerdictEngine` process). `C:\Dev\DeribitBridge\verdict_signal.json`
  last written **2026-07-24 16:51 UTC** (engine was briefly up; stopped now) — **re-verify engine +
  payload state** before any write-payload run. No `.harness-backup` exists yet ⇒ write-payload has
  not run this session.
- **Circuit breaker persisted = `$1`** — a losing testnet trade trips it fast → a bonus way to see
  the breaker-trip urgent auto-STOP.
- ~~**Current app state: DOWN.**~~ **RESOLVED 2026-07-25 (Opus 5 seat) — testnet has RECOVERED and
  the app is UP.** The `code 11094 = internal_server_error` was confirmed a Deribit-side transient,
  exactly as diagnosed (nothing in our code changed). Evidence:
  - public API healthy (`get_time`, `ticker`, `book_summary`, `get_instruments` all 200);
  - **the authenticated path — the one that was 500ing — now succeeds:** `WebSocket authorized
    successfully` → `Connected successfully` → `Account summary received - Max Credits: 2000` →
    `Rate limits updated`. No 11094.
  - **App state at handoff: UP, PID 4660**, x64 bin, title `… V2.2 — TESTNET`, **Connected/ONLINE**,
    **flat** (Awaiting Orders, P/L 0), Amount **10**, mode **Off / disarmed**, breaker **$1**,
    `Remote notifier: configured`. Balance 0.01393137 BTC ≈ $888. **Preconditions for §7 are met.**
  - This seat wrote `verify/app.pid` = its own launched PID so drive-tier harness scripts work
    (that file exists to stop scripts driving a session the harness did not launch — this IS the
    harness-launched session). If the owner relaunches by hand, the file goes stale; harmless.
  - Note: each launch fires the Q1 startup ping, so the owner's phone saw 2 extra pings today
    (09:08:58 and 09:15:26 UTC) — those are this seat's, not a fault.

## 6. The two owner observations — both RESOLVED & CONFIRMED (2026-07-25)
- **Delayed notification** = ntfy/FCM push batching, **not the app** (ntfy's own server timestamps
  proved each POST fired at close time, a minute apart; nothing in `Post` defers). ✅ owner enabled
  **Instant Delivery** in the ntfy app → real-time push. (Production note: keep it on — urgent safety
  alerts must not batch.)
- **Settings window not minimizing with the main form** — diagnosed as the unowned `Show()` since
  retirement B (not a rebuild/quick-wins regression). Fixed `2346fad` (`Show(Me)`), ✅ owner-verified
  at runtime.

## 7. Exact remaining test steps (give these to the owner; they drive)
Preconditions each entry: **flat**; Amount ≥ 10.
- **Trade 2:** owner arms **Live + ARM + Start**, then runs at repo root:
  `powershell -NoProfile -ExecutionPolicy Bypass -File tools\write-payload.ps1 -Direction LONG -Confidence HIGH`
  → `acted` (phone). On fill, **Reduce Market** to close → tracked close. Grid → Signal ID=1/HIGH.
- **Trade 3:** armed, `…write-payload.ps1 -Direction LONG -Entry 30000 -Stop 29950 -Target 30100`
  (rests) → **Cancel All Open** before fill → then a manual Market entry → close → grid row empty.
- **Trade 4:** disarmed, manual Market entry → close it on **test.deribit.com web UI** → external
  (urgent).
- **Cleanup:** `tools\restore-payload.ps1`; delete test rows in View Trades if desired.

## 8. Harness tools (`tools/`) + the launch recipe used this session
- **read-log.ps1** `[-Tail N]` — observation-tier, finds the form by **title**, dumps `txtLogs`.
  Safe against any session (reads only). Use it to confirm `Remote notifier: configured`, dispositions,
  close lines.
- **write-payload.ps1** — **OWNER runs it** (it feeds the armed bridge = their trade). Crafts a fresh
  actionable payload with a **new instance GUID** each time (so the de-dupe watermark `last acted 24`
  never blocks it), backs the current payload up to `.harness-backup`. Refuses if the engine runs.
- **restore-payload.ps1** — restores the backup (cleanup).
- **Direct launch (x64):** confirm nothing running (`tasklist | grep DeribitOrder`), then
  `Start-Process` the x64 exe with `-WorkingDirectory` = its bin dir; poll `Find-MainFormElement`
  (from `tools\harness-common.ps1`) for the PID. **NOT** launch-app.ps1 (§5). The PowerShell tool
  wrapper prints a benign `$LASTEXITCODE`-undefined epilogue after such scripts — ignore it.
- **stop:** stop-app.ps1 keys off the harness PID file (won't see a directly-launched session) — stop
  by PID (`$p.CloseMainWindow()` then `Stop-Process`).

## 9. ⛔ The SAFETY BOUNDARY this seat held (keep it)
- This seat **did not place trades or arm the automated bridge** — executing a crypto trade, even a
  testnet BTC-PERPETUAL order, is a hard line. The **owner** drives every trade: arm, run
  write-payload (which triggers the armed bridge), manual entries, Reduce closes, web-UI close.
- This seat **does**: launch the app, read logs, rebuild the bin, verify DB/grid, and hand the owner
  exact commands. Do **not** run write-payload while the bridge is armed — give the owner the command.

## 10. Finish line & after
- **Acceptance criteria** = spec §Acceptance items 2 (Q1) and 3 (Q2): Q1 startup/acted/auto-STOP +
  disabled-parity; Q2 bridge-tagged / manual-empty / aborted-not-tagged / legacy intact. §3 above is
  done **plus Q1 disabled-parity (§3)**; §4 remains — i.e. Q1 needs only the `acted` ping and Q2
  needs the bridge-tag / step-3-clear pair, all three of which fall out of Trades 2–3.
- When the owner calls it complete → they **push** `2b77b75..HEAD`, then update the memory ledger.
- The broader queue then resumes per HANDOVER-3/memory: **N1** emergency-hoist spec, **N2** risk-sized
  bridge trades, **C1** v2 feedback file, Phase-3 P5 policy enable.

## 11. First moves for the incoming seat
1. `git rev-parse HEAD origin/master` (expect `2346fad` / `2b77b75`, ahead 8).
2. Read §0 docs. Confirm the gate: `tools\checks\verify-gate.ps1` → expect GATE PASSED 104/104.
3. Ask the owner whether testnet has recovered. If yes: confirm no app instance, confirm x64 bin
   config (testnet + ntfy) and that its dll postdates `2346fad`, then launch the x64 exe → confirm
   `Remote notifier: configured`, and walk the owner through Trades 2→4 (§7), reading back each result.
4. Hold the §9 boundary throughout.

**§11 status: ALL EXECUTED 2026-07-25 by the Opus 5 seat.** HEAD was `c42aaa3` (ahead **10**, not 8 —
the two docs commits landed after this file was written); gate re-run at HEAD = **GATE PASSED 104/104**;
x64 dll (00:40:16) postdates `2346fad` (00:40:03) so the runtime bin is at HEAD; testnet recovered;
app up and connected. Only the owner-driven Trades 2–4 remain.

## 12. Verifying ntfy posts server-side (recipe — use instead of trusting the phone)

The phone is a *delivery* check; the topic itself is the *send* check, and it settles
"did the app post?" without owner involvement. This is how §3's disabled-parity was proven and how
the earlier "delayed notification" observation was pinned on FCM batching rather than the app.

```powershell
$u = ((Get-Content <secrets.json> -Raw | ConvertFrom-Json).ntfy_url).Trim().TrimEnd('/')
$r = Invoke-WebRequest -Uri "$u/json?poll=1&since=all" -TimeoutSec 25 -UseBasicParsing
$text = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }
@($text -split "`r?`n" | ? { $_.Trim() }) | % { $o = $_ | ConvertFrom-Json
  "$([DateTimeOffset]::FromUnixTimeSeconds($o.time).UtcDateTime) p=$($o.priority) [$($o.title)] $($o.message)" }
```

Three traps, all of which cost this seat time:
- **PS 5.1 hands you `.Content` as a `byte[]`** for this response, so a naive `-split` iterates *bytes*
  and every parse silently yields empty objects — it reads exactly like "zero messages sent". Decode
  first. (Same family as the known PS-5.1 BOM quirk.) A **200-byte body split into "200 lines"** is the
  tell.
- **ntfy.sh retention is ~12 h** — yesterday's accepted pings are simply gone. Absence of old messages
  is not evidence of a fault.
- **Never print the URL.** Print counts, timestamps, priorities and message bodies only; the topic is a
  credential (anyone holding it can read *and post*).

⚠ **Editing `secrets.json`: `ntfy_url` is the LAST key.** Deleting its line leaves a trailing comma on
the previous line and the file becomes invalid JSON — and because `ConvertFrom-Json`'s failure is
*non-terminating*, a validate-then-write one-liner will happily write the broken file anyway. Use a
`ConvertFrom-Json` → `PSObject.Properties.Remove('ntfy_url')` → `ConvertTo-Json -Depth 10` round-trip,
stage to a temp file, validate keys **and** that `DeribitTestnet.ClientId/ClientSecret` survived, then
copy in. Always keep a byte-identical backup and restore by hash-verified copy (this seat did; the
first attempt did corrupt the file and the backup made it a non-event).
