# Quick-wins review — Q1 ntfy notifier · Q2 signal-tag lifecycle · Q3 ops tools (2026-07-24)

**Reviewer:** coordinator seat (Opus high) · **Scope:** `f24c383..95dd83a` (4 commits) against
`docs/spec-quickwins-notifier-signalcols.md` **including the two RATIFIED coordinator rulings in
its header** (a: keep the item-C TEXT columns; b: definitive refusals clear the tag, timeout
carve-out stands). **Method — HANDOVER-3 §5:** `git show` on every commit, **EXECUTED
`tools/checks/verify-gate.ps1`**, re-ran the greps myself, adversarial pass on the invariants,
arithmetic re-derived. Nothing was driven live — Q1/Q2 runtime remain owner acceptances.

## VERDICT: **APPROVED — all three code commits + tools + impl report verified. Ships DISABLED (no `ntfy_url`); byte-identical off-path.**

The implementation matches the spec and **both ratified rulings exactly**. The gate is green, the
SL/commanded tripwires are untouched, the receive-thread discipline holds, and the one genuinely
load-bearing ordering question (record-vs-clear in the close path) re-derives correct.

---

## Gate — EXECUTED at HEAD

`GATE PASSED` · **104/104 fixtures** · Release build OK · Debug (vbproj-direct) OK · OrderCheck OK ·
all repo guards OK (secrets.json / bridge.json / bridge-state.json / orderapp-settings.json
untracked — no credential or state leak). The fixture progression re-derives from the diffs:
96 → **102** (Q1: +6 rate-limit-seam) → **104** (Q2: +2 migration-extension). Both new sets pass,
including `notifier: urgent bypasses BOTH limits`, `migration: enriched row round-trips
SignalId/SignalConfidence`, and `migration: legacy row reads SignalConfidence '' (NULL-safe)`.
(HEAD is `4262785`, one docs-only commit past the range; it changes no code or fixtures, so 104/104
IS the post-`e750525` state.)

## Grep tripwires — all match the expected values

| Grep | Expected | Found | Verdict |
|---|---|---|---|
| `ResetCommandedSLPrices()` | 8 | **8** (7 paired reset sites + 1 def) | unchanged ✓ |
| `cancelPending = False` | 5 | **5** | unchanged ✓ |
| `Await.*RemoteNotifier` (code) | 0 | **0** (`Post` is a `Sub` — un-awaitable by construction) | ✓ |
| `RemoteNotifier.Post` (code call sites) | 9 | **9** | ✓ |
| `pendingSignalId = -1` (assignments) | 4 | **4** — `ClearPendingSignalTag` @639, promotion @2656, nuclear teardown @3678, scoped teardown @3758 | ✓ |

