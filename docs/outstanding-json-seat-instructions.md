# `docs/outstanding.json` — instructions for seats

> **COPIED 2026-10-06 from the engine repo** (`C:\Dev\DeribitVerdictEngine\docs\outstanding-json-seat-instructions.md`),
> on the engine orchestrator's advice. A project rule wins over the global block, so **this copy is
> the one in force here.** Engine §7 (its hand-off state) is dropped. Two terms name engine
> artefacts: "the Session Start Protocol in `CLAUDE.md`" means this repo's `CLAUDE.md` section
> "Outstanding pane"; `trader-tick-queue.md` and "D-table" have no copy here, so the specs and
> `docs/HANDOVER-7.md` win instead.
>
> **Two traps the engine hit:** (a) when the push row counts commits ahead, count the commit
> that updates the row itself; (b) never commit a file an agent is editing in the same tree --
> run `git diff <file>` before staging.

**Written 2026-10-02 (UTC), trader-directed.** The standing rule is item 8 of the Session Start Protocol in [`CLAUDE.md`](../CLAUDE.md). This file is the detail behind it.

**Model + effort for this upkeep:** any seat, **low**. It becomes **medium** when a row's state needs a check in git first (see step 2).

---

## 1. What the file is for

- The **`outstanding-pane` mod** reads `docs/outstanding.json` and shows it in a pane and in the status line of the Claude Code desktop app.
  - Mod source: `C:\Users\user\.claude\mods\outstanding-pane\`.
  - The mod is **read-only**. It never edits this file or any other doc.
- The trader reads the pane instead of asking "what is outstanding?".
- **The file is a mirror.** The specs, their D-tables and [`trader-tick-queue.md`](trader-tick-queue.md) still win. A wrong row here misleads the trader at a glance, so check before you write.

## 2. When to update it

**Update the file in the same commit as the change.** Its `updated_utc` changes in that same commit.

| Event | Change in `outstanding.json` |
|---|---|
| A decision is queued for the trader | Add a `waiting_on_trader` row with `tick: true`, `id`, `doc`, `anchor` |
| The trader rules or ticks it | Delete the row |
| An agent, a long job or a deploy starts | Add an `in_progress` row with `started_utc` |
| It finishes | Delete the row; add any follow-up to `next_up` or `dated` |
| A dated read or check is set, moved or done | Add, re-date or delete the `dated` row |
| The work order changes | Re-order `next_up` |
| Push state changes (the trader pushed, or new local commits) | Update or delete the `push` row |

**Before you write a row, check it against the tree.** Queue rows and handovers go stale. Rows have said "owed" for work already shipped.
- `git log --oneline -S'<symbol>' -- <file>` for code state.
- `git status -sb` for push state.
- Read the D-table itself for ruling state, not a summary of it.

## 3. Format (schema 1)

```json
{
  "schema": 1,
  "updated_utc": "2026-10-02T13:52Z",
  "updated_by": "seat <name>, <model>",
  "away": { "from": "2026-10-14", "to": "2026-11-25" },
  "waiting_on_trader": [ ... ],
  "in_progress": [ ... ],
  "dated": [ ... ],
  "next_up": [ ... ]
}
```

### Fields on an item

| Field | Used in | Meaning |
|---|---|---|
| `what` | in_progress, dated, next_up | What the item is. One line |
| `kind` | waiting_on_trader | `ruling` · `tick` · `review` · `vote` · `push` · `go` |
| `meaning` | waiting_on_trader | One line: what the trader decides |
| `tick` | any | `true` only when a D-table row or decision waits for the trader's tick |
| `id` | any | The decision or finding ID. **Only on items with `tick: true`** |
| `doc` | any | Repo-relative path, e.g. `docs/liquidation-park-spec.md` |
| `anchor` | any | Where in `doc`, e.g. `§4 D-table, row LP-3`. Never without `doc` |
| `since` | waiting_on_trader | GMT+8 date it started waiting, `YYYY-MM-DD` |
| `where` | in_progress | `AWS box …` · `agent <name>` · `local` |
| `started_utc` | in_progress | UTC time, `YYYY-MM-DDTHH:mmZ` |
| `check` | in_progress | The command that checks it |
| `date` | dated | **GMT+8** calendar date, `YYYY-MM-DD` |
| `due_utc` | dated | Exact due time in **UTC**; use instead of `date` when the time matters |
| `approx` | dated | `true` when the date is approximate (shown as `~`) |
| `model` | next_up | Model and effort, e.g. `Opus 5.5, high` |

## 4. Rules — the pane depends on them

1. **The doc path shows only for an item with `tick: true` or an `id`.** Every other item shows no doc, to keep the pane short.
2. **Give an item an `id` only when it has `tick: true`.** An ID with no doc beside it is unusable to the trader (global rule: never a bare ID).
3. **Keep internal IDs out of `what` and `meaning` on items without `tick`.** Write the plain meaning instead.
   - Wrong: `"what": "Build RV-1..RV-3"`
   - Right: `"what": "Fix the burst-tools review findings (cache check, run-1 pins, surviving mutants)"`
4. **Never write a bare section number.** `anchor` always sits with its `doc`. In `what`, name the doc first if you must cite a section.
5. **Dates and times:**
   - `date` and `since` are **GMT+8** calendar dates. The pane shows them as written.
   - `*_utc` fields are **UTC**. The pane converts them to GMT+8.
   - Project docs give UTC dates. When you copy one into `date`, keep the same calendar date unless the exact hour matters. If the hour matters, use `due_utc`.
6. **`next_up` is priority order, not date order.** The pane shows the first 5. Put the most important item first.
7. **One item, one row.** Several items on one date are separate rows with the same `date`. The pane groups them under one date header.
8. **Delete closed items.** History lives in git and in `trader-tick-queue.md`.

## 5. Example rows

```json
{ "kind": "ruling", "tick": true, "id": "EX-1",
  "meaning": "Example: park the replay penalty with the live one, or keep it",
  "doc": "docs/example-spec.md", "anchor": "§4 D-table, row EX-1", "since": "2026-10-02" }

{ "kind": "push", "meaning": "Push master — 8 commits ahead of origin, docs and tools only", "since": "2026-10-02" }

{ "what": "History backfill on a temporary AWS instance", "where": "AWS i-0abc…",
  "started_utc": "2026-10-01T16:46Z", "check": "aws ssm … (runbook step c2)" }

{ "date": "2026-10-08", "approx": true, "what": "Absorption Stage 1 read" }

{ "what": "Review the burst-tools fixes — owed before run 1", "model": "Opus 5.5, high" }
```

(`EX-1` and `docs/example-spec.md` are invented to show the shape. Neither exists.)

## 6. Check before you commit

Run this from the repo root. It must print `OK`:

```bash
python -c "import json,os,sys;d=json.load(open('docs/outstanding.json',encoding='utf-8'));assert d['schema']==1;bad=[(k,i) for k in ['waiting_on_trader','in_progress','dated','next_up'] for i in d[k] if (i.get('id') and not i.get('tick')) or ((i.get('id') or i.get('tick')) and not i.get('doc')) or (i.get('doc') and not os.path.exists(i['doc']))];print('OK' if not bad else bad)"
```

It checks:
- the JSON parses and `schema` is 1;
- no item has an `id` without `tick`;
- every `id` or `tick` item has a `doc`;
- every `doc` path exists.

It does **not** check for internal IDs inside `what` text. Read your rows once for that.
