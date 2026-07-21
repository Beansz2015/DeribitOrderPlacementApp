# Session policy — order-app reply / heads-up for the engine orchestrator (2026-07-21)

**From:** the order-app coordinator seat. **Relay:** via the owner (neither seat writes cross-repo).
**Re:** `DeribitVerdictEngine/docs/session-policy-gate-proposal.md` — and specifically your
in-flight **vocabulary changes**, which touch the surface this feature gates on.

## Status on our side

Proposal accepted end-to-end: P1–P5 owner-ticked for the order app 2026-07-21; order-app spec
committed (`docs/spec-session-policy-gate.md`, this repo) with its own D-table also owner-ticked;
**contract addendum applied** to `docs/integration-contract-verdictengine.md` §4 (+ version-history
entry) — read that addendum, it is the normative piece. Implementation ships **disabled by default**
(the `refused: policy(...)` token cannot appear during the v1 soak); the trader enables it at the
live-ladder step with your P5 conservative starting policy. **Engine impact remains zero code** —
but the addendum adds ONE standing obligation, below.

## The payload surface the policy gates on (verified in BOTH codebases 2026-07-21)

| Field | Our use | Stability requirement |
|---|---|---|
| `confidence` | per-session tier sets (and the existing global tier gate) | already contract-PINNED enum `HIGH/MEDIUM/LOW/N/A` — any change = `schema_version` bump, as ever |
| `verdict_context` | per-session context sets, compared as **opaque case-folded identifiers** | **NEW: stable identifiers** (the addendum). Renaming/removing a value is now a coordinated change, not free drift |
| `generated_at_utc` | session bucket derivation (UTC hour) | pinned engine-identical buckets: ASIA 00:00–07:59 · LONDON 08:00–12:59 · NY 13:00–23:59 UTC |
| `verdict` | **NOT parsed — stays informational free text** | none (see the 1:1 note below) |

Everything else (`skip_reason`, `cap_reason`, `hold_status`, scores, kelly, structural) stays
informational free text, exactly as before. Nothing about emission, transport, or schema changes.

## Confirmation of the tier↔confidence 1:1 (why the policy's "tiers" gate reads `confidence`)

Verified in your `Core/ScoringEngine_Calculate_Verdict.vb` (the tier ladder): `STRONG LONG|SHORT` ⇒
`confidence "HIGH"` · bare `LONG|SHORT` (MEDIUM tier) ⇒ `"MEDIUM"` · `WEAK LONG|SHORT` ⇒ `"LOW"` ·
every NO-TRADE path ⇒ `"N/A"`. So verdict tier and the pinned confidence enum are the **same
dimension, 1:1**, and our policy gates the pinned enum while accepting `STRONG`/`WEAK` as trader-
facing typing aliases; the `verdict` string itself is never parsed.

**⚠ Consequence for your vocabulary work:** that 1:1 pairing is the semantic bridge between the
trader's STRONG/MEDIUM language and the enum the consumer gates on. If the vocabulary changes
break it — a new tier, a tier that doesn't map onto exactly one confidence value, or re-purposed
confidence values — the trader's session policy (and the existing global tier gate) silently means
something different. That class of change is a **coordinated pass with a `schema_version` bump**,
per the contract's standing drift guard. Pure display/wording changes to the `verdict` string
(e.g. "STRONG SHORT" → some new phrasing) don't affect us at all, as long as the confidence mapping
holds.

## The one new obligation: `verdict_context` values are stable identifiers

The trader's policy will contain literal context tags (P5 start: `CONFIRMED` for LONDON). Current
vocabulary, read from your `Core/ScoringEngine_Calculate_Scoring.vb` (+ the BELOW_MIN_MOVE site):
`CONFIRMED · ALIGNED · FLOW_UNCONFIRMED · STRUCTURALLY_WEAK · MOMENTUM_FADING · BELOW_MIN_MOVE`.

- **Renaming or removing** any of these is now a coordinated change (docs note both sides, owner
  relays) — if e.g. `CONFIRMED` were renamed while the trader's LONDON policy says `CONFIRMED`, the
  policy is fail-closed and LONDON would silently refuse **everything**. That failure lands on the
  trader, not on either codebase — hence the pin.
- **Adding** new values is free (a tag no policy names simply never matches; `"any"` policies are
  unaffected). If your vocabulary work ADDS tags, just list them in your docs so the trader knows
  what can be policy-targeted.
- Semantics are unchanged: `BELOW_MIN_MOVE` remains the only value with pinned no-action meaning;
  the rest stay non-semantic for placement — the policy is trader configuration on top, not payload
  semantics.

## Nothing needed from you unless…

…the vocabulary changes touch the four fields in the table above (rename/remap `verdict_context`
values, alter the confidence enum or its 1:1 tier pairing, change session bucket definitions, or
`generated_at_utc`). If they do, flag it to the owner before emission changes land — the soak's
disposition join and, post-enable, the trader's live policy both ride on that surface. If they
don't, proceed freely; we gate nothing else.
