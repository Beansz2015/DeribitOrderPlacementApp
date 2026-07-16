# Spec-back — Signal-bridge log-only bring-up (2026-07-16)

**Why this doc:** the owner started the real log-only soak, and bring-up surfaced **two post-approval defects** in the bridge (both in coordinator-APPROVED code) plus a run of runtime test passes. This captures that session so a fresh seat or the coordinator can resume without the chat. It is a companion to — not a replacement for — `spec-back-autotrade-retirement.md` (the retirement code change) and `impl-report-autotrade-tiein.md` (the tie-in itself, whose top now carries the culture-bug write-up).

**Push-state:** `origin/master = afca0c2` — **nothing pushed.** This session's commits are local at `5b629b3..c7cc8a4`; tree clean; **Debug + Release both 0/0.** (The local branch also carries a separately-owned UI-test-harness stack ahead of this work — see memory `ui-test-harness-implemented`; not part of this spec-back.)

---

## 1. Two defects found at bring-up (this is the important part)

Both were in code the coordinator APPROVED at `bbebaa7`, and both are the kind that pass every desk review because they only misbehave against a live engine and/or a non-US locale. They are why "reviewed + builds 0/0" is not "done" until the owner runs it.

### D1 — culture bug: every payload read as `stale` (`8956baa`)

- **Symptom:** engine emitting every 30 s, bridge seeing the files, **every one disposed `stale`**, stand-down alert looping.
- **Mechanism:** Newtonsoft's default `DateParseHandling.DateTime` silently converts an ISO-8601-*looking* **string** into a **Date token**; `JToken.ToString()` then renders that token **in the current culture**. On en-MY (`d/M/yyyy`), `"2026-07-15T16:50:08Z"` came back out of `.ToString()` as `"15/7/2026 4:50:08 PM"`; the `InvariantCulture` (`M/d/yyyy`) `TryParse` rejected month `15`; the code fell to `DateTime.MinValue` = maximally stale. **Freshness gate (contract §4.2) could never pass → the bridge could never act.**
- **Culture-dependent, which is why review + mock tests missed it:** en-US works; **en-GB / de-DE / en-MY are dead.** A reviewer on a US box sees correct code. The engine is blameless — contract §3 pins ISO-8601+`Z`+invariant, it emits exactly that; the consumer un-did it on read.
- **Fix:** `ParsePayloadJson` loads with `DateParseHandling.None`, so tokens stay the contract's raw strings and the explicit invariant parse gets the ISO text it was written for. Also stops any other field being silently re-typed as a date.
- **Hardening:** an unparseable `generated_at_utc` still stands down but now logs **RED** — *"malformed payload, not a dead engine"* — quoting the raw value. The silent `MinValue → stale` is exactly what disguised this for an evening.
- **Verified** by driving the SHIPPED parse path via reflection against the live payload under en-MY: `TimestampOk=True`, `Kind=Utc`, age 0.93 min, `FRESH=True`.
- **Scope:** confined to `generated_at_utc`. Every other `JObject.Parse` reads Deribit JSON-RPC (epoch-ms **numbers**); `bridge.json`/`bridge-state.json`/`secrets.json` hold only strings/numbers/GUID.
- **Coordinator item:** the engine has fixture **A22** (invariant-culture *emit* under de-DE). The consumer has **no** day-first-culture *parse* fixture — that asymmetry is the exact hole. Add one before the soak freezes the test set.

### D2 — ATR source: guard ran on an ageing / frozen ATR (`bbec1e2`)

Owner-found via the new Tooling `ATR now:` readout. Two bugs + one cross-app hazard.

1. **Held an ageing ATR.** `_lastSignalAtr` refreshed only on `direction <> NONE`, so a lone WEAK signal's atr was held **indefinitely** through the `NO TRADE` stretches that are most of a session, while fresher values streamed past every run. Live: **34.91 held from WEAK SHORT #33 while payload #41 said 24.78** → slippage limit $20.95 vs a volatility-correct $14.87, **~41 % too loose** — and that guard governs **manual** entries too.
   **Ruling (owner):** refresh from **every fresh `signal_state=OK` payload with `atr > 0`, NO TRADE included.** `atr` is an execution-resolution market measurement, not a trade decision. The contract's *"guaranteed non-zero when `direction <> NONE`"* is a **guarantee**, not a claim other payloads' atr is junk. **Deviation from spec §1's "last actionable payload's atr" — owner-ruled, logged.**
