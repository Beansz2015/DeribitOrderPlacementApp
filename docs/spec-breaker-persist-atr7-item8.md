# Spec — breaker persistence + trailing-LONG guard alignment + ATR fallback 7 (cutover §8 rulings)

**Origin:** the owner's three rulings 2026-07-24 on `production-cutover-checklist.md` §8.
**Implementer:** the coordinator seat (owner-directed "write the spec and implement"; deviation
from the usual fresh-implementer split, acceptable at this size — ~30 mechanical lines across
three already-proven patterns; mitigations: gate per commit, greps recorded below, owner runtime
acceptance). **Target:** `AppUserSettings.vb`, `frmMainPageV2.vb`, `AutoTradeSettings.vb` +
`.Designer.vb`. One commit per item.

## Rulings (owner, 2026-07-24 — decisions of record)

| # | Ruling |
|---|---|
| R1 | **Persist the circuit breaker**; starting default **10** (was Designer `-1` = off each start) |
| R2 | **Align the trailing-LONG slippage-guard input to `bestBid`** (own-side convention; closes the F18 drift flag) |
| R3 | **Payload-first ATR stands (no change); the FALLBACK mirrors the engine: ATR period default 14 → 7** (engine `settings.json` `ATR.period = 7`, verified 2026-07-24; the old 14 followed the retired FrmIndicators autotrading) |

## Item 1 — breaker persistence (R1; the risk-keys pattern, third application)

- `AppUserSettings`: `CircuitBreakerUsd As Decimal = 10D`; Load reads `circuit_breaker_usd`
  (absent ⇒ 10); Save writes it (atomic path). Docstring updated.
- Host (`frmMainPageV2`, beside the risk accessors): `Friend ReadOnly Property CircuitBreakerUsd`
  (userSettings, default 10) + `Friend Sub SetCircuitBreakerUsd(v As Decimal)` — **accepts ANY
  parsed value including ≤ 0**: disabling the breaker is a legitimate owner choice, so unlike the
  risk keys only a PARSE failure keeps last good (the existing `TryParse` guard + orange warning).
- `AutoTradeSettings`: `SeedCircuitBreakerFromHost()` runs with the other seeds, BEFORE the first
  `CommitGateConfig` (the standing seed-before-commit trap, third application);
  `CommitGateConfig`'s parse-success branch also pushes to the host. Designer default `-1` → `10`;
  the `_circuitBreakerUsd` mirror default likewise (overwritten by the seed in practice).
- Semantics unchanged everywhere else: `<= 0` disables; the bridge live-reads the mirror.

## Item 2 — trailing-LONG guard input (R2; one line)

`frmMainPageV2.vb` trailing-entry LONG block: `IsATRSlippageExcessive(bestAsk, "LONG")` →
`bestBid`; the "left as-is deliberately, flagged" comment is replaced by the ruling record.
Effect: that one path's abort guard measures on the own-side quote like the other three, tripping
~one spread (~1 tick) later than before. Placement behaviour untouched (the guard only measures).

## Item 3 — ATR fallback period 14 → 7 (R3; defaults only)

Designer `txtAtrLength.Text` `"14"` → `"7"`; `frmMainPageV2` `atrLengthVal` field default and the
`AtrLength` property's `If(… , 14)` floor → 7. The `$70` fallback and the payload-first priority
are untouched; the box stays a live UI knob (owner re-syncs it if the engine's period ever moves).

## Acceptance

1. Gate per commit (GATE PASSED, 96/96) + the §Greps below.
2. **Owner runtime (breaker round-trip):** hand-edit `circuit_breaker_usd: 25` → restart → box
   shows 25 and is in force; set the box to `-1`, save-close, restart → still `-1` (a disable
   persists); delete the key → restart → 10.
3. Owner observation: `ATR now:` readout shows the indicator at period 7 when the payload is stale;
   trailing-LONG entries abort ~1 tick later (no observable difference expected in practice).

## Greps (recorded at implementation)

- `IsATRSlippageExcessive(best` → LONG rows all `bestBid`, SHORT rows all `bestAsk` (4 reposition
  gates own-side; the 6 pre-placement gates use `BestPrice` by construction — untouched).
- No new SL/commanded sites; `SignalBridge.vb` untouched except nothing (bridge reads mirrors).

## Implementation record (appended at completion)

*(pending)*
