# Transliteration of SignalBridge/frmMainPageV2 pure seams (NOT their compiled code).
from decimal import Decimal as D, ROUND_HALF_UP, ROUND_FLOOR
def round_to_tick(p): return (p*2).quantize(D(1), rounding=ROUND_HALF_UP)/2   # frmMainPageV2.vb:381 (AwayFromZero; prices >0)
def derive_manual_sl(is_long, stop, off): return round_to_tick(stop-off if is_long else stop+off)  # SignalBridge.vb:975
def risk_sized_base(risk, maxs, ref, dist):                                    # SignalBridge.vb:1123
    if risk<=0 or ref<=0 or dist<=0: return D(-1)
    s=(risk*ref/dist/10).to_integral_value(rounding=ROUND_FLOOR)*10
    if maxs>0 and s>maxs: s=(maxs/10).to_integral_value(rounding=ROUND_FLOOR)*10
    return s
OFF=D(30); ATR=D('120.2'); MAKER=D('1.5'); TAKER=D('3.5'); MSL=D(70)
def case(name, entry, stop, target):
    lim=derive_manual_sl(True, stop, OFF); trig=lim+OFF; tp=round_to_tick(target)
    print(f"== {name}: engine E={entry} S={stop} T={target}")
    print(f"  placed: TP={tp}  SL trigger={trig} (engine {stop}, moved {trig-stop:+})  SL limit={lim}")
    print(f"  levels gate (stop<=0 or target<=0): {'REFUSE' if stop<=0 or target<=0 else 'PASS'}; side check: none")
    for maxs in (D(500), D(0)):
        print(f"  risk size (risk 25, max {maxs}): {risk_sized_base(D(25),maxs,entry,abs(entry-stop))} USD")
    cap=D('0.6')*ATR
    print(f"  slippage cap 0.6*ATR = {cap}; anchored to FIRST SEEN BID, not levels.entry")
    for fill in (entry, entry-50, entry+cap):
        win=(tp-fill)/fill*10000; loss=(fill-trig)/fill*10000; loss_e=(fill-trig+MSL)/fill*10000
        wn=win-2*MAKER; ln=loss+2*MAKER; le=loss_e+MAKER+TAKER
        be=ln/(wn+ln) if wn>0 else None; bee=le/(wn+le) if wn>0 else None
        print(f"  fill {fill:.2f}: gross TP {win:.2f}bp / stop {loss:.2f}bp | net win {wn:.2f}bp, net loss maker {ln:.2f}bp, via M.SL taker {le:.2f}bp | breakeven winrate {'n/a (TP loses money)' if be is None else f'{be*100:.1f}% / {bee*100:.1f}%'}")
case("A flush STRONG LONG", D('59003.5'), D('58811.13'), D('59079.15'))
case("B 2-USD SWING_STOP", D('59003.5'), D('59001.5'), D('59079.15'))
case("C bad-settings long, stop ABOVE entry", D('59003.5'), D('59050.0'), D('59079.15'))
