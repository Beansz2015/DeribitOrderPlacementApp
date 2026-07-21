# Spec — Session policy gate (per-session tier/context subsets + size multiplier)

**Origin:** the engine orchestrator's proposal `C:\Dev\DeribitVerdictEngine\docs\session-policy-gate-proposal.md`
(engine-side APPROVED, P1–P5; **owner re-ticked all five for the order app 2026-07-21**, with the P2
note: the context/tier settings join the section where the Tiers box already lives). Driver: trader —
"MEDIUM seems more accurate than STRONG; I want the choice to trade specific subsets per session."
**Engine impact: ZERO** — all work is order-app side; the payload already carries everything
(`confidence`, `verdict_context`, `generated_at_utc`).

**Recommended implementer:** **fresh Fable high while the window lasts (~Aug 2), else Opus high** —
§3 sits in the bridge gate chain and §4 touches the placement call. One conversation, 4 commits.
**Target:** `SignalBridge.vb`, `AutoTradeSettings.vb` + `.Designer.vb`, `frmMainPageV2.vb`,
`AppUserSettings.vb`, `tools/OrderCheck`. **Base:** master HEAD (contains the raced-abort repair and
the risk-sizing settings UI — the seed-before-commit machinery this spec extends).
**Anchors by symbol; verify against HEAD.**
**Ground rules:** the standing ones — build 0/0 per commit via `tools/checks/verify-gate.ps1`
(**GATE PASSED required**), local commits, never push, receive-thread/bridge-thread rules (the gate
chain runs off the UI thread — mirrors are plain-field/reference reads, display via the existing
`_log`), scope discipline, impl report at the end.

**Soak safety (load-bearing):** disposition tokens are SOAK-FROZEN, but the new `refused: policy(...)`
token can only ever be emitted when the policy is ENABLED — and it ships **disabled by default**
(acceptance 2: disabled = byte-identical disposition stream). Landing this mid-soak is join-safe;
the owner enables it at the live-ladder step, post-soak-review (proposal P5), starting from the
conservative suggestion (NY unchanged · LONDON `MEDIUM`+`CONFIRMED` at 0.5× · ASIA 0.75×).

---

## §0 — D-table (consumer-side decisions; owner ticks before implementation)

| # | Decision | Recommendation |
|---|---|---|
| **D1** | Interaction with the existing global `Tiers:` box (`txtBridgeTiers`, contract §4.4 gate) | **KEEP BOTH — intersection semantics.** The global box stays the contract's tier gate, untouched, `refused: tier` unchanged; the policy is a SECOND, per-session filter evaluated after it. A signal must pass both. The policy can only ever *narrow* what the global box allows — which is exactly the proposal's "opt-in restriction". Nothing runtime-proven is retired, and the soak's gate-for-gate join semantics stay intact. |
| **D2** | Persistence | **YES, in `orderapp-settings.json`** as the proposal-§2-shaped `session_policy` block (nested object), loaded/saved by `AppUserSettings` on item A's existing save path — **no new save path**. A weeks-cadence standing policy that had to be retyped every start would guarantee drift. (Mode/ARM/Started still never persist — this block contains none of them; `enabled` persists like any other gate *config*, it arms nothing.) |
| **D3** | `size_mult` result below the 10-USD contract minimum (e.g. amount 10 × 0.75) | **Clamp UP to 10 and log** (`size_mult 0.75 clamped to contract min 10`), don't refuse. At live-at-min-size the Amount box IS 10 — refusing would silently kill every signal in a reduced-size session, the highest-surprise failure available. The mult becomes effective as soon as the amount is large enough to express it. |
| **D4** | Allowed `size_mult` range | **(0, 1] in v1.** Every P5 example reduces; a >1 multiplier is a size *increase* hiding in a filter config. Values outside the range fail the parse (warning, keep last good). Revisit only with matrix evidence. |
| **D5** | Where the vertical space comes from (the form is pinned 512×856) | **Merge the Inclusion Time Range row into the Trade Gates group** (§5). Pure Designer relocation — same boxes, same names, same handlers, same mirrors; frees 100px+gutter for the policy UI in SIGNAL BRIDGE, per the owner's P2 placement. |

## §1 — Config model and parsing

### Persisted shape (`orderapp-settings.json`, proposal §2 verbatim)

