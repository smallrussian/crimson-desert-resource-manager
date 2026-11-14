# Save contents: are camp reserves and dispatch state readable from a save copy?

Milestone 1 feasibility note. Date: 2026-11-11.

## Answer

**Yes, for reserves and active dispatches. Partly, for workers and the clock. No, for mission costs, rewards and names.**

| Question | Result | Confidence |
|---|---|---|
| Camp reserves (Food, Funds, Stone, Timber, Armaments) | In the save, exact integers | Decoded on 7 saves; **game UI check by owner still needed** |
| Which dispatch missions are active, where, by whom | In the save | Decoded on 4 saves; owner check needed |
| Dispatch progress and timers | In the save, linear in an in-game clock | Consistent across 5 operations on 2 saves |
| In-game clock | A monotonic `_fieldTimeRaw` exists. Units not confirmed | Medium |
| Workers in use | Count of mercenaries assigned to operations | Medium |
| Workers total | Unknown. Total mercenaries is readable, but which ones count as camp workers is not | Low |
| Mission costs, rewards, duration, display name | **Not in the save.** Only a hashed operation key is stored | High. Must come from static game data or from the owner's sheet |

## Checked against

- Game: **Crimson Desert 2.04.08**, from `meta/0.paver` (`02 00 04 00 08 00`, DLC `dlc_01` rev 7, build `0xe4d53a97`).
  Steam app 3321460 ("Crimson Desert Enhanced"), Steam build id 26480954.
- Saves: `%LOCALAPPDATA%\Pearl Abyss\CD\save\<steam-id>\slot{0,1,2,100,101,102,103}\save.save`.
  These are **copies** taken to a scratch directory and marked read-only. Originals were not opened for writing.
  The game was not running, launched, saved or loaded at any point.
- Save file version field: 2. HMAC checks out on every file, so decryption is correct.

## How the save is decoded

Format knowledge comes from reading source in public prior art, not from running its binaries:
NattKh `CrimsonGameMods/save_crypto.py`, `save_parser.py`, and bbfox0703 crimsonatomtic
`docs/save-body-format.md`. Reimplemented in `tools/re/` (research tooling, not the product reader).

1. **Container** (`save.save`): 0x80-byte header: magic `SAVE`, u16 version @0x04, u32 decompressed size @0x12,
   u32 payload size @0x16, 16-byte ChaCha20 nonce @0x1A, HMAC-SHA256 @0x2A. Payload at 0x80 is
   ChaCha20(key derived from version) over an LZ4 block. The HMAC covers the compressed bytes.
2. **Body**: a self-describing schema (105 types with field names and type names on 2.04.08), then a TOC of
   top-level blocks, then the data. Because field names travel with the save, the reader can look fields up
   **by name** instead of by offset. That should survive patches much better than fixed offsets.
3. **Objects**: per-object presence mask, then fields in schema order. An absent array or list writes one
   `0x01` byte; a present one starts with `0x00` and a u32 count. Each nested object ends with a u32 size
   that must equal its own length, which is a strong self-check. A pointer field (`meta_kind` 5) has a 1-byte
   `0x01` prefix (found here, not documented in either reference).

`tools/re/cdbody.py` is a strict decoder: it raises on any byte it does not expect. **It decodes every top-level
block in all 7 saves with zero leftover bytes** (783 to 1,179 blocks per save). That is the basis for trusting
the values below.

## Where the data lives

### Reserves

Top-level block `InventorySaveData` → `_inventorylist` → element with `_inventoryKey == 1` → `_itemList`.
Each reserve is an item; the amount is `_stackCount`.

| Reserve (our name) | itemKey | Internal name | slotNo seen |
|---|---|---|---|
| Funds | 11 | `Money_Camp_Money` ("Camp Funds") | 6 |
| Food | 12 | `Money_Camp_Food` | 1 |
| Timber | 13 | `Money_Camp_Wood` | 2 |
| Stone | 14 | `Money_Camp_Stone` | 3 |
| Armaments | 15 | `Money_Camp_Weapon` ("Camp Weapons") | 4 |

Item key → name mapping is from NattKh `data/item_names.json` (extracted game data; used locally for lookup,
not copied into this repo). Note there are separate *trade goods* with the same display names
(e.g. 1001240 "Camp Food", `Trade_Camp_Food`, max stack 10). Those are not reserves. Match on key, not name.

Values read (save file modified time, local):

