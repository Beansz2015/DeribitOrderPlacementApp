# Implementation Report — pre-ladder quick wins (Q1 ntfy notifier · Q2 signal-tag lifecycle · Q3 ops tools · Q4 assessed-SKIP)

**Spec:** `docs/spec-quickwins-notifier-signalcols.md` (owner-approved 2026-07-24; transport ruling ntfy.sh)
**Seat:** fresh Fable-high implementer, one conversation, 2026-07-24
**Branch:** `master`, started from `2b77b75` (= `origin/master` at start — the owner had pushed)
**Build:** gate ran after every commit — Release + Debug builds green, repo guards all OK, **GATE PASSED** throughout. OrderCheck fixtures: 96 → **102** (Q1, +6 rate-limit seam) → **104** (Q2, +2 migration extension).
**Push status:** NOT pushed. 4 local commits (this doc = 4th).
**Runtime scope:** Q3 scripts executed against the real files (read-only). Q1/Q2 app behaviour is owner-runtime acceptance (checklist below) — nothing live was driven by this seat.

---

## Q1 — ntfy remote notifier ✅ `f24c383`

**Files:** `RemoteNotifier.vb` (new), `AppSecrets.vb`, `secrets.example.json`, `frmMainPageV2.vb`, `SignalBridge.vb`, `tools/OrderCheck/Program.vb`

- **`RemoteNotifier.Post(title, message, priority, tags)`** — internally fire-and-forget (`Task.Run` + one shared static `HttpClient`, 10 s timeout), swallows every exception at dispatch AND inside the send, posts body = message with `Title`/`Priority`/`Tags` headers (`TryAddWithoutValidation` so a bad header can never throw). Every call site is a plain one-liner.
- **Config:** `AppSecrets.NtfyUrl` from `secrets.json` key `ntfy_url`, read immediately after JSON parse (a bad credential block still configures the channel). Absent/blank ⇒ every `Post` returns at the first check — inert, ships safe. Documented in `secrets.example.json`. The URL is treated as a credential: the only startup output is the spec's single line `Remote notifier: configured` / `disabled (no ntfy_url)` (placed right after the credentials-loaded log).
- **Rate limit:** pure seam `ShouldSend(nowUtc, lastSentUtc, sentInWindow, isUrgent)` — non-urgent needs ≥ 5 s since last send AND < 12 in the current 60 s window (drop silently beyond); urgent bypasses both. Window bookkeeping in `Post` under a private lock (no network under the lock). 6 OrderCheck fixtures pin the seam (first-ever, 4 s drop, 5 s pass, window-full drop, 11-sends pass, urgent-bypasses-both).
- **Call sites (exactly the spec table, one line each):**

| Event | Site | Priority |
|---|---|---|
| App start | end of `frmMainPageV2_Load` (after `InitialiseSettings`, so the persisted breaker is live) — `"OrderApp started — LIVE/TESTNET, breaker $X"` | default |
| Significant disposition | `EmitDisposition`, gated on the item-H `IsSignificantDisposition` predicate; message = the host-log line text (computed once into `hostLine`, shared with the existing `_log`) | default |
| Bridge auto-STOP | `ForceStop`, inside the `wasStarted` branch beside the `auto-STOP:` log — covers every cause incl. breaker trip | urgent |
| Emergency market-stop | both branches of `UpdateStopLossForTriggeredStopLossOrder`, beside `Alert("emergency_stop")` | urgent |
| Tracked close | all three `CompletePositionClose` branches beside the item-D `Alert` calls, incl. P/L (`profit $X` / `loss $X` / `scratch`) | high |
| External/untracked close | item D's external-close branch | urgent |

- **Receive-thread discipline:** no call site awaits, wraps, or can observe a failure; grep in §Acceptance below.

## Q2 — signal-tag lifecycle ✅ `a0648ff`

**Files:** `frmMainPageV2.vb`, `SignalBridge.vb` (call sites only), `tools/OrderCheck/Program.vb`