```json
"session_policy": {
  "enabled": false,
  "sessions": {
    "NY":     { "tiers": ["HIGH","MEDIUM"], "contexts": "any",         "size_mult": 1.0 },
    "LONDON": { "tiers": ["MEDIUM"],        "contexts": ["CONFIRMED"], "size_mult": 0.5 },
    "ASIA":   { "tiers": ["HIGH","MEDIUM"], "contexts": "any",         "size_mult": 0.75 }
  }
}
```

- **Tier tokens = the PINNED confidence enum** (`HIGH`/`MEDIUM`/`LOW`), same vocabulary as the
  existing Tiers box. The proposal's STRONG/MEDIUM/WEAK are the trader's verdict-tier names for the
  same 1:1 dimension (contract §3: `WEAK LONG` ⇒ `confidence:"LOW"`; STRONG verdicts carry HIGH) —
  **accept `STRONG`→`HIGH` and `WEAK`→`LOW` as parse aliases**, store/display canonical. Never parse
  the `verdict` string itself — it is contract-informational free text.
- `contexts`: `"any"` or a list of `verdict_context` tags, compared ordinal after `Trim().ToUpperInvariant()`
  — **opaque identifiers, no app-side vocabulary list** (see the contract addendum §6: the engine
  pins them stable). An empty/absent payload context vs a non-"any" set **fails the set** (restriction
  is fail-closed).
- **Defaults when a session key (or the whole block) is absent:** `tiers HIGH,MEDIUM · contexts any ·
  size_mult 1.0` — absent config = today's behaviour exactly.
- `AppUserSettings`: `SessionPolicyEnabled As Boolean` (default False) + one small
  `SessionPolicyRule` (Tiers list / Contexts list, empty = any / SizeMult) per session key. Load is
  tolerant (missing/garbage block ⇒ defaults + disabled); Save writes the block on item A's path.

### The settings-box text grammar (one line per session)

```
NY = HIGH,MEDIUM | any | 1.0
LONDON = MEDIUM | CONFIRMED | 0.5
ASIA = HIGH,MEDIUM | any | 0.75
```

`SESSION = tiersCsv | contextsCsv-or-any | mult`, case-insensitive, whitespace-tolerant. Omitted
session line ⇒ that session's defaults. Blank box ⇒ all defaults (harmless in either enabled state).
Parse failures (unknown session name, empty tier set after aliasing, mult outside (0,1], malformed
line) follow the settings-form convention: **keep the last good config, orange
"Ignored (keeping last good): session policy '<line>'"** via `ShowGateConfigWarnings()`.
Two pure Friend seams, fixture-pinned: `ParseSessionPolicyText(text) → config-or-Nothing(+problem)`
and `RenderSessionPolicyText(config) → String` (render(parse(x)) round-trips canonically).

## §2 — Session derivation

Pinned engine-identical buckets, from the **UTC hour of `generated_at_utc`** (the already-parsed
`GeneratedUtc` — `DateParseHandling.None`, culture-proven):

**ASIA 00:00–07:59 · LONDON 08:00–12:59 · NY 13:00–23:59 UTC.**

- Seam: `Friend Shared Function SessionBucketFor(utcHour As Integer) As String` in `SignalBridge` —
  fixture-pinned at all four boundaries (07:59/08:00, 12:59/13:00) plus 00:00 and 23:59.
- One fixture derives the bucket **through `ParsePayload`** from a payload string, so any
  `DateTimeKind` mishandling surfaces (implementer: verify `GeneratedUtc` is the UTC moment before
  taking `.Hour`; convert defensively if Kind ≠ Utc).
- `GeneratedUtc = MinValue` (malformed timestamp) can never reach this gate — §4.2 freshness refuses
  first. Note it, don't handle it.
- ⚠ Do NOT confuse with the Inclusion Time Range window: that is the owner's **UTC+8 local** hard
  window (gate 4.6, fail-closed); the session buckets are **UTC** analysis sessions. Different
  clocks, different jobs, deliberately independent.

## §3 — The gate (SignalBridge)

**Position: a new §4.4b block — after the whole contract-§4.4 `ElseIf` chain, before §4.5 interlock.**
Rationale: every payload the contract itself would refuse keeps its exact contract token (soak/join
semantics untouched even when enabled); only signals that clear all of §4.4 — including the GLOBAL
tier gate (D1) — can be policy-refused, so `refused: policy(...)` rows measure precisely "engine-
actionable, declined by my policy" (the P3 counterfactual cells, joinable on the session in the token).