| Slot | Saved | Food | Funds | Stone | Timber | Armaments |
|---|---|---|---|---|---|---|
| 101 | 2026-10-30 19:34 | 1,287 | 1,780 | 1,042 | 2,056 | 360 |
| 102 | 2026-10-31 16:00 | 2,497 | 1,680 | 1,042 | 2,056 | 360 |
| 100 | 2026-11-05 16:05 | 5,191 | 79,526 | 3,679 | 1,061 | 2,642 |
| 0 | 2026-11-11 15:35 | 7,564 | 33,926 | 3,459 | 3,015 | 3,183 |
| 1 | 2026-11-11 15:41 | 7,564 | 33,926 | 3,459 | 3,015 | 3,183 |
| 2 | 2026-11-11 16:36 | 7,564 | 33,326 | 3,459 | 3,015 | 3,183 |
| 103 | 2026-11-11 16:50 | 7,304 | 31,126 | 3,459 | 3,015 | 3,183 |

**Owner action:** compare slot 103 (the newest) with what the camp screen shows. Until that's done this is *decoded*, not *verified*.

### Dispatch missions (faction operations)

Top-level block `FactionSaveData` → `_factionNodeElementSaveDataList` (1,260 nodes on 2.04.08).
A node with a dispatch running has `_operationKey` present. Fields seen:

| Field | Meaning (inferred) |
|---|---|
| `_ownerFactionKey` | Faction **node** key (type is `FactionNodeKey` despite the name). This is the location |
| `_operationKey` | Which operation (32-bit hash). Joins to static data |
| `_operationStateType` | 1 on every running operation seen. Other values not yet observed |
| `_operationStartTimeRaw` | Clock value when the current cycle started |
| `_operationLastUpdateTime` | Clock value at the last update. Identical across all nodes in one save, just before `_fieldTimeRaw` |
| `_operationCurrentProgress` | Progress, 0–100 (one value of 103 seen, see below) |
| `_operationShouldRepeat` | The "repeat" toggle. In the schema from 2.04.08 on, absent from older schemas. Written only when true (see below) |
| `_operatorMercenaryList[]._mercenaryNo` | Mercenaries assigned (the workers) |
| `_operatorWorkerCount`, `_secondOperatorWorkerCount` | In the schema but absent on every node seen |
| `_totalCombatPower` | Reads as +inf on every running operation. Meaning unknown |

Also in `FactionSaveData`: `_factionStoredOperationRewardList` (absent here: probably rewards waiting to be
collected) and `_lastRewardPushedDay` (absent here).

Active operations in slot 103. Names are the internal names from this install's `factionnode` table
(see `mission-static-data.md`). An earlier version of this note used node names from NattKh's
`faction_node_locations.json`; those were wrong for 2.04.08.

| Node | operationKey | Internal name | Progress | Mercs |
|---|---|---|---|---|
| 1000016 | 4072557552 | `Node_Her_OakenshieldManor_Serkis_Work_Guard_Tier1` | 19.57 | 2 |
| 1000073 | 2535802006 | `Node_Her_StreamsidePier_Fishing` | 33.61 | 2 |
| 1000070 | 42107545 | `Node_Her_SouthQuarry_Work_Stone_Tier2` | 12.41 | 5 |
| 1000311 | 3624579346 | `Node_Her_GlenbrightFarm_Work_Food_Farm_Tier1` | 6.29 | 2 |

**Owner action:** confirm these are the 4 missions marked active in the game, with these worker counts.

### Progress, timers and the clock

`TransformSaveData` → `_fieldSaveDataList[]` → `_fieldTimeRaw` (u64) increases across saves in save order
(3,873,861,300 → 3,878,156,700 → 3,887,659,632 → 3,895,689,912 on 2026-11-11). The gaps do not track
wall-clock time (6 real minutes → +4.3M; 55 → +9.5M; 14 → +8.0M), which fits an **in-game** clock that
skips forward when you wait or sleep.

Progress is linear in that clock:
`progress = (lastUpdate − start) × 2.7778e-6`. In other words, **100% = 36,000,000 ticks**. That holds for:

- Goldleaf: 11.2806 at Δ4,060,188 (slot 2) and 33.6139 at Δ12,100,812 (slot 103), the same operation seen twice
- the 3 other slot-103 operations, and the slot-100 Howling Cave operation

Open points:
- All 5 come out at the same 36M ticks. Either these missions all have the same duration, or progress is
  normalised some other way. If a tick is 1 ms, 36M ticks is 10 in-game hours. **Owner: what durations does the
  game show for these 4 missions?** If they differ, this reading is wrong.
- Slot 101 had 103.05% at a different rate (3.126e-6 per tick). Maybe overrun past completion, or a buff
  (`_operationRewardRateByBuff`). Not explained yet.
- Tick units are unconfirmed. Next step: a single save before and after an in-game wait of known length.
  That needs the owner to play; this agent must not save or load.