The Q2 clears sit **beside the existing teardown resets and touch nothing else** — verified by
reading both methods, not just the diff: nuclear (`CancelOrderAsync` @3676–3679) adds the field-pair
after the `ReduceOrderId`/`reduceOrderPrice`/`reduceOrderAmount` nulling; scoped
(`CancelWorkingEntryCoreAsync` @3757–3759) adds it after `placedPrice`/`legAnchorPrice`/
`pendingReanchorFill`, before `ResetOrderAttempt()`. `cancelPending`, the id-nulling, and every
other line in both teardowns are untouched. `currentTrade*` deliberately **not** cleared in either
(a live position's tag survives a nuclear cancel and dies at its close).

---

## Q1 — ntfy notifier (`f24c383`) — APPROVED

- **`RemoteNotifier.Post` is hot-path-safe by construction:** `Task.Run` + one shared static
  `HttpClient` (10 s timeout), **two** `Try` boundaries (dispatch AND inside the send), all
  exceptions swallowed, headers via `TryAddWithoutValidation` (a header the framework dislikes
  drops the header, never the notification, never throws). Every call site is a bare one-liner —
  no `Await`, no wrapper, no `Try` at the site. `Return` inside the `SyncLock` releases the lock
  (VB compiles `SyncLock` as `Try/Finally`); no network I/O is done under the lock.
- **`ShouldSend` pure seam re-derived against all 6 fixtures** — correct at each: first-ever
  (`MinValue`) sends; 4 s < 5 s min-interval drops; exactly 5 s sends (strict `<`); `sentInWindow
  >= 12` drops; 11 sends; `isUrgent` short-circuits before both checks.
- **Inert without config, ships safe:** `AppSecrets.NtfyUrl` parsed right after `JObject.Parse` —
  BEFORE credential validation (a bad credential block still configures the channel), null-safe +
  `.Trim()`. `Post` early-returns at the first check when blank; `IsConfigured` gates the startup
  line. Exactly **one** startup log line (`configured` / `disabled (no ntfy_url)`); the URL is
  treated as a credential and is never logged. `secrets.example.json` documents it, ships `""`.
- **9 call sites = the spec table, priorities exact:** startup ping (default), significant
  disposition (default), auto-STOP (urgent), emergency stop ×2 (urgent), tracked close ×3
  profit/loss/scratch (high), external close (urgent). The 2 emergency + 3 close sites collapse to
  their single spec "events".
- **`EmitDisposition` refactor is behaviour-preserving:** `hostLine` is extracted once and reused;
  the host-log text is byte-identical to the prior inline string. The notifier fires ONLY on
  `IsSignificantDisposition(disposition)` (`StartsWith "acted"`/`"rejected"`), a strict subset of
  the host-log emission — so no phone buzz for idle `would-act` heartbeats. The unconditional
  disposition **FILE append and `_lastDisposition`** sit above this block, untouched → join
  integrity / soak fidelity intact. Consequence worth stating: the notifier is **silent through the
  log-only soak** (log-only mode never emits `acted`/`rejected:`), loud only when live-trading — the
  intended behaviour.
- **Startup ping cannot throw during `Load`:** it reads `CircuitBreakerUsd`, whose getter is
  null-safe (`If(userSettings IsNot Nothing, …, 10D)`), and `userSettings` is loaded at @751 well
  before the ping @798; `Post` is itself `Try`-wrapped. The persisted breaker (`§8 R1`) is live by
  then, so the phone line shows the real value.

## Q2 — signal-tag lifecycle (`a0648ff`) — APPROVED, both rulings matched

**Ruling (a) — keep the item-C TEXT columns — MATCHED.** No schema migration in this commit; the
`TEXT NOT NULL DEFAULT ''` columns from ergonomics item C are kept and populated via the lifecycle.
`RecordCompletedTrade` writes `SignalId = currentTradeSignalId.ToString(Invariant)` (a string, TEXT-
consistent); a manual trade leaves `currentTradeSignalId = -1`, the `If currentTradeSignalId >= 0`
block is skipped, and the `TradeRecord`'s `''` default stands = the spec's "NULL/empty". The
enriched-row and legacy-`''` fixtures pin both directions.

**Ruling (b) — definitive refusals clear, timeout carve-out stands — MATCHED, and the string is
exact.** `SignalBridge` @~757: `If result.Reason <> "timeout" Then _host.ClearPendingSignalTag()`.
I enumerated every `PlacementResult.Reason`: the **only** `"timeout"` is @701 (the placement-ack
TimedOut lateness — the order may exist, echoes remain the source of truth), so it alone survives;
every definitive refusal — `not connected` / `rate limit` / `cancel pending` / `position open` /
`working entry exists` / the `"{code}: {msg}"` exchange rejection @1605 / the unknown-side/kind
guards — is `<> "timeout"` and clears. The carve-out is precise, not approximate.

**The four lifecycle steps, each read in place:**
1. **Stage** — `SetPendingSignalTag(p.SignalId, p.Confidence)` beside `SetTradeTargets`, **before**
   `PlaceAutomatedOrder` (the entry-fill echo can beat the placement ack, so the tag must be staged
   when the fill lands). Writes plain fields, no marshal — correct: this runs on the bridge
   processing thread and touches no control (contrast `SetTradeTargets`, which marshals because it
   writes textboxes).
2. **Promote** — at the single blessed flat→nonzero transition (`HandleOrderPositionUpdates`,
   `If Not wasOpen`, @2654–2657), beside the item-C `maePrice/mfePrice/plannedStopAtEntry` seeding:
   `current* = pending*`, then stage cleared. A manual entry promotes the **empty** stage → records
   NULL by construction. No duplicate detection site.
3. **Teardown clears** — the two sites above; beside the existing resets, nothing else touched.
4. **Record + clear** — see the ordering note below.

**Load-bearing ordering — record-then-clear — CORRECT.** In `CompletePositionClose`,
`RecordCompletedTrade(...)` is **called at @4978** (its body reads `currentTradeSignalId` @5055),
and `current*` is cleared **afterward at @5009–5010**. My first read of the raw diff flagged this as
a possible always-`-1` bug because the clear's line number (5009) precedes the record block's line
number (5055); reading the actual control flow dissolves it — `RecordCompletedTrade` is a *separate
method* physically below `CompletePositionClose` but *invoked* at @4978, before the clear. The tag
is recorded, then dies with the position. Both close branches flow through the clear, so an external
close cannot leave a tag behind.

**Threading:** `pending*`/`current*` are plain engine fields written on the bridge/receive paths
that already own the surrounding state (staged before placement; promoted/read on the receive
thread) — consistent with invariant §4.11 ("receive thread = engine fields only") and the
existing cross-thread engine-field pattern. The happens-before (stage → placement → network → fill
echo) makes visibility a non-issue in practice, same as `positionAvgEntry` et al.

## Q3 — ops tools (`e750525`) — APPROVED

Tools-only, **no app code**. Both UTF-8 **with BOM** (PS 5.1). `backup-orderapp.ps1`: stages the 4
operational files from the x64 session bin to a temp folder, zips, cleans up in `finally`; missing
files warn-and-skip, zero-present fails; read-only w.r.t. the app; S3 one-liner in the header.
`policy-report.ps1`: read-only over `bridge-dispositions.log`, 7-field ` | ` parser (7-part max split
keeps a separator-bearing disposition whole), malformed rows counted never fatal, per UTC bucket ×
tier. The bucket boundaries (`>=13 NY`, `>=8 LONDON`, else `ASIA`) match the engine-identical
boundaries the OrderCheck session-bucket fixtures pin (07:59 ASIA / 08:00 LONDON / 12:59 LONDON /
13:00 NY). Neither script is on any safety-critical path; not re-run here (the impl report tested
both against the real files — archive 4/4, 1912 rows / 0 malformed / zero policy refusals).

## Docs (`95dd83a`) — impl report is accurate

Every claim in `impl-report-quickwins.md` checks out against the code. The two items it flags for a
ruling — the stale-`SignalId INTEGER NULL` migration bullet, and the rejected-placement gap closure
with its timeout carve-out — are **exactly** the two questions ratified in the spec header; both are
implemented to match the rulings. Q4's spec-sanctioned SKIP is on record with the geometry
measurements.

---

## Minor observations (non-blocking, on record — no change requested)

- **M1 — fixed (not sliding) rate window.** Up to ~2×12 posts can land in a ~60 s real span across a
  window boundary. Matches the spec's "max 12/min" per-window intent; the fixture-pinned decision is
  the pure `ShouldSend` seam, and the window bookkeeping around it is a standard fixed-window
  approximation. Accepted characteristic.
- **M2 — urgent increments the window counter.** An urgent post passes `ShouldSend` and so bumps
  `_sentInWindow`; a burst of ≥12 urgents in a window could then drop subsequent *non-urgent* posts.
  Harmless — urgent events (auto-STOP / emergency / external close) are rare and high-signal, urgent
  itself never drops, and shedding a routine disposition line while 12 emergencies fire is fine.
- **M3 — `hostLine` now built unconditionally** per consumed payload (was conditional on the log
  predicate). One extra interpolated-string allocation ~once/min in soak, off the receive/quote hot
  path. Negligible.
- **M4 — external/liquidation close of a *bridge* trade records no signal tag.** That path records
  no trade at all by design, then clears `current*`. So an externally-closed bridge position gets no
  per-signal attribution. Pre-existing close-recording limitation, **not a Q2 regression** — noted
  so the eventual per-signal P/L query isn't assumed to cover liquidation exits.

## Owner runtime acceptances still open (not review-blocking)

- **Q1 live check** — set `ntfy_url` → phone shows "OrderApp started — …, breaker $X" + log
  `configured`; isolated-harness payload protocol (testnet bin, own `bridge.json`, engine untouched)
  → one `acted` notification, one urgent auto-STOP; remove `ntfy_url` → `disabled`, zero posts,
  behaviour byte-identical.
- **Q2 next live/testnet trade** — bridge trade records its SignalId/Confidence; a manual trade
  records empty; a slippage-aborted bridge entry followed by a manual trade → the manual trade is
  **not** tagged (the step-3 clear); legacy rows intact (fixture-proven).

## Bottom line

Approve `f24c383..95dd83a` as implemented. No code changes requested in the range itself. The spec
should carry the two header rulings forward as the contract (the impl already treats them as such);
the owner runtime acceptances above are the only remaining gate before Q1/Q2 are "done" per the §5
"reviewed ≠ done until the owner runs it" rule.

---

## Addendum 2026-07-24 — grid surfacing of the signal tag (owner-directed follow-up)

**Context.** Preparing the Q2 runtime-acceptance steps surfaced a usability gap: the **View Trades**
grid (`btnViewTrades_Click`) shows the item-C metric columns (MAE/MFE/R/Fees) but **not**
`SignalId`/`SignalConfidence` — Q2 writes the tag to `trades.db` only. With no `sqlite3`/`pwsh` on
the box, verifying Q2 (and using the per-signal attribution the feature exists for) meant an external
SQLite viewer. Owner directed: surface the two columns in the grid so acceptance is one click.

**Change (display-only, `frmMainPageV2.vb` `btnViewTrades_Click`).** Two `DataGridViewTextBoxColumn`s
— **"Signal ID"** → `SignalId` (70 px), **"Confidence"** → `SignalConfidence` (90 px) — added before
the Result column, bound to the existing `TradeRecord` properties; grid form widened `1180 → 1340`
to seat them (the same move item C made `1000 → 1180`). Empty string renders for manual trades.

**Verification.**
- `TradeRecord.SignalId` / `SignalConfidence` are `Public Property` (TradeRecord.vb:31–32) — bindable
  by `DataGridView` exactly like `MaeUSD`/`FeesUSD`; the grid is `AutoGenerateColumns = False` so only
  the explicit columns show. `RefreshTradeGrid` (post-delete) resets `DataSource` on the same grid, so
  the columns persist across a refresh — no second edit needed.
- **No change to the DB, the tag lifecycle, the insert, or any fixture** — this reads existing data
  the record path already writes. OrderCheck is UI-agnostic, so the count stays 104.
- **Gate EXECUTED after the change → `GATE PASSED`, 104/104**, both builds green.

**Scope note.** This completes the spec's own "records its SignalId/Confidence in the **Results
grid**/DB" wording (previously only the DB half was satisfied) and makes the Q2 acceptance check a
one-click read of the grid. No behavioural or safety surface touched.
