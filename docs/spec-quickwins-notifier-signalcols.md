# Spec — pre-ladder quick wins: ntfy remote notifier (Q1) + trade-DB signal columns (Q2) (+ Q4 optional, Q3 tools)

**Origin:** `ROADMAP-2026-08.md` §2, owner-approved 2026-07-24; transport ruling: **ntfy.sh**.
**Recommended implementer:** fresh **Fable high** while the window lasts (Q2 touches the acted/
close-recording paths; Q1's call sites sit beside hot paths and must stay fire-and-forget).
One conversation; suggested order Q1 → Q2 → Q3 tools → Q4 (Q4 only if headroom).
**Target:** new `RemoteNotifier.vb`, `SignalBridge.vb` (call sites only), `frmMainPageV2.vb`,
`TradeDatabase.vb`, `AppSecrets`/`secrets.example.json`, `tools/OrderCheck`, `tools/*.ps1`.
**Ground rules:** the standing ones — gate per commit (GATE PASSED required), one commit per item,
never push, receive-thread discipline (NOTHING here may block or throw on the receive/FSW/quote
paths), scope discipline, impl report at the end. **Do-not-touch:** the §4 gate chain order and
disposition tokens (SOAK-stable), SL/commanded invariants, the seed-before-commit orderings,
`CancelWorkingEntryCoreAsync`'s existing teardown semantics (Q2 adds ONE benign tag-clear beside
the existing resets — nothing else).

---

## Q1 — ntfy remote notifier

**Why:** sound/taskbar alerts are worthless off-RDP; production is a remote AWS box (checklist §9).

### Config (secrets, not settings)

`AppSecrets` gains optional `NtfyUrl` (key `ntfy_url` in `secrets.json`; documented in
`secrets.example.json` as e.g. `https://ntfy.sh/<your-secret-topic>`). Absent/blank ⇒ the notifier
is INERT (ships safe). The topic URL is a credential: **never log it** — log exactly one line at
startup: `Remote notifier: configured` / `disabled (no ntfy_url)`.

### `RemoteNotifier.vb` (new, small, self-contained)

- `Friend Shared Sub Post(title As String, message As String, Optional priority As String = "default", Optional tags As String = "")`
  — **fully fire-and-forget INTERNALLY** (`Task.Run` + one shared static `HttpClient`, 10 s
  timeout), so every call site is trivially hot-path-safe; POST body = message, headers
  `Title`/`Priority`/`Tags` per the ntfy API. **Swallows all exceptions** (fail-silent — an alert
  channel must never be able to hurt the trading path).
- **Self-rate-limit**, pure seam fixture-pinned: non-urgent posts min-interval 5 s and max 12/min
  (drop silently beyond); `priority = "urgent"` bypasses both. `Friend Shared Function
  ShouldSend(nowUtc, lastSentUtc, sentInWindow, isUrgent) As Boolean` — OrderCheck fixtures.

### Call sites (exactly these; each one line, wrapped in nothing — Post is already safe)

| Event | Site (locate by symbol) | Priority |
|---|---|---|
| App start | end of the startup sequence, after secrets load | default — `"OrderApp started — LIVE/TESTNET, breaker $X"` (doubles as the connectivity test AND tells you when the AWS box restarted) |
| Bridge significant disposition (`acted` / `rejected:`) | the item-H significant branch of the bridge's host-log emission | default — the disposition line text |
| Bridge auto-STOP (any `ForceStop` cause incl. breaker trip) | `ForceStop` | **urgent** — the cause |
| Emergency market-stop fired | the emergency branch's existing log line | **urgent** |
| Position closed (tracked) | the `CompletePositionClose` close-alert site (item D) | high — incl. P/L |
| Untracked/external close | item D's external-close branch | **urgent** |

**Accepted simplification (v1):** one master switch = `ntfy_url` present. No per-class remote
toggles (the alerts block keeps governing SOUND only); revisit only if the channel gets noisy.

## Q2 — signal columns in the trade DB