**⚠ Spec deviation (stale migration bullet) — the DB surface already existed.** The spec asks for a migration adding `SignalId INTEGER NULL` / `SignalConfidence TEXT NULL`. Ergonomics item C (`2686404`) already shipped both columns as `TEXT NOT NULL DEFAULT ''`, plus the `TradeRecord` properties, the insert parameters, and the null-safe readers — Phase A wrote them empty by design ("bridge fills them in Phase B", `frmMainPageV2.vb` comment at `RecordCompletedTrade`). Following the bullet literally would mean a destructive SQLite table rebuild against every already-migrated DB (including the soak-era journal) for zero query benefit. **Ruling taken: keep the shipped TEXT schema; "manual trades record NULL" is satisfied as the `''` default (the spec itself says "NULL/empty").** No schema change in this commit. Recommend a one-line spec amendment.

**The tag lifecycle (spec steps 1–4, implemented exactly, plus one gap closure):**

1. **Stage** — host fields `pendingSignalId As Long = -1` / `pendingSignalConfidence As String = ""` (declared beside the item-C close trackers with the full lifecycle comment), written by the bridge act path via `Friend Sub SetPendingSignalTag`, one line beside its `SetTradeTargets` call and **before** `PlaceAutomatedOrder` — the entry-fill echo can beat the placement ack, so the tag must already be staged when the fill lands.
2. **Promote** — at the item-C flat→nonzero transition (the single blessed position-entered detection): `currentTradeSignalId/-Confidence = pending…`, stage cleared. A manual entry promotes the *empty* stage and therefore records `''` — this is what makes "manual trades record NULL" true by construction.
3. **Teardown clears** — one field-pair reset beside the existing id-nulling in BOTH cancel teardowns (scoped `CancelWorkingEntryCoreAsync` — the ATR-slippage abort path — and the nuclear `CancelOrderAsync`), touching nothing else. `currentTrade*` deliberately untouched there: a live position's tag survives a nuclear cancel and dies at its close.
4. **Record + clear** — `RecordCompletedTrade` (called only from `CompletePositionClose`) writes the current pair into the `TradeRecord` when `currentTradeSignalId >= 0`; the pair is cleared beside the item-C trackers (both branches, so an external close of a bridge trade cannot leave a tag behind).

**⚠ Spec-gap closure (flagged for coordinator/owner ruling):** the spec's four steps leave one path where an aborted bridge act could still tag a later manual trade — a **definitive placement refusal** (`rejected: not connected / rate limit / cancel pending / position open / working entry / exchange error`): no order exists, no teardown ever runs, the stage would linger. Added: the bridge's rejected branch calls `Friend Sub ClearPendingSignalTag` — **except for `rejected: timeout`**, which the placement path explicitly documents as "the order may exist; echoes remain the source of truth": there the stage deliberately survives, so a late fill still tags the position (a dead stage dies at the next teardown or is overwritten by the next act). This closes the spec's own intent ("an aborted bridge entry can never tag a later manual trade"); the residual lingering-stage window (timeout + order never existed + manual trade next, no teardown between) is accepted and on record.

**Fixtures:** the item-C migration fixture set already covered "old DB opens / legacy defaults (incl. `SignalId = ''`) / idempotent re-open"; extended the enriched row to round-trip `SignalId = "9001"` / `SignalConfidence = "HIGH"` and pinned the legacy `SignalConfidence = ''` explicitly (+2, → 104).

## Q3 — ops tools ✅ `e750525`

**Files:** `tools/backup-orderapp.ps1`, `tools/policy-report.ps1` (new; no app code). Both UTF-8 **with BOM** (the PS 5.1 quirk).