```vb
' 4.4b session policy (docs/spec-session-policy-gate.md; consumer-side per D-table P1).
' Runs in Log-only too - post-enable, the soak stream shows exactly what the policy declines.
If disposition Is Nothing Then
    Dim pol = SessionPolicy              ' immutable snapshot reference off the settings form
    If pol IsNot Nothing AndAlso pol.Enabled Then
        Dim sess As String = SessionBucketFor(<UTC hour of p.GeneratedUtc>)
        Dim rule = pol.RuleFor(sess)     ' absent key => the §1 defaults
        If Not rule.Tiers.Contains(p.Confidence) Then
            disposition = $"refused: policy({sess}/tier)"
        ElseIf rule.Contexts.Count > 0 AndAlso
               Not rule.Contexts.Contains(p.VerdictContext.Trim().ToUpperInvariant()) Then
            disposition = $"refused: policy({sess}/context)"
        End If
    End If
End If
```

- **Live-read pattern = the `TiersCsv` precedent:** the settings form holds the committed config as
  ONE immutable snapshot object (`_sessionPolicy`, reference-swapped on commit; never mutated), and
  the bridge reads it through a `Friend ReadOnly Property` — a single reference read is safe from
  the bridge's FSW/timer threads, same discipline as the existing plain-field mirrors.
- Token classification is automatic and must be fixture-pinned: `IsSignificantDisposition` matches
  `acted`/`rejected` prefixes ⇒ `refused: policy(...)` is not-significant (item H filter: full
  stream while flat, quiet in-position); panel color falls to the default refused branch.
- The disposition FILE line format is unchanged — the token is just a new `refused:` value.

## §4 — size_mult application

- Effective size, computed at the act/would-act site only (gates before it, including `refused: size`,
  keep reading the raw `SizeUsd`): `mult = rule.SizeMult` (1.0 when disabled/absent);
  **unity passes through untouched** — `If mult = 1.0D Then effective = SizeUsd Else
  effective = Math.Max(10D, Math.Floor(SizeUsd * mult / 10D) * 10D)` — i.e. the step-floor and the
  D3 clamp apply only when an actual reduction is in play (a reduced non-step result like 12.5 is
  unplaceable and MUST floor; an untouched size must stay untouched, whatever it is — if it is
  non-step, that is today's behaviour and today's -32602, not the policy's business). The clamp
  logs one yellow line, once per placement.
  *(Corrected 2026-07-21 — implementer finding, pre-commit: the original unconditional formula
  floored non-step amounts even at mult 1.0, e.g. 25 → 20, contradicting acceptance 2's
  byte-identical claim and §1's "absent config = today's behaviour exactly". Unity-passthrough is
  the ruling; not a deviation.)*
- **Live:** `PlaceAutomatedOrder` gains `Optional sizeUsdOverride As Decimal = 0D` — `0` ⇒ existing
  behaviour (reads the Amount box), so **every existing call site is byte-identical**; only the
  bridge act path passes `effective` (and only when `effective <> SizeUsd`, keeping the common path
  untouched). Do NOT write `txtAmount` — the multiplier must never mutate the trader's standing input.
- **Log-only:** the `would-act:` line's existing `size {…}` field carries `effective` — same format,
  the value is the policy's counterfactual size. Applied exactly once; when the stop-distance
  formula later becomes the bridge's size source, this factor folds in as its `sessionFactor` term
  (ONE formula, no stacked hidden multipliers — proposal §2).

## §5 — Settings UI (AutoTradeSettings; the owner's P2 placement)

### D5 relocation (commit 3, Designer-only)

Move the Inclusion Time Range row into `grpTradeGates` and delete `GroupBox1` (verified 2026-07-21:
`GroupBox1` is referenced ONLY in the Designer). `txtStartTime`/`txtEndTime`/`Label7`/`Label8` keep
their names — the wiring array, `CommitGateConfig`, mirrors and harness `AccessibleName`s are all
untouched by construction. **Migrate `GroupBox1`'s tooltip** (the UTC+8 / blank-blank / midnight-wrap
text) verbatim onto BOTH `txtStartTime` and `txtEndTime` (they carry none today — verified). Retitle
the merged group `"Trade Gates & Inclusion Time Range"`.