**Why:** per-signal P/L attribution becomes a query; joins the book to the engine matrix directly.

- **Migration** (the ergonomics-item-C pattern, fixtures and all): `SignalId INTEGER NULL`,
  `SignalConfidence TEXT NULL` (manual trades record NULL/empty). OrderCheck migration fixtures
  extended: old DB opens; legacy rows read defaults; enriched row round-trips; idempotent re-open.
- **Plumbing (the tag lifecycle — spec'd precisely, keep it exactly this):**
  1. Two host fields `pendingSignalId As Long = -1` / `pendingSignalConfidence As String = ""`,
     written by the bridge act path only (beside the existing `SetTradeTargets` call).
  2. Promoted to `currentTradeSignalId/…Confidence` at the ENTRY FILL (the position-entered
     branch), pending cleared there.
  3. Pending cleared in BOTH cancel teardowns (scoped + nuclear) — one field-pair reset beside the
     existing id-nulling, **touching nothing else** — so an aborted bridge entry can never tag a
     later manual trade.
  4. `CompletePositionClose`'s DB insert includes the current pair, then clears it.
- All four steps are plain engine fields on the paths that already own them — no new marshalling.

## Q3 — ops tools (no app code, tools/ only, one commit)

- `tools/backup-orderapp.ps1`: zips `orderapp-settings.json`, the trade DB, `bridge-state.json`
  and `bridge-dispositions.log` (x64 bin) to a dated archive in a target folder (param; S3 sync is
  the owner's scheduled-task wrapper — document the one-liner in the script header). UTF-8 BOM.
- `tools/policy-report.ps1`: over `bridge-dispositions.log` — per UTC session bucket × tier:
  counts of `would-act` / `acted` / `refused: policy(...)` (split tier/context) since a `-Since`
  date. Read-only; the soak-join row parser pattern.

## Q4 (OPTIONAL — only with headroom) — USE-ENGINE-LEVELS button

Main form near the manual Take Profit / Stop Loss boxes: writes the LATEST payload's
`RoundToTick(target)` → `txtManualTP` and `DeriveManualSl(direction, stop, StopLimitOffset)` →
`txtManualSL`; yellow refusal if the payload is stale/none or direction NONE. Needs a `Friend`
snapshot accessor on `SignalBridge` (immutable last-actionable-levels snapshot, reference-swapped
— the SessionPolicy pattern). UI-thread only. **Geometry: screenshot-verify** (the WordWrap
lesson); measure the caption single-line. If space is not clean, SKIP and report — this item is
explicitly droppable.

## Acceptance

1. Gate per commit; new fixtures counted (rate-limit seam + migration extensions).
2. **Q1 live check (owner):** set `ntfy_url`, start the app → phone notification "OrderApp
   started…". Then the isolated-harness payload protocol (testnet bin, own `bridge.json` — the
   soak-era standard, engine untouched): one acted → notification; an auto-STOP → urgent
   notification. Remove `ntfy_url` → startup logs `disabled`, zero posts, behaviour otherwise
   byte-identical.
3. **Q2 (owner, next live/testnet trade):** bridge trade records its SignalId/Confidence in the
   Results grid/DB; a manual trade records NULL; slippage-aborted bridge entry followed by a
   manual trade → the manual trade is NOT tagged (the step-3 clear). Legacy DB rows intact.
4. Q3: both scripts run against the real files read-only; backup archive opens.
5. Greps in the report: no `Await` on any notifier call site; the Q2 tag-clears sit beside the
   existing teardown resets and touch nothing else; SL/commanded tripwires unchanged.

## Commits

1. `Remote notifier: ntfy fire-and-forget alerts (+ rate-limit fixtures)` (Q1)
2. `Trade DB: SignalId/SignalConfidence columns + tag lifecycle (+ migration fixtures)` (Q2)
3. `Tools: backup-orderapp + policy-report scripts` (Q3)
4. `USE-ENGINE-LEVELS button` (Q4, only if taken)
5. `Docs: impl report - quick wins` → `docs/impl-report-quickwins.md`, standard format.