### Workers

- Assigned (in use): count of `_operatorMercenaryList` entries over active operations. 11 in slot 103.
- Per comrade: `MercenarySaveData._workPlaceFactionNodeKey` is set on exactly the dispatched ones (11), and
  equals the node of their operation. **Verified against the game's comrade list on 2026-11-11**: the 11
  comrades shown as "Dispatched" are exactly these 11.
- Total: `MercenaryClanSaveData._mercenaryDataList` has 93 entries in slot 103: 52 human comrades
  (character internal names `NHM_/NHW_/NDM_/NOM_Unique_*`), plus mounts, pets, collected insects and Damiane.
- The game's dispatch list says "Comrades Found: 37". In the save, 38 human comrades have `_spawnPosition`;
  the other 14 (Kamu, who runs the farm, Alec, Ben, …) do not, and none of those 14 is in the game's list.
  The owner's screenshots show all 37 names, and all 37 are among the 38. The owner confirms the 38th,
  **Marius** (`NHM_Unique_Marius_468`, mercenaryNo 1000253), is not dispatchable.
- Nothing in Marius's save record separates him from dispatchable comrades (same fields present; his
  `_lastPaidTime` matches Carl and Ross, who are also not dispatchable). So dispatchability is **not fully
  in the save**. It is probably static game data. NattKh's `mercenaryinfo_parser.py` (24 fields) has no
  obvious dispatch flag. Rule for now: human comrade with `_spawnPosition`, minus a per-version exclusion
  list (currently: Marius). Checked on one save only; treat as provisional.
- **Owner decision, 2026-11-11: always exclude Marius.** In the game he manages comrade recruitment and is
  never dispatchable. Match on `_characterKey` (internal name `NHM_Unique_Marius_468`), not on
  `_mercenaryNo`, which is assigned per save.

### Not in the save

The save stores only `_operationKey`. Cost, reward, duration and display name are not there. Per AGENTS.md,
these become **versioned reference data**. Two possible sources:

1. **Owner's spreadsheet** (already has costs, rewards and durations per mission). Import it and key it by
   `(node key, operation key)`. Cheapest, and needs nothing from the game files.
2. **Static game tables** inside the install's `.paz` archives (read-only). crimsonatomtic documents the PAZ
   container and some `.pabgb` tables, but not a faction-operation table. More work, and it must be generated
   on the user's machine, never committed (hard rule 4).

## Other observations

- Slots 100–103 vs 0–2: slot 103 was written last (16:50) and has the newest clock. Which slots are autosaves
  is not established. The reader should pick the save with the highest `_fieldTimeRaw`, not a fixed slot.
- `lobby.save` (about 560 bytes) uses the same container. Not decoded yet; probably the slot summary shown on
  the title screen.
- Save version 1 uses a different key prefix (in prior art). Not seen on this machine.

## Live memory (tier 2): status

Not started, as AGENTS.md requires. With tier 1 working, the main thing tier 2 adds is **freshness**: the
save only changes when the game saves. Before tier 2:
- The owner needs to read this note and sign off explicitly (AGENTS.md).
- Denuvo Anti-Tamper is present. Ban and anti-tamper risk is still **unknown**.
- Nexus mod 2189 (the external overlay reference) returned HTTP 403 to automated fetch. Not reviewed.
- One plus: the schema type and field names above (`FactionNodeElementSaveData`, `_operationCurrentProgress`,
  …) are likely the engine's reflection names. If tier 2 is approved, they would be the starting search terms.

## Reproduce

```
python -m venv .venv && .venv\Scripts\pip install lz4 cryptography
copy a save.save somewhere else first, then:
.venv\Scripts\python tools\re\cdsave.py <copy>\save.save          # header, schema, TOC
.venv\Scripts\python tools\re\cdbody.py <copy>\save.save          # strict decode of every block
.venv\Scripts\python tools\re\camp.py   <copy>\save.save          # reserves, operations, clock
```

### Repeat flag: absent means off (2026-11-13, game 2.05.00)

The body only writes fields whose presence-mask bit is set, and a bool at its default (`false`) is left out.
Evidence: owner save slot 1 (2.05.00) has six running operations. Five carry `_operationShouldRepeat = true`;
operation 1525100747, a one-off build that the owner confirms is not repeating, has no such field while the
`FactionNodeElementSaveData` schema in the same save lists it. Same pattern as `_operatorWorkerCount`, which is
in the schema but absent on every node seen. The reader therefore reads a missing field as `false` when the
save's schema has it, and as unknown when the schema does not (pre-2.04.08 saves). Only one repeat-off mission
has been seen so far; a second (a regular mission with repeat turned off) would confirm it.