### Geometry (ClientSize stays **512×856**; 18px margin / 482 width / 12px gutters; captions one line)

| Group | Location | Size | Internals |
|---|---|---|---|
| `grpTradeGates` | (18, 12) | 482×**176** | breaker/cooloff rows unchanged (y28/74); time row appended: `Label8` (11,127), `txtStartTime` (150,125), `Label7` (264,127), `txtEndTime` (396,125) |
| `grpSignalBridge` | (18, 200) | 482×**344** | existing internals unchanged; NEW: `chkSessionPolicyOn` (11,262), text `"Policy"`, 12F (the ARM style) — the caption IS the enable switch; `txtSessionPolicy` (150,254) 330×76, **Multiline**, Calibri 10F, black/white box style |
| `grpTooling` | (18, 556) | 482×270 | unchanged internals |

Bottom = 826 of 856 ✓. Implementer may fine-tune within the constraints; **measure every caption
single-line** (the wrap-clip lesson).

Tooltips: `chkSessionPolicyOn` — what the policy does, that OFF = today's behaviour, that it gates
nothing to Live by itself (Mode/ARM/START unchanged); `txtSessionPolicy` — the §1 grammar with one
example line, the defaults, and the json key.

### Plumbing — the seed-before-commit trap AGAIN (the risk-sizing §2 pattern, verbatim)

`InitialiseSettings` commits before wiring, so the policy box/checkbox MUST be seeded from the host
first or the first commit overwrites the loaded file values with blanks:

1. Extend the existing seed step (beside `SeedRiskSizingFromHost()`, same position — FIRST):
   `SeedSessionPolicyFromHost()` writes `RenderSessionPolicyText(_host.SessionPolicy)` into the box
   and `Enabled` into the checkbox. Same Load-ordering dependency (`userSettings` loads before the
   form is constructed) — already stated in comments both sides; extend them.
2. `txtSessionPolicy` joins the wiring array **for Leave/select-all/AccessibleName only — NOT the
   commit-on-Enter handler** (Enter must insert a newline in a multiline box). If the shared loop
   can't express that cleanly, wire this box individually and say so in the report.
3. `CommitGateConfig()` extended: parse per §1; success ⇒ swap `_sessionPolicy` + push to host
   (`_host.SetSessionPolicy(config)` → `userSettings`); failure ⇒ keep last good + warning (§1).
   `chkSessionPolicyOn.CheckedChanged` → the same commit.
4. Host accessors beside `RiskPerTradeUsd`/`SetRiskSizingValues`: `Friend ReadOnly Property
   SessionPolicy` (from `userSettings`, defaults when Nothing) + `Friend Sub SetSessionPolicy(...)`.
   Persistence rides item A — **no new save path**.

## §6 — Contract addendum (already applied by the coordinator — read it, honor it)

`docs/integration-contract-verdictengine.md`, addendum 2026-07-21: P1 decision-of-record
(consumer-side; engine-side per-session thresholds rejected for the subset use-case); the
`refused: policy(<session>/<tier|context>)` disposition class registered as a §4-EXTERNAL trader-
policy gate; `verdict_context` values pinned as **stable identifiers** (compared opaquely; renames =
coordinated change) while remaining non-semantic except `BELOW_MIN_MOVE`; session buckets pinned;
`size_mult` = consumer-side sizing config per §5. R1 is untouched: this is execution policy (tier
selection grown up), never verdict re-gating.

## §7 — Must NOT change

- Contract §4 chain order, every existing token, and the disposition file line format. The GLOBAL
  Tiers box, its `TiersCsv` mirror and `refused: tier` (D1: both gates, intersection).
- `refused: size` still reads the raw `SizeUsd` (the mult applies after all gates).
- Mode/ARM/Started never persist; the interlock (§6) untouched; log-only still advances
  watermark+cooloff exactly as today.
- All existing `PlaceAutomatedOrder` call sites (the Optional default keeps them byte-identical).
- `txtAmount` is never written by the bridge or the mult.
- The receive path, SL paths, commanded-set count, reset-site counts — nothing here goes near them
  (grep-assert in the report anyway).

## §8 — Harness impact