2. **Mode Off froze it forever.** `StopWatching` stops the staleness timer that would zero the ATR but never zeroed it itself → the guard stayed pinned to the last engine ATR with nothing able to clear it. Now zeroed in `StopWatching` (Off + Dispose). Also zeroed on fresh **SKIPPED** (contract §4.2 "never hold the last signal").
3. **⚠ Cross-app hazard:** **engine ATR period = 7, order app = 14** — NOT the same measurement, so the limit *steps* when the source flips, and "bridge off / stale / skipped" **must** revert to the app's own 14. That is the owner's stated requirement and the reason bug 2 mattered. If the two periods should agree, that is an engine-side `indicators.ATR` decision → owner; **never write cross-repo.**
- **Verified:** all five transitions against the SHIPPED assembly by reflection (OK/direction → adopt; OK/NO TRADE → adopts newest; SKIPPED → 0; stale → 0; `StopWatching` → 0). Bugs 1 and 2 additionally **runtime-confirmed** by the owner (see §3 tests 9.2 / 9.2a).

---

## 2. Commit map (this session, on top of the reviewed `bbebaa7`)

| Commit | What |
|---|---|
| `5b629b3` | F-1 payload identity/levels guard + F-2 atomic state write (the review's should-fix) |
| `df615b7` | Retirement A — retire FrmIndicators UI, keep it headless as the ATR engine |
| `cdc7ce4` | Retirement B — main-form `btnAutoSettings` opener + re-parent settings form |
| `1e314aa` | Retirement C+D — fold gate config into the old controls, add Tooling, bridge rewiring |
| `c622212` | Retirement C layout fixup (from rendering the form) |
| `895b73a` | Owner-run fixups — `Auto Settings` caption width + live `ATR now:` readout |
| `8956baa` | **D1 culture fix** |
| `bbec1e2` | **D2 ATR-source fix** |
| + docs | `d597561`, `c28073d`, `1f3fa5d`, `8582f2a`, `ca655c3`, `d4a16e8`, `10f5819`, `7f2eb2d`, `c7cc8a4` |

New disposition token added this session beyond the reviewed set: **`refused: size`** (order size now comes from the main form's Amount box; an empty/zero box is refused before it reaches the exchange). Full token set lives in `impl-report-autotrade-tiein.md` §(d).

---

## 3. Runtime test status (spec-back-autotrade-retirement §9 + tie-in §6)

| Test | State | Evidence |
|---|---|---|
| §9.1 headless ATR alive | ✅ PASS | `ATR now:` reads a live value — only possible if the whole headless chain works (StartHeadless→ConnectAndStream→marshal→UpdateATR reads host AtrLength). Retires risk §7.1. |
| §9.2 Tooling knobs (length moves it; log-only shows engine ATR regardless of length) | ✅ PASS | Both halves. The log-only half **is D2 bug-1's live confirmation** — engine emits only NO TRADE, and pre-`bbec1e2` those never refreshed the ATR. |
| §9.2a ATR hands back on mode Off | ✅ PASS | green→cyan on Off, at the `txtAtrLength` period. **D2 bug-2 live confirmation** + proves the ATR-length repoint is live. |
| §9.3 commit-on-blur | ✅ PASS | Typed ATR Length without leaving field → readout unchanged; tabbed away → moved in ~1 s. **Closes the §4 half-typed-value concern.** Generalises to all 7 boxes (one `InitialiseSettings` wiring loop → one `CommitOnLeave`). |
| Live emitter parse / freshness / gate-order | ✅ (implicit) | Real #16–#41 dispositions; the stale `WEAK LONG` reading `stale` not `refused: tier` proves 4.2 precedes 4.4. |
| `refused: tier` (WEAK carries direction) | ✅ | #33 `WEAK SHORT (LOW/SHORT) → refused: tier`, matched the analysis app. |
| `refused: direction` (NO TRADE incl. lean-in-verdict, D2 contract rule) | ✅ | #16–#41, incl. `NO TRADE [WEAK LONG] (…/NONE)`. |
| §9.4 fail-closed window | ⬜ OPEN | needs an actionable payload (see §4). |
| §9.5 `refused: size` | ⬜ OPEN | needs an actionable payload (see §4). |
| §9.7 manual-trading regression | ⬜ OPEN | no payload needed. |
| §6.2 interlock ladder / §6.3 min-size placement | ⛔ LIVE-GATED | live mode only; min-size also waits on the engine placed-geometry pass (contract §7). |

**Passed so far runtime-confirm:** the headless premise, both D2 bugs, the ATR-length repoint, the bridge-first ATR rule, and commit-on-blur. **Blocking push: §9.4, §9.5, §9.7** (+ coordinator re-review of this session's deltas).

---

## 4. Remaining-test procedure (hand it to the owner as-is)

**Why hand-crafted:** the live engine emits only `NO TRADE`, refused at gate 4.4 *before* the 4.6 window/size gates run — so those gates are unreachable without an **actionable** (HIGH/MEDIUM, real direction) payload. And a running engine overwrites the file every run interval.

**Setup**
1. **Stop the engine** (`signal_bridge.enabled: false` beside its exe + restart, or close it) so it stops overwriting.
2. Save the template at `C:\Dev\DeribitBridge\test-actionable.json`:
   ```json
   {
     "schema_version": 1, "signal_id": 1001, "generated_at_utc": "REPLACED_BY_SCRIPT",
     "engine": { "instance_id": "manual-test-01", "autotrade_armed": false, "app": "DeribitVerdictEngine" },
     "instrument": "BTC-PERPETUAL", "signal_state": "OK",
     "verdict": "STRONG LONG", "confidence": "HIGH", "direction": "LONG",
     "verdict_context": "CONFIRMED", "mtf_blocked": false, "exec_resolution_min": 1, "atr": 40.0,
     "levels": { "long": { "entry": 65000, "stop": 64950, "target": 65100 } },
     "health": { "ws": "OK", "ledger_mismatch": false }
   }
   ```
3. Save the **drop helper** as `C:\Dev\DeribitBridge\drop.ps1` (edit the template, run this to stamp a fresh timestamp — freshness window is 2.5 × `exec_resolution_min` = **2.5 min** — and copy it onto the watched path):
   ```powershell
   $src='C:\Dev\DeribitBridge\test-actionable.json'
   $dst='C:\Dev\DeribitBridge\verdict_signal.json'
   $j = (Get-Content $src -Raw) -replace '"generated_at_utc": "[^"]*"', ('"generated_at_utc": "' + [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ') + '"')
   Set-Content $dst $j -Encoding utf8
   ```
   Run each drop with `& 'C:\Dev\DeribitBridge\drop.ps1'`. Order app stays in **Log-only**, Auto Settings open.

**Required (block push)**
- **§9.5 `refused: size`** — drop → `would-act`. Clear the main-form **Amount** box, click away. Bump `signal_id`→1002, drop → **`refused: size`**. Restore Amount.
- **§9.4 fail-closed window** — Start Time = garbage (`9x`), End blank, click away. Bump id, drop → **`refused: window`** (+ orange "both boxes must be set or both blank"). Then a valid window you're inside (`00:00`–`23:59`), bump id, drop → **`would-act`**. Then **both** blank, bump id, drop → **`would-act`** (unrestricted).
- **§9.7 regression** — no payload; in Off and in Log-only, place+cancel a small manual order → behaves exactly as before; **no FrmIndicators window ever appears.**

**Optional but valuable (coverage the live stream can't reach)** — same setup, bump `signal_id` each drop:
- `refused: levels` (F-1): set `"stop": 0`.
- `skipped`: `"signal_state": "SKIPPED"`.
- `refused: schema_version`: `"schema_version": 2` (+ red alert).
- **malformed timestamp**: after stamping, hand-edit `generated_at_utc` to `"not-a-date"` → the **red "MALFORMED PAYLOAD, not a dead engine"** line (this is D1's guard).
- `duplicate`: valid drop (→ `would-act`), then re-drop **without** bumping `signal_id` → `duplicate`.

**Do NOT try in Log-only:** `refused: cooloff` (cooloff anchors on position close; log-only opens none) and the interlock/live-placement tests (live mode only).

**When done testing:** flip the engine's `signal_bridge.enabled` back to `true` and restart it to resume real emission.

---

## 5. Open items for the coordinator re-review

1. This session's deltas post-date the `bbebaa7` review: the retirement stack (supersedes the reviewed panel — see `spec-back-autotrade-retirement.md` §6) + D1 + D2 + `refused: size` + the ATR-source **deviation** from spec §1.
2. **Missing consumer parse fixture** (day-first culture) mirroring the engine's A22 — D1 would have been caught pre-ship.
3. **Cross-app:** engine ATR period 7 vs app 14 — owner decision if they should agree; not this repo's to change.
4. `bridge.json` beside the running exe has been trimmed to `path` + `slippage_atr_mult` (the gate keys are dead post-retirement); the committed `bridge.example.json` already reflects this.
