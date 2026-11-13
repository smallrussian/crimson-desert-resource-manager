# Patch 2.05.00: what moved and what held

Game **2.05.00**, Steam build 26636972 (`meta/0.paver` = `02 00 05 00 00 00`, `dlc_01`). Checked 2026-11-13 in one
session (pid 47124). The owner started the game and pressed Continue. The agent did not launch, load or save, and
wrote nothing to the game. Baseline for comparison: the last 2.04.08 save snapshot (2026-11-13 02:46 UTC) in the
reader's snapshot log. The game had not been saved since, so the save holds the same data.

Procedure used: [`docs/patch-update.md`](../patch-update.md).

## Summary

| Data | Source | 2.05.00 result |
|---|---|---|
| Reserves, workers, active missions, clock, camp level, comrades | save (by field name) | **held**, identical to the 2.04.08 baseline |
| Mission cost, members, type, category, duration, node name | factionnode | **held**, all 54 missions identical |
| English names | string tables | **held**, with one text change (below) |
| Available missions (start conditions) | conditioninfo + save | **held**, 48 of 48, same unknowns |
| Mission rewards | dropsetinfo | **moved**, fixed in `offsets/2.05.00/gamedata.json` |
| Comrade skill slots | characterinfo | **moved**, fixed in `offsets/2.05.00/gamedata.json` |
| Live reserves | process memory | **RVAs moved**, offsets held; `offsets/2.05.00/memory.json` |

## Save

`CampLedgerReader snapshot` on the newest save: reserves (food 9,274, funds 50,456, stone 65,831, timber 2,515,
armaments 3,207), workers 37 / 21 in use, 6 active missions with elapsed hours and repeat flag, camp level 4 and
clock all decoded with no errors. All equal to the baseline. The save body is self-describing and read by field
name (`save-contents.md`), so this was expected.

## Game data: drop sets (mission rewards)

Symptom: every reward table failed its self-check (`reward table 1015077 did not validate`) and rewards were null.
The reader failed closed as intended.

Found: `cdprobe rec dropsetinfo 1015077 0 200`, then `cdprobe multidrop` for records with 2 and 3 items. Record
sizes after the comments: 1 item 115 bytes, 2 items 180, 3 items 245, so entries are now **0x41** bytes (were 0x40).

| Field (from record key) | 2.04.08 | 2.05.00 |
|---|---|---|
| item count (u32) | +0x11 | **+0x12** |
| first entry | +0x15 | **+0x16** |
| entry size | 0x40 | **0x41** |
| entry: item, rate, min, max, `FF FF` | +0x1, +0x1A, +0x2A, +0x32, +0x3A | same |
| entry: trailing item copy | +0x3C | **+0x3D** (a new 0 byte at +0x3C) |

Checked on 1015077 (`Item(Money_Camp_Food,1100)`), 1015078 (food 1,200 + milk 5) and 1015082 (stone 2,400 + fine
stone 5 + iron stone 3): every amount equals the record's comment string. With the new layout the six active
missions give the same base rewards as on 2.04.08 (1,900 / 0 / 1,300 / 1,800 / 1,400 / 1,100), and 45 of 48
available missions have a reward.

## Game data: comrade skill slots

Symptom: all 37 comrades had five empty slots. This did **not** fail closed: the old offset landed on zeros, and
zero means "slot holds a non-camp skill", so the reader reported "no camp skills" and the bonus would have been
under-stated. Fixed: a record whose slots are all zero is now unknown (`Catalogue.ParseSkillSlots`); every comrade
checked on both versions has at least one camp skill.

Found: `cdprobe recbin characterinfo <key>` for Brant (1002970), Matilda (1002608) and Luke (661), then searched
backwards from each slot marker for known camp skill keys (`cdprobe campskills`).

| | 2.04.08 | 2.05.00 |
|---|---|---|
| distance between slot runs | ~1,504 | **1,581-1,585** |
| skill key before the run's first marker | 696 | **768** |

The decoded slots for all 37 comrades are identical to 2.04.08 (e.g. Brant: Strengthening I, Service I,
Strengthening II, Food Processing I, Strengthening III).

## Names

One English mission type changed: `Her_TimeberhamSawmill_Work_Wood_Tier1` was "Small-Scale Logging", now
"Basic Logging". Read from the game's own 2.05.00 string table, so this is the game's text, not a reader error.
**Owner to confirm on screen.**

## Live memory

`cdprobe verify offsets/2.04.08/memory.json`: 0 of 5 (exe rebuilt, class RVAs moved).

`cdprobe relocate offsets/2.04.08/memory.json funds=50456 food=9274 ...` (shape search using the 2.04.08 object
offsets, no RVAs) found one group with a full owner route into the exe:

| | 2.04.08 | 2.05.00 |
|---|---|---|
| item class | exe+0x591EA30 | **exe+0x599B338** |
| owner class (item +0x28) | exe+0x5A50FA0 | **exe+0x5ACB500** |
| root class (owner +0x28) | exe+0x59FCB20 | **exe+0x5A78688** |
| header +0xD0, amount +0xD8, key +0xE4, balance list +0x30 | | unchanged |

On the first pass food, timber, stone and armaments had balance objects equal to the save. Funds had only its zero
placeholder at that moment. A minute later, `cdprobe verify offsets/2.05.00/memory.json` (the reader's own
`MemorySource` code) and `CampLedgerReader memory` both located **5 of 5** in about 11 s: funds 50,456, food 9,274,
timber 2,515, stone 65,831, armaments 3,207, all equal to the save.

## Not yet confirmed (AGENTS.md wants a second session)

- Memory RVAs after a game restart on 2.05.00.
- A live reserve change picked up from memory on 2.05.00.
- The Timberham Sawmill name change, on screen.
- A full launcher (ASI) session on 2.05.00. The launcher started the old Release build of the reader for this
  session, before these changes. It needs a rebuild of that folder after the game closes, because the running
  reader locks it.
- The dispatch-progress object from `live-memory.md` (node +0x30 f32) is not used by the reader and was not
  re-checked.