- `txtSessionPolicy` gets `AccessibleName` via the wiring (or individually — §5.2); multiline
  `set-textbox` value writes carry `\r\n` — verify once with `inspect-tree`/`set-textbox` on testnet.
- No new substring-shadowing pair (`txtSessionPolicy` collides with nothing; `chkSessionPolicyOn` is
  a CheckBox). `trade-buttons.txt` unchanged (nothing here places an order; the checkbox is config,
  not an arm — same class as the Tiers box, and it cannot fire while mode is Off regardless).
- §10's payload tests use the standing engine-stopped write-payload/restore-payload protocol.

## §9 — OrderCheck fixtures (gate grows; all pure seams)

1. `SessionBucketFor`: 00:00→ASIA · 07:59→ASIA · 08:00→LONDON · 12:59→LONDON · 13:00→NY · 23:59→NY.
2. Bucket **via ParsePayload** from a payload string (the DateTimeKind trap).
3. Parse: the 3-line example round-trips; `STRONG`/`WEAK` aliases canonicalize; omitted session ⇒
   defaults; blank ⇒ defaults; `mult 1.5` ⇒ invalid; unknown session name ⇒ invalid; garbage line ⇒
   invalid (each invalid case reports the offending line).
4. Evaluation: LONDON `MEDIUM|CONFIRMED|0.5` refuses (HIGH, CONFIRMED) as `policy(LONDON/tier)`;
   refuses (MEDIUM, "") as `policy(LONDON/context)`; passes (MEDIUM, CONFIRMED); disabled passes
   everything; absent-session key passes HIGH/MEDIUM.
5. Effective size: 20×0.5→10 · 10×0.75→10 (clamp) · 30×0.5→10 (floor) · **25×1.0→25 (unity
   passthrough — pinned per the §4 correction; non-step amounts are reachable and unity must be
   size-neutral)** · 25×0.5→10 (reduction of a non-step amount floors).
6. `IsSignificantDisposition("refused: policy(NY/tier)")` = False.

## §10 — Acceptance

1. **Gate:** `verify-gate.ps1` GATE PASSED per commit, new fixtures counted in.
2. **Disabled parity (the soak-safety proof):** policy disabled (and separately: enabled with a
   blank box) against the live engine stream in Log-only ⇒ disposition lines byte-identical to
   HEAD-before-this-spec over the same payloads. No new token appears anywhere.
3. **Config round-trip (the §5 trap test):** hand-edit the json block (LONDON `MEDIUM|CONFIRMED|0.5`,
   enabled true) → restart → checkbox ON, box shows the rendered lines — not blank, not defaults —
   and the gate uses them.
4. **Tier refusal:** engine-stopped, write-payload an actionable MEDIUM-confidence payload stamped
   inside the current UTC bucket, policy excluding MEDIUM there ⇒ `refused: policy(<sess>/tier)` in
   log + file; re-admit MEDIUM ⇒ `would-act`.
5. **Context refusal:** same payload, contexts `CONFIRMED` vs a payload context of `""` ⇒
   `refused: policy(<sess>/context)`.
6. **size_mult:** Amount 20, mult 0.5 ⇒ `would-act … size 10`; Amount 10, mult 0.75 ⇒ size 10 + the
   clamp line.
7. **UI:** nothing clipped/overlapping at 512×856; merged Trade Gates group correct; window tooltip
   present on both time boxes; policy tooltips per §5; captions single-line.
8. **Persist:** edit the policy in the box, tab away, close/reopen ⇒ intact and in force.
9. **Greps:** `PlaceAutomatedOrder(` call sites — only the bridge act site passes the new argument;
   no new SL-context/commanded-price sites; `txtAmount` writers unchanged.

## Commits

1. `Session policy: config model, parse/render seams, session buckets (+ fixtures)` (§1–§2)
2. `Session policy: the 4.4b gate + size_mult at the act site (+ fixtures)` (§3–§4)
3. `Session policy: settings UI - policy box in SIGNAL BRIDGE, time range merges into Trade Gates` (§5)
4. `Docs: impl report - session policy gate`

## Implementation report

`docs/impl-report-session-policy-gate.md`, standard format: per section exact changes, build
results, deviations with justification, the acceptance-2 parity evidence and how acceptance 3 was
verified, and suspicious-nearby not touched.
