# Implementation report — Session policy gate

**Spec:** `docs/spec-session-policy-gate.md` (including the 2026-07-21 §4 unity-passthrough
correction, `300dd01`, which came out of the pre-commit anchor pass below).
**Implementer seat:** Opus high, one conversation, 2026-07-21.
**Base:** `7572bbe` (master HEAD at kick-off; `origin/master` was `b77b724`, i.e. the owner's push of
the preceding stack had not happened yet — nothing here is pushed).
**Commits:** `0afe06d` → `893d3a0` → `d32674e` (+ this report).
**Gate:** `GATE PASSED` executed per commit. OrderCheck **48 → 96 fixtures**, all passing.

---

## 0. Pre-commit anchor pass (and the one finding that changed the spec)

Every §-anchor was verified against HEAD before any edit. All matched: the §4.4 `ElseIf` chain ended
at `SignalBridge.vb:628` with §4.5 opening at `:631` (the 4.4b slot exactly as specced);
`PlaceAutomatedOrder` had exactly one caller; `InitialiseSettings` really does commit before wiring;
`GroupBox1` really was Designer-only.

**The finding.** §4's original formula was unconditional:

```
effective = Math.Max(10D, Math.Floor(SizeUsd * mult / 10D) * 10D)
```

With `mult = 1.0` and a non-step Amount (25), this yields 20, and the `effective <> SizeUsd` guard
therefore *fires*. Consequences: §10 acceptance 2 ("enabled with a blank box ⇒ byte-identical
disposition lines") is unsatisfiable by construction — the Log-only `would-act` line would read
`size 20` where HEAD reads `size 25` — and §1's "absent config = today's behaviour exactly" breaks.
In Live it would have quietly cut a 25 order to 20 while configured as a pure *filter*.

Raised before writing code; owner took it to the engine orchestrator; ruling was unity-passthrough
and **the spec was amended in place** (`300dd01`), so this ships **as written, not as a deviation**.

**§9.5 resolved** (the spec asked the implementer to determine this): placement does **not** enforce
the 10-USD step upstream. `ExecuteOrderAsync` only requires `amount > 0`
([frmMainPageV2.vb:3195](../DeribitOrderPlacementApp/frmMainPageV2.vb)); the step floor lives solely
in `ApplyRiskBasedSize` (the SIZE button). Non-step amounts are therefore reachable by typing —
which is precisely what makes the unity-passthrough correction load-bearing rather than theoretical.
Pinned by fixture `25 x 1.0 -> 25`, with `25 x 0.5 -> 10` pinning that reductions still floor.

---

## 1. §1–§2 — config model, parse/render, session buckets (`0afe06d`)

**New file `DeribitOrderPlacementApp/SessionPolicy.vb`** (388 lines).

- `SessionPolicyRule` — immutable; `Tiers` (never empty), `Contexts` (empty = "any"), `SizeMult`.
  `AllowsTier` canonicalises both sides; `AllowsContext` is fail-closed (empty payload context vs a
  non-"any" set fails). `CanonicalTier` accepts `STRONG`→`HIGH` and `WEAK`→`LOW` on input and stores
  canonical. The `verdict` string is never parsed — it is contract-informational free text.
- `SessionPolicyConfig` — immutable; `Enabled`, per-session rules, `RuleFor` (absent key ⇒ §1
  defaults), `HasRuleFor`, `WithEnabled` (returns a new instance).
- `ParseSessionPolicyText(text, ByRef problem)` / `RenderSessionPolicyText(config)` — pure `Friend`
  seams. Parse returns `Nothing` + the offending line on any malformed input; blank box ⇒ all
  defaults; duplicate session line ⇒ rejected (silently taking the last would put a rule in force
  that isn't visible at the top of the box). Numbers parse **invariant**, never current-culture,
  because the same text round-trips through the json — the D1 culture lesson applied pre-emptively.
- `FromJson` / `ToJson` — tolerant load (missing/garbage ⇒ defaults + disabled; an unusable single
  rule drops to that session's defaults without disturbing its neighbours).

**`SignalBridge`** gains `SessionBucketFor(utcHour)` (ASIA 00–07 · LONDON 08–12 · NY 13–23) and
`SessionBucketForPayload(generatedUtc)`, which converts defensively before reading `.Hour`. Both
carry a comment warning that these are UTC *analysis* sessions and deliberately **not** the UTC+8
Inclusion Time Range.

**`AppUserSettings`** gains `Friend SessionPolicy` loaded from / saved to the `session_policy` block
on item A's existing path (D2 — no new save path). `orderapp-settings.example.json` documents the
full shape.

### Deviation (§1, minor): where `enabled` lives

The spec sketched `SessionPolicyEnabled As Boolean` plus a rule per session key on `AppUserSettings`.
Implemented instead as a single `SessionPolicyConfig` holding both, because that is exactly what
§5.4's `SessionPolicy` accessor and §3's snapshot want to hand around; parallel fields would have to
be recombined at all three read sites. Same data, same json, one object.

### Deviation (§1, minor): `ToJson` writes only configured sessions

Mirrors `RenderSessionPolicyText`. Materialising unconfigured sessions as explicit defaults would
mean the box silently grew two lines the owner never typed, the first time they saved.

### The bug the new fixtures caught immediately

First draft used ReadOnly **auto-properties** assigned from same-named constructor parameters.
**VB is case-insensitive**, so `sizeMult` shadows `SizeMult` and `SizeMult = sizeMult` is a
self-assignment of the parameter. Every multiplier read back `0.0` and `Enabled` read `False` — 9
fixtures failed on the first gate run. Fixed with underscore backing fields (the app's existing
convention); the trap is commented at both classes so it does not come back.

---

## 2. §3–§4 — the 4.4b gate and size_mult (`893d3a0`)

**Gate placement:** a new 4.4b block after the entire contract-§4.4 `ElseIf` chain, before the §4.5
interlock, exactly as specced. Every payload the contract itself refuses keeps its exact contract
token, so soak/join semantics survive enablement; only engine-actionable signals can be
policy-refused.

**`PolicyRefusalFor(policy, session, confidence, verdictContext)`** — the whole decision as one pure
seam, so semantics are fixture-pinned rather than buried in the chain. Returns `Nothing` whenever the
policy is disabled, which makes disabled-parity true *by construction*. Tier is checked before
context so the token names the first reason, matching the chain's first-failing-gate convention.

**Live-read:** new `Private ReadOnly Property SessionPolicy` on `SignalBridge` reads the settings
form's snapshot — one reference read of an immutable object the form reference-swaps. The `TiersCsv`
precedent extended to a compound value; safe from the FSW/timer/processing threads.

**`EffectiveSizeUsd(raw, mult)`** — unity passes through untouched; the 10-USD step floor and the D3
clamp apply only to a real reduction. `EffectiveSizeWasClamped` drives one yellow line per placement
(D3: clamp, never refuse). Applied **exactly once**, at the act/would-act site; every gate above it
including `refused: size` still reads the raw `SizeUsd`. Log-only's `would-act` line carries the
effective size so the soak records the counterfactual.

**`PlaceAutomatedOrder`** and **`ExecuteOrderAsync`** each gain `Optional sizeUsdOverride As
Decimal = 0D`. `0` ⇒ read the Amount box, so all six manual button call sites are byte-identical;
only the bridge act path passes a value, and only when the mult actually changes the size. The
Amount box is still validated first (an override must not paper over an empty box), and `txtAmount`
is never written.

---

## 3. §5 — settings UI and the D5 relocation (`d32674e`)

**D5:** `GroupBox1` deleted; `txtStartTime`/`txtEndTime`/`Label7`/`Label8` moved into `grpTradeGates`
as its third row. Names, handlers, mirrors and harness `AccessibleName`s are untouched by
construction — only parent and coordinates changed. `GroupBox1`'s window tooltip is migrated verbatim
onto **both** time boxes (they carried none). Group retitled
`"Trade Gates && Inclusion Time Range"` — the doubled ampersand is required; a lone `&` is the
WinForms mnemonic prefix and would be swallowed, underlining the "I".

**New controls:** `chkSessionPolicyOn` (caption *is* the switch, `chkBridgeArm`'s 12F style) and the
multiline `txtSessionPolicy`. Tooltips per §5 — the policy tooltip carries the grammar, an example
line, the sessions, the defaults, the `(0,1]` range and the json key; the checkbox tooltip states
that OFF is today's behaviour and that it arms nothing.

**Geometry** at the pinned 512×856, verified arithmetically:

| Group | Location | Size | Internals reach | Slack |
|---|---|---|---|---|
| `grpTradeGates` | (18, 12) | 482×176 | right 478, bottom 167 | 4 / 9 |
| `grpSignalBridge` | (18, 200) | 482×344 | right 471, bottom 330 | 11 / 14 |
| `grpTooling` | (18, 556) | 482×270 | unchanged | — |

Uniform 12px gutters; bottom 826 of 856.

### Deviation (§5 geometry, permitted): `txtSessionPolicy` width 320 not 330

330 puts the right edge at 480, 2px from the 482 group width — tighter than anything else on the
form. 320 lands at 471, matching `lblBridgeStatus`'s proven edge; three lines of
`LONDON = MEDIUM | CONFIRMED | 0.5` at 10F need barely 270. §5 explicitly permits fine-tuning.

### Deviation (§5.2, reported as the spec requested): individual wiring

`txtSessionPolicy` is wired individually, not through the shared loop. The spec anticipated dropping
only `CommitOnEnterKey`; **`SelectAllOnEnter` had to go too**. It is bound to `.Click` as well as
`.Enter`, so in a multiline box every click would reselect all three lines and the next keystroke
would wipe the whole policy — you could never edit one line. The box keeps commit-on-blur (the
standing model, invariant 10) and the harness `AccessibleName`.

`chkSessionPolicyOn` is wired with `AddHandler` rather than `Handles`, so the seed cannot fire the
commit before the form is initialised.

**Plumbing:** `SeedSessionPolicyFromHost()` runs beside `SeedRiskSizingFromHost()`, FIRST — the same
trap, same fix. `CommitGateConfig` parses, reference-swaps the snapshot and pushes to the host; a
malformed line keeps the last good **rules** but still honours the checkbox, so the policy can always
be switched off mid-edit. The offending line surfaces through the existing orange
`ShowGateConfigWarnings` path. Host gains `SessionPolicy` / `SetSessionPolicy` beside the
risk-sizing pair.

---

## 4. Build results

| Commit | Gate | OrderCheck |
|---|---|---|
| `0afe06d` | GATE PASSED | 76/76 (was 48) |
| `893d3a0` | GATE PASSED | 96/96 |
| `d32674e` | GATE PASSED | 96/96 |

48 new fixtures: 6 bucket boundaries, 3 bucket-via-`ParsePayload` (incl. the 23:30Z DateTimeKind
trap, new fixture file `late-utc-hour.json`), 17 parse/render/persistence, 10 gate evaluation, 8
effective-size, 2 token classification, plus the case-insensitivity and fail-closed assertions.

---

## 5. §7 must-NOT-change — grep evidence

- **`PlaceAutomatedOrder(`** — 2 hits: the definition and the single bridge act site, which is the
  only caller passing the new argument.
- **`ExecuteOrderAsync(`** — 8 hits: definition, the `PlaceAutomatedOrder` pass-through, and the 6
  manual button sites, all unchanged 1-argument calls.
- **`txtAmount.Text = `** — 4 writers, all pre-existing (Designer default, settings load,
  `ApplyRiskBasedSize`, the `sizeUSD` restore at :583). None added.
- **SL invariants untouched:** `RecordCommandedSLPrice` 3 → 3 and the **7** paired
  `emergencyBaseline = 0` / `ResetCommandedSLPrices()` reset sites, both identical to `7572bbe`.
- **`frmMainPageV2.vb` diff is 4 hunks total**, all session-policy: two signatures, one
  pass-through, one `If sizeUsdOverride > 0D` line, plus the host accessors. Nothing near the
  receive path, SL paths or commanded-set sites.
- Contract §4 chain order, every existing token and the disposition line format are unchanged; the
  global Tiers box, `TiersCsv` and `refused: tier` are untouched (D1 intersection); `refused: size`
  still reads raw `SizeUsd`; Mode/ARM/Started still never persist.

**Method note for the reviewer:** a naive `grep -c "emergencyBaseline = 0"` returns 11, not the 7
that HANDOVER-3 invariant 1 states. That is a *measurement* artefact, not drift — the invariant is
about reset sites *paired with* `ResetCommandedSLPrices()`, and the paired count is exactly 7. Worth
knowing before the number causes a false alarm in a future review.

---

## 6. Acceptance status

| # | Item | Status |
|---|---|---|
| 1 | Gate per commit, new fixtures counted | **DONE** — 3/3 GATE PASSED, 96/96 |
| 2 | Disabled parity (soak-safety proof) | **Argued + fixture-backed, owner runtime pass outstanding.** `PolicyRefusalFor` returns `Nothing` when disabled and `EffectiveSizeUsd` is the identity at unity, both pinned; nothing populates a non-default policy unless the owner ticks the box. Needs the live-stream byte-comparison. |
| 3 | Config round-trip (the §5 trap test) | **Mechanism fixture-pinned** (json → rules → rendered text); the hand-edit + restart run is the owner's. |
| 4–6 | Tier refusal / context refusal / size_mult | Runtime, engine-stopped write-payload protocol — owner. |
| 7 | UI not clipped/overlapping | **Verified on paper** (table above); not verified on screen. |
| 8 | Persist round-trip | Runtime — owner. |
| 9 | Greps | **DONE** — §5 above. |

Acceptances 2–8 need a runtime pass; nothing here is "done" until the owner runs it. I did not launch
the app: the owner trades through it daily and HANDOVER-3 §6 asks that session-disruptive work be
coordinated. Happy to drive the harness (testnet profile, engine-stopped payload protocol) on request.

---

## 7. Suspicious nearby, not touched

- **`ShowGateConfigWarnings` is clobbered by `RefreshBridgePanel`.** Both write `lblBridgeStatus`,
  and `CommitOnEnterKey` calls the commit *then* the refresh — so a warning raised by pressing Enter
  is overwritten immediately. Pre-existing (not introduced here); the policy's warning inherits it.
  `OnSessionPolicyToggled` deliberately does *not* call `RefreshBridgePanel`, so the checkbox path is
  unaffected. Worth a housekeeping item.
- **`orderapp-settings.json` is written non-atomically** — `File.WriteAllText`
  ([AppUserSettings.vb:161](../DeribitOrderPlacementApp/AppUserSettings.vb)) — where
  `bridge-state.json` writes a `.tmp` then `File.Move(overwrite:=True)` (the F-2 discipline,
  [SignalBridge.vb:362](../DeribitOrderPlacementApp/SignalBridge.vb)). A crash mid-save could
  truncate the standing inputs. Pre-existing and out of scope, but flagged because the policy block
  now rides that path too.
- **`FromJson` silently drops a rule with an out-of-range `size_mult`** (clamps to 1.0) where the
  text parser *rejects* the line. Deliberate — a hand-edited file should not disable the whole policy
  — but the asymmetry is worth knowing.
- The `AutoTradeSettings` TabIndex sequence now has a gap (groups 2/3/4, no 1) since `GroupBox1`
  went. Harmless — relative order is what matters — and left alone to keep the Designer diff minimal.