- **backup-orderapp.ps1** `-TargetDir <dir> [-BinDir <bin>]` — stages `orderapp-settings.json`, `trades.db`, `bridge-state.json`, `bridge-dispositions.log` from the x64 session bin (default resolved from the script location; the AnyCPU bin belongs to the harness) into a temp folder, zips to `orderapp-backup-YYYYMMDD-HHmmss.zip`. Missing files warn-and-skip; zero present files fails. S3 sync one-liner documented in the header (the owner's scheduled-task wrapper). **Tested:** 4/4 files staged from the real bin, archive re-opened and verified entry-for-entry.
- **policy-report.ps1** `[-LogPath <log>] [-Since <date>]` — read-only over the disposition log; soak-frozen 7-field ` | ` parser (7-part max split so a disposition keeps itself whole; malformed rows counted, never fatal); per UTC session bucket (ASIA 00–07Z / LONDON 08–12Z / NY 13–23Z, the engine-identical boundaries) × confidence tier: `would-act` / `acted` / `refused: policy(…/tier)` / `refused: policy(…/context)`. **Tested against the real log:** 1912 rows total, 1693 since 2026-07-16, 0 malformed; 4 `acted` (the 2026-07-23 live session), **zero policy refusals — the ships-disabled parity holds on the live stream.** (Standing watchlist rule: row count 1912 at this read; the full join review completed clean 2026-07-22 — delta join only if the owner extends the soak.)

## Q4 — USE-ENGINE-LEVELS button: **SKIPPED** (spec-sanctioned, measurements on record)

The spec makes Q4 droppable: "If space is not clean, SKIP and report." Measured from `frmMainPageV2.Designer.vb`:

- `ManualTPSL` group: Location (545, 413), Size 528×95 — **flush against the form's right edge** (545+528 = 1073 of ClientSize 1080).
- Interior: `txtManualTP` (63, 45) 200×47, `txtManualSL` (269, 45) 200×47 → the boxes end at x = 469; free interior strip right of the SL box ≈ **56 px** wide.
- The caption `USE ENGINE LEVELS` single-line at the form's Calibri 12 pt bold measures ~150 px — ~3× the available strip. Below the group, `PlacedOrders` starts at y = 502 (flush); no top-level free region adjoins the group.

Space is not clean without a layout redesign of an area the owner uses daily mid-soak — exactly the WordWrap-lesson risk the spec guards against. Skipped; no code written. If the owner wants it, it needs an owner-approved placement first (e.g. widening/rehoming the group), then the snapshot-accessor design in the spec stands as written.

---

## Acceptance status

1. **Gate per commit** ✅ — GATE PASSED at `f24c383` (102/102), `a0648ff` (104/104), `e750525` (104/104). New fixtures: 6 (rate-limit seam) + 2 (migration extension).
2. **Q1 live check** — OWNER: create a secret ntfy topic + install the phone app, add `ntfy_url` to `secrets.json`, start the app → phone shows "OrderApp started — …, breaker $X" and the log shows `Remote notifier: configured`. Then the isolated-harness payload protocol (testnet bin, own `bridge.json` — engine untouched): one acted → notification; an auto-STOP → urgent notification. Remove `ntfy_url` → startup logs `disabled (no ntfy_url)`, zero posts, behaviour otherwise byte-identical.
3. **Q2** — OWNER (next live/testnet trade): bridge trade records its SignalId/Confidence in the Results grid/DB; a manual trade records empty; slippage-aborted bridge entry followed by a manual trade → the manual trade is NOT tagged (step-3 clear). Legacy DB rows intact (fixture-proven; also visible in the grid).
4. **Q3** ✅ — both scripts ran read-only against the real files; the backup archive opens (verified entry-for-entry).
5. **Greps** ✅ (run at HEAD):
   - `Await.*RemoteNotifier` over the repo → **zero hits** (no call site awaits the notifier); 9 `RemoteNotifier.Post` call sites total, matching the spec table (3 tracked-close branches = one event).
   - The Q2 tag-clears: `pendingSignalId = -1` at exactly 4 sites — `ClearPendingSignalTag`, the promotion, and one per teardown beside the existing id-nulling; nothing else in the teardowns touched.
   - SL/commanded tripwires unchanged: `ResetCommandedSLPrices()` = 8 sites, `cancelPending = False` = 5 sites.

## Commits

| # | Hash | Message |
|---|---|---|
| 1 | `f24c383` | Remote notifier: ntfy fire-and-forget alerts (+ rate-limit fixtures) |
| 2 | `a0648ff` | Trade DB: SignalId/SignalConfidence tag lifecycle (+ migration fixture extension) |
| 3 | `e750525` | Tools: backup-orderapp + policy-report scripts |
| 4 | (this doc) | Docs: impl report - quick wins |

## Open items for the coordinator review

- The Q2 **stale-migration-bullet deviation** and the **rejected-placement gap closure** (with its timeout carve-out) both need explicit APPROVE/REJECT; the spec should be amended either way per the escalation protocol.
- Q4's skip measurement is on record above if the owner wants to rule on a placement.
