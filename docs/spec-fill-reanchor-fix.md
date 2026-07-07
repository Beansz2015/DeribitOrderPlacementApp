# Answer + forward spec — fill re-anchor fix (TP-only, deferred)

**Answers:** `spec-back-fill-reanchor-bug.md` (2026-07-08). **Decision authority:** coordinator, 2026-07-08; the §2 decision is final unless the owner vetoes the trailing-stop reading below.
**Implementer:** the same conversation (entry-chase v2 / sl-chase-v2 seat). **Sequencing:** sl-chase-v2 is already implemented locally (`b1b37e7`/`f0a2110`, coordinator review pending) — this fix lands **on top, before the combined runtime session + push**; its doc-count reversal (§3.5) supersedes any "8 sites" mention in the sl-chase-v2 records, so reconcile those in the same commit.
**Ground rules:** standing — build 0/0, local commits, owner pushes, impl report `docs/impl-report-fill-reanchor-fix.md`.

---

## 1. Verdict on the spec-back — mechanism CONFIRMED, and the miss was the spec's

§2 (stale `Current*` IDs) and §3 (fires before the new IDs exist) are verified against the code and the #47 log. For the record: the two-pair ID model was documented by the original author at the exact region entry-chase v2 touched (`:2160`/`:2192`), and **both the v2 spec (§4) and its coordinator review missed it** — the review's greps validated the new code's internal consistency, not its target-ID assumption. The drift bound contained the damage exactly as designed (no money bug). Process note for the next seat: "verify against code" includes the original author's comments in the touched region.

## 2. THE DECISION — TP-only re-anchor (spec-back option (b)), because the SL leg is a native TRAILING stop

The design question assumed the SL is a static stop whose "intended distance" (`fill ± triggerDistance`) the app should enforce. **It is not.** Both SL legs are placed with Deribit `trigger_offset` from `txtTriggerOffset` (OTOCO leg `:2966`, trailing bracket `:3884`) — the **exchange trails the trigger natively** after activation.

**Proof from trade #47 itself:** both chase steps (63126→63125.50→63112.50, total 13.5) were inside the 30-point drift bound (`Min(60,60)/2`), so the legs were **never edited** — a static SL would have rested at placement geometry (trigger 63186 / limit 63216). The exchange's open SL limit was **63160.5** — the market fell toward TP (~63082.5 low), the trail followed it down (offset ≈ 48), a ~48 retrace price-triggered it, and the limit rested at 63130.5 + 30 = 63160.5. The "tighter than intended" stop in the spec-back's §4 is **the trailing offset doing its job from the live market** — not a defect. Consequently:

- A static `fill ± triggerDistance` re-anchor of the SL would **fight the native trail** — transient at best (the trail re-moves the trigger on the next favorable tick), state-confusing at worst. There is no durable effect to buy with the interleave complexity.
- If the owner wants **wider** stops, the knob is `txtTriggerOffset` (and `txtStopLoss`/`txtTrigger` for placement geometry) — a settings decision, not code.
- The TP side is a plain static limit — the fill-anchored re-anchor is pure win and stays.

**Correction to the spec-back's §4 premise** (matters for the record, not the outcome): the post-fill SL is typically in its **untriggered/armed phase first** — tracked by the *untriggered* `StopLossOrder` branch, which captures the new leg's ID and syncs `StopLossTriggerOriginal` as the trail moves (that sync is *why* the pre-trigger emergency baseline stays fresh). #47's `Triggered SL placed @ 63160.5` was a **later price-trigger**, not the activation echo — the log excerpt elides the gap. So a hypothetical SL re-anchor would live in the untriggered branch pre-trigger (no adopt/discriminator interleave at all) — cleaner than §4 feared, but still pointless against the trail. **Backlog, gated:** revisit an SL re-anchor only if the owner someday trades static-stop mode (`txtTriggerOffset = 0`) *and* dislikes the bounded residual (placement-anchored stop ≤ 1.5× intended distance); first step then = one instrumented trade confirming the echo timeline above.

## 3. The fix (one commit + docs)

1. **Defer via a pending flag.** In the filled-`EntryLimitOrder` branch, replace the `ReanchorLegsAsync` call with: capture `pendingReanchorFill As Decimal = entryFillPrice` (new ORDER-context field, same lifecycle as `legAnchorPrice`; reset to 0 at the same three order-context death sites AND after consumption). **Delete the filled-`EntryTrailingOrder` hook entirely** — the trailing bracket has no TP leg, and its SL is the trailing leg (§2 decision) — nothing left to re-anchor.
2. **TP re-anchor at the new TP's echo.** In the *open* `TakeLimitProfit` branch (where `PositionTPOrderId` is set): if `pendingReanchorFill > 0`, compute the fill-anchored TP with the existing formulas (`manualTPval` override honored; **skip the edit if the target equals the echoed resting price** — no pointless edits in manual mode), consume the flag, and dispatch ONE edit to **`PositionTPOrderId`**. ⚠️ Thread placement: this branch runs inside a `Me.Invoke` state lambda — capture the values inside, **dispatch the async send outside the lambda / fire-and-forget** per the handler's existing patterns; never `Await` inside the lambda.
3. **Slim `ReanchorLegsAsync` → TP-only** (or inline it): remove its SL edit and its entire SL-context block (`StopLossTriggerOriginal`/`emergencyBaseline`/`ResetCommandedSLPrices`/`RecordCommandedSLPrice`) — post-fill, the untriggered-echo trail sync is the better source of truth for the trigger record, and with no SL edit there is nothing to record. Keep the `ConsumeCredits`-before-send and amount guards for the single TP edit.
4. **Honest logging:** the log line moves to the dispatch point and names what actually happened: `TP re-anchored to fill $… : $…` — never printed ahead of the send again.
5. **Doc-count reversal (same commit): reset-site count 8 → 7.** Removing the SL-context writes un-makes `ReanchorLegsAsync` as the 8th reset site. Reverse entry-chase v2's count edits: `spec-back-session-2026-07-04.md` §1 (×2) + §10 bullet 4, `HANDOVER-2.md` §3 delta line, and the two code comments (commanded-set header ~`:61`, `ResetCommandedSLPrices` header ~`:4064`) — each noting "was briefly 8 (entry-chase v2 fill re-anchor), reverted to 7 by the TP-only fix". Grep-verify 7/7 pairing afterward.

## 4. Owner runtime tests

1. **Chased entry (the #47 scenario):** exactly one TP edit, to the **new** TP id, fill-anchored price visible on the exchange UI; **zero** `order_not_found`; SL untouched — trail behaves exactly as before; no spurious `Manual SL edit`.
2. **Non-chased entry:** no pending flag consumption beyond a no-op, no edits.
3. **Manual-TP mode:** no pointless edit (skip-if-equal).
4. **Cancel between fill and TP echo** (hard to hit — best-effort): pending flag cleared at context death; nothing fires on the next position.
5. **Trailing-entry chased fill:** no re-anchor edits at all, no errors.
6. **Docs:** grep shows 7 paired reset sites; the four doc locations read 7 with the history note.

## 5. Impl report

`docs/impl-report-fill-reanchor-fix.md`: before→after per §3 item, the thread-placement choice for the TP dispatch, grep evidence for §3.5, and explicit confirmation the SL path (untriggered branch, adopt, chase, trail) is byte-untouched.
