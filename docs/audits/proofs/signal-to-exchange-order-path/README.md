# Proofs — signal-to-exchange order path audit (2026-09-24)

Proof material for `docs/audits/2026-09-24-signal-to-exchange-order-path.md`.

| File | What it is | Run status |
|---|---|---|
| `trace.py` | A Python copy of the app's pure pricing and sizing functions (`RoundToTick`, `DeriveManualSl`, `RiskSizedBase`) plus the fee arithmetic for the LOGIC TRACE. **It copies the formulas; it is not the app's compiled code.** | Run. Output is below |
| `AuditFixtures.vb.txt` | An OrderCheck fixture module that calls the app's own `Friend Shared` functions (`ParsePayload`, `ParsePayloadJson`, `RiskSizedBase`, `DeriveManualSl`). It prints `FAIL` wherever the app does the unsafe thing. | **NOT RUN.** There is no .NET SDK in the audit environment, and the app targets `net9.0-windows8.0` |

## trace.py

Command, run from the repo root:

```
python3 docs/audits/proofs/signal-to-exchange-order-path/trace.py
```

Output (Python 3, Linux, 2026-09-24; the scratchpad original and this copy printed the same thing):

```
== A flush STRONG LONG: engine E=59003.5 S=58811.13 T=59079.15
  placed: TP=59079  SL trigger=58811 (engine 58811.13, moved -0.13)  SL limit=58781
  levels gate (stop<=0 or target<=0): PASS; side check: none
  risk size (risk 25, max 500): 500 USD
  risk size (risk 25, max 0): 7660 USD
  slippage cap 0.6*ATR = 72.12; anchored to FIRST SEEN BID, not levels.entry
  fill 59003.50: gross TP 12.80bp / stop 32.63bp | net win 9.80bp, net loss maker 35.63bp, via M.SL taker 49.49bp | breakeven winrate 78.4% / 83.5%
  fill 58953.50: gross TP 21.29bp / stop 24.17bp | net win 18.29bp, net loss maker 27.17bp, via M.SL taker 41.05bp | breakeven winrate 59.8% / 69.2%
  fill 59075.62: gross TP 0.57bp / stop 44.79bp | net win -2.43bp, net loss maker 47.79bp, via M.SL taker 61.64bp | breakeven winrate n/a (TP loses money)
== B 2-USD SWING_STOP: engine E=59003.5 S=59001.5 T=59079.15
  placed: TP=59079  SL trigger=59001.5 (engine 59001.5, moved +0.0)  SL limit=58971.5
  levels gate (stop<=0 or target<=0): PASS; side check: none
  risk size (risk 25, max 500): 500 USD
  risk size (risk 25, max 0): 737540 USD
  slippage cap 0.6*ATR = 72.12; anchored to FIRST SEEN BID, not levels.entry
  fill 59003.50: gross TP 12.80bp / stop 0.34bp | net win 9.80bp, net loss maker 3.34bp, via M.SL taker 17.20bp | breakeven winrate 25.4% / 63.7%
  fill 58953.50: gross TP 21.29bp / stop -8.14bp | net win 18.29bp, net loss maker -5.14bp, via M.SL taker 8.73bp | breakeven winrate -39.1% / 32.3%
  fill 59075.62: gross TP 0.57bp / stop 12.55bp | net win -2.43bp, net loss maker 15.55bp, via M.SL taker 29.40bp | breakeven winrate n/a (TP loses money)
== C bad-settings long, stop ABOVE entry: engine E=59003.5 S=59050.0 T=59079.15
  placed: TP=59079  SL trigger=59050 (engine 59050.0, moved +0.0)  SL limit=59020
  levels gate (stop<=0 or target<=0): PASS; side check: none
  risk size (risk 25, max 500): 500 USD
  risk size (risk 25, max 0): 31720 USD
  slippage cap 0.6*ATR = 72.12; anchored to FIRST SEEN BID, not levels.entry
  fill 59003.50: gross TP 12.80bp / stop -7.88bp | net win 9.80bp, net loss maker -4.88bp, via M.SL taker 8.98bp | breakeven winrate -99.3% / 47.8%
  fill 58953.50: gross TP 21.29bp / stop -16.37bp | net win 18.29bp, net loss maker -13.37bp, via M.SL taker 0.50bp | breakeven winrate -271.8% / 2.7%
  fill 59075.62: gross TP 0.57bp / stop 4.34bp | net win -2.43bp, net loss maker 7.34bp, via M.SL taker 21.19bp | breakeven winrate n/a (TP loses money)
```

Reading notes:
- `bp` means basis points of notional. "maker" means maker entry plus maker exit (3.0 bp). "via M.SL taker" means maker entry, a taker exit, and the 70 USD market-stop threshold beyond the trigger (5.0 bp in fees).
- Negative "stop" values mean the stop trigger sits **above** the long's fill: the stop is on the wrong side of the position. Break-even figures in those rows are meaningless.
- The three fills model: filling at the engine's entry; the flush continuing 50 USD; and the chase running to the 0.6 × ATR cap (72.12).

## AuditFixtures.vb.txt (not run)

Intended run on Windows:

1. Copy it to `tools/OrderCheck/AuditFixtures.vb`.
2. Add `AuditFixtures.Run()` to `OrderCheck.Program.Main`.
3. Run:

```
dotnet run --project tools/OrderCheck/OrderCheck.vbproj
```

Expected: a `FAIL` line for each check. No output was captured because it has never been run.
