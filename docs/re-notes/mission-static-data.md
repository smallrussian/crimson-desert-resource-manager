# Mission catalogue: static game data

Date: 2026-11-11. Game **2.04.08** (Steam build 26480954). Status: **partly decoded, not yet verified
against the game UI.**

## Answer so far

Mission definitions (cost, worker requirement, type, internal name) are **static game data**, not save data.
They are in the `factionnode` table inside the install's archives. The save and live memory only say which
operation key is running where. So: catalogue from the install (read-only), state from save or memory.

## Where

- Archive index: `<install>/0008/0.pamt`, data in `0008/<n>.paz`. DLC adds `dlc/dlc_01/0008/`.
- Table: `gamedata/binarystaticinfo__/bin/factionnode` (body 759,524 bytes, LZ4; header 8,962 bytes).
  DLC variant: `factionnode.dlc_01`. Also present, not yet examined: `factionmanagement.dlc_01`,
  `factionoperationgroupinfo`, `mercenaryinfo`, `mercenarygroupinfo`.
- Tool: `tools/re/paz.py` (read-only). Extracted tables go to a scratch folder, **never into this repo**.

### PAMT layout changed

The public parser (NattKh `paz_parse.py`) does not parse 2.04.08 indexes. Layout observed here:

```
u32 hash, u32 paz_count, u32 ?, then paz_count x (u32 index, u32 crc, u32 size)   [index of first is the "?" slot's neighbour]
u32 folder_names_size, folder name trie
u32 file_names_size,   file name trie
u32 folder_count, folder_count x (u32 hash, u32 folder_name_ref, u32 first_file, u32 file_count)
u32 file_count,   file_count   x (u32 name_ref, u32 paz_offset, u32 comp_size, u32 orig_size, u32 flags)
extension strings, NUL-separated
```

Name trie entry: LEB128 back-distance to the parent entry (0 = root), u8 length, bytes. Full name = parent
chain joined. `flags & 0xFF` = paz file number, `(flags >> 16) & 0xF` = 2 means LZ4, `flags >> 24` selects an
extension (the exact index-to-extension mapping is not pinned down). 2,095,400 entries parse; `0016/0.pamt`
does not parse and a few `*_misc` tables fail LZ4 (not needed).

## Operation record (inside a node record)

Found by searching the table for the four `_operationKey` values from the save: **each occurs exactly once.**
Layout from the key onward, as far as understood:

| Field | Notes |
|---|---|
| u32 operation key | Same value as the save's `_operationKey` |
| 9 bytes | Unknown |
| u32 len + decimal digits | 19–20 digit number. Probably the localisation key for the display name |
| u16 | 0x4240–0x4248. Unknown |
| u32 cost count, then count x 24 bytes | `u32 item key, 8 zero bytes, u64 amount, u32 0`. Item keys 11 (Funds) and 12 (Food) seen |
| reward block | Keys like 1015059 (`13 7d 0f 00`). Probably a drop-set reference. **Not decoded** |
| u32, u32, u32 | `1, N, M`. **N matches the number of comrades assigned in the save** on all four. M unknown (max?) |
| u32 len + category | "Farming", "Fishing", "Mining", "Strengthening", … |
| … | A 10000 (`10 27 00 00`) appears; meaning unknown |
| u32 len + internal name | e.g. `Node_Her_StreamsidePier_Fishing` |

1,022 operation records in the base table. 131 have a category and a `Node_…` name within the expected
distance: Farming 35, Fishing 18, Ranching 14, Ritual 12, Foodprocessing 11, Strengthening 7, Mining 7,
Logging 7, Banking 6, Weaving 4, Engineering 3, Drawing 2, Crafting 2, Ornamenting 2, Building 1. The other
891 share one repeated shape (2,000 Funds + 1,800 Food, no name) and look like combat or patrol operations.

The owner's four active missions:

| operationKey | Internal name | Category | Cost | N |
|---|---|---|---|---|
| 4072557552 | `Node_Her_OakenshieldManor_Serkis_Work_Guard_Tier1` | Strengthening | Food 120 | 2 |
| 2535802006 | `Node_Her_StreamsidePier_Fishing` | Fishing | Funds 600 | 2 |
| 42107545 | `Node_Her_SouthQuarry_Work_Stone_Tier2` | Mining | Funds 1,300 + Food 140 | 5 |
| 3624579346 | `Node_Her_GlenbrightFarm_Work_Food_Farm_Tier1` | Farming | Funds 900 | 2 |

**Correction:** `save-contents.md` first named these nodes from NattKh's `faction_node_locations.json`
(Oasis Reflection, Goldleaf Guildhouse, Rodbel Fishing Dock, Pailune cave). Those names were wrong for
2.04.08; that table is from an older game version. Use the names above, which come from this install.

**Owner check, 2026-11-11: the four costs match the game.** The category string is an internal label, not
the display name: the game shows "Strengthening" as **Estate Protection**.

## Rewards: drop sets

The key in the operation's reward block is a key into `gamedata/binarystaticinfo__/bin/dropsetinfo`
(body 2,825,609 bytes). Record: `u32 key`, 13 zero bytes, `u32 entry count`, then per entry roughly
`u8 1, u32 item key, …, u32 1,000,000 (rate, = 100%), …, u64 min, u64 max, ff ff, u32 item key, …`, then a
length-prefixed comment such as `Item(Money_Camp_Money,1900)`. Read the binary amounts, not the comment.

| Mission | Drop set | Reward (binary) | Comment in file |
|---|---|---|---|
| Guard Tier1 (Estate Protection) | 1015059 | item 11 Funds × 1,900 | `Item(Money_Camp_Money,1900)` |
| StreamsidePier Fishing | 1015076 | item 12 Food × 1,300 | `Item(Money_Camp_Food,1300)` |
| SouthQuarry Stone Tier2 | 1015091 | item 14 Stone × 1,400, item 1000555 × 5, item 720001 × 3 | `Item(Money_Camp_Stone,1400);Item(Fine_Stone,5);Item(IronSto…` |
| GlenbrightFarm Food Tier1 | 1015077 | item 12 Food × 1,100 | `Item(Money_Camp_Food,1100)` |

**Owner check, 2026-11-11: these base reward amounts match the game.** The owner adds that **bonuses are
applied on top**, so base reward is not the actual payout. The bonus source and formula are unknown. Lead:
the save's `FactionNodeElementSaveData._operationRewardRateByBuff` (TPercent), which is absent on all four
running operations in slot 103. Net-gain numbers must not be published from base rewards alone.
The Fishing record's reward block holds two more values (169977 and 11001) that are not understood;
they may be the personal-storage reward.

## Bonuses (from an owner screenshot, 2026-11-11)

Mission shown in game as **"Simple Farming"** = `Node_Her_GlenbrightFarm_Work_Food_Farm_Tier1`
(Evelyn and Terry dispatched; "Repeating mission"; "11 hr 12 min" with a progress bar).

The screen lists three bonuses and a total: Conversion +40%, Skills +40%, Comrade +0%, Total Reward "1.9K".
Base is 1,100 Food. **1,100 × (1 + 0.40 + 0.40 + 0.00) = 1,980**, shown truncated as 1.9K. A multiplicative
reading (1,100 × 1.4 × 1.4 = 2,156) would show 2.1K, so the bonuses **add**. One data point.

| Bonus | What the owner says it is | Where the input might live | Status |
|---|---|---|---|
| Conversion | Religion | Node/faction state (`_religionRange`, faction skill lists) | Not located |
| Skills | Comrade skills, e.g. "Novice Farmer I: +10% Farming mission rewards", "Skilled Farmer II: +30%", "Expert Farmer III: +60%" (locked) | Not in `MercenarySaveData` (its `_levelData` has no level or skills). Maybe static per character plus an unlock level elsewhere (e.g. `FriendlySaveData`) | Not located |
| Comrade | Extra comrades beyond the requirement | Assigned count vs the record's `N` and `M` (2 assigned, N=2, M=4 here → +0%) | Formula unknown |

Open: how skills combine across comrades. Evelyn shows Farmer I and II unlocked (10 + 30 = 40) and Terry
appears to have the same, and the screen says +40%, so it is not a sum over both comrades. Best-comrade,
average, or highest-tier-only all fit one data point.

`_operationRewardRateByBuff` in the save is absent on this node, so the bonus is not stored there.

## Comrade skills (static data)

- `skill` table: `Skill_<Type>_I/II/III` for 19 mission types (Banking, Building, Crafting, Drawing,
  Engineering, Farming, Fishing, Foodprocessing, Forging, Gathering, Logging, Mining, Ornamenting, Ranching,
  Refining (I only), Service, Smelting, Strengthening, Weaving). The u32 right after the name is the skill
  key: Farming I 66004, II 66005, III 66045.
- Each skill grants an amount of a status whose key is the same one the mission record carries next to its
  category string (Farming = 1000054, `76 42 0f 00`). Farming I grants 10,000, Farming II 30,000. The game
  shows these as +10% and +30%, so **1,000 units = 1%**.
- Matching `Knowledge_Skill_<Type>_<tier>` entries exist in `knowledgeinfo` (Farming I = 1004213).
- **Which comrade has which skill is static**, in `characterinfo`. Header `characterinfo` (the small file):
  `u16 count`, then `count × (u32 character key, u32 body offset)`. A comrade's record contains five
  per-tier blocks, each with one skill. Evelyn (`NHW_Unique_Evelyn_634`, key 1002973): Farming I, II, III in
  tiers 1, 3, 5. Terry (`NHM_Unique_Terry_668`, key 668): Farming I, II, and Engineering I in tier 5.
  This matches the owner's screenshot (Evelyn: Farmer I, Cooking I, Farmer II, Cook II, Farmer III locked).
  The cooking skills use names this search did not match; tier blocks are not cleanly delimited yet.
- **The skill keys do not appear anywhere in the save.** So the unlocked tier must come from something else.
  Not found yet. `MercenarySaveData._levelData` has no level or exp set for comrades, and they have no
  `FriendlySaveData` entry. Lead: `mercenaryinfo` has a `use_camp_level` flag, so the unlocked tier may
  simply be the camp's level. Both comrades on the screenshot have 4 of 5 unlocked, which fits.

## Sub-levels: likely inputs for Conversion and skill unlocks

Save block `SubLevelSaveData._list[]` holds `(_key, _level, _maxAchievedLevel, _experience)`. Key names come
from the `sublevelinfo` table (header: `u16 count`, then `(u32 key, u32 offset)`; each record starts with a
length-prefixed name). Values on the owner's saves:

| Key | Name | slot 101 | slot 100 | slot 103 |
|---|---|---|---|---|
| 600 | `Contribution_Graymane` | 33 | 57 | 94 |
| 601 | `Contribution_Hernandian` | 47 | 50 | 88 |
| 603 | `Contribution_Demenissian` | – | 4 | 4 |
| 1000 | `Religion_Hernand` | – | – | 20 (exp 2000) |
| 701 | `LiberationRefugee` | 29 | 34 | 45 |
| 521 | `SkillPoint_Kliff` | 15 | 18 | 31 |
| 511 | `TradeShopSellPrice` | 1 | 1 | 1 |

Hypotheses, **not verified**:
- Conversion bonus comes from the religion sub-level of the mission's region. All four active missions are
  `Node_Her_…` (Hernand); `Religion_Hernand` is level 20 and the game shows Conversion +40%. If linear, 2% per level.
- Skill slots unlock on a camp-wide level, most likely `Contribution_Graymane` (94). A second owner
  screenshot shows a comrade with six slots, four unlocked, so "four unlocked" held on all three comrades
  seen. The threshold table has not been found (the 1,359-byte `Contribution_Graymane` record is a candidate).
- Some skills are gates, not bonuses ("Allows engineering missions to be carried out"), so skills are also
  the **skill requirements** for missions.

## Second verified mission: Burnhand Quarry (owner screenshot, 2026-11-11)

Game screen "Advanced Mining" at Burnhand Quarry: Members Required 5~7, Task Duration 22 hr, Materials
270 Food + 2.5K Funds, Reward 3K Stone + Fine Stone ×5 + Iron Ore ×3, all three bonuses +0% (no comrades
selected), Total Reward 3K.

Record `Node_Dem_BurnhandQuarry_Work_Stone_Tier2`: Funds 2,500, Food 270, `N`=5, `M`=7. **So `N`..`M` is the
"Members Required" range (min..max).** Verified on this mission.

- The u16 after the digit string is a key into `factionoperationgroupinfo` (header: `u16 count`, then
  `(u16 key, u32 offset)`): 0x4240 Camp_Money, 0x4241 Camp_Food, 0x4242 Camp_Wood, 0x4243 Camp_Stone,
  0x4244 Camp_Weapon, 0x4245 Crafting, 0x4246 Explore, 0x4247 Building, 0x4248 Combat. It says which reserve
  the mission mainly produces.
- The second cost entry is not on the same 24-byte grid as the first (the first parse misread it). The
  amounts are present; the entry layout needs fixing.
- **Duration is not found.** (Superseded 2026-11-12: found 90 bytes before the operation key; see "Mission durations" below.) No 22, 1,320, 79,200 or float 22.0 in or near the Burnhand record. It may be
  derived (for example from distance to the camp) or live in another table.
- Conversion shows +0% here. Burnhand is in Demeniss and the save has no `Religion_Demenissian` level,
  while Hernand missions show +40% with `Religion_Hernand` 20. That fits the religion guess, but the screen
  had no comrades selected, so it is weak evidence.
- Dead end: the `Contribution_Graymane` record's 54 entries all carry the same constants (70, 45). It does
  not hold skill-unlock thresholds.

## Duration and clock calibration (owner, 2026-11-11)

- The owner reports South Quarry mining shows **12 hr**; Burnhand Quarry shows **22 hr**. Same category,
  tier, group and member range, so **duration depends on the location, not the mission tier**.
- **Correction (same day).** An earlier version of this note said 12 hr = 36,000,000 ticks. That was wrong.
  The owner's spreadsheet lists Glenbright Farm at 16 hr, and the game showed 11 hr 12 min remaining, which
  is exactly 70% of 16. Yet the save shows every operation gaining 1 "progress" per 360,000 ticks. So
  `_operationCurrentProgress` is **not a percentage. It is elapsed in-game hours × 10**, and
  **one tick of `_fieldTimeRaw` is one in-game millisecond** (3,600,000 ticks per in-game hour). A mission of
  D hours completes at progress 10 × D. This also explains the 103.05 seen in slot 101. Live memory showed
  about 14,400 ticks per real second, so in-game time ran about 14.4× real time (one in-game hour ≈ 250 s).
- Durations in the owner's spreadsheet (owner says it is incomplete): Oakenshield Manor 18, Hillside Manor 17,
  Nas river Fishing Dock 16, Glenbright Farm 16, Southern Quarry 12.
- A byte comparison of the Burnhand, South Quarry and Pervin Quarry records found no field that reads as
  12 vs 22. Duration is probably derived (distance from camp is the obvious candidate) or held in another
  table. **Not found.** (Superseded 2026-11-12: it is in the operation record; see "Mission durations" below.)

## Bonus rules from a public wiki (2026-11-11)

Source: thegameswiki.com "Dispatch Missions" (community wiki, updated Oct 29; read in a browser because it
blocks automated fetch). Treat as claims to test, not facts.

| Rule (wiki) | Check against our data |
|---|---|
| Conversion: each church "Blessing" level in the mission's region adds +2%. Raised by donating camp silver | **Matches**: save `Religion_Hernand` = 20, game shows +40% on Hernand missions; no Demeniss level, +0% at Burnhand |
| Skill bonus: taken from the single best comrade, not summed across comrades. Tiers 10% / 30% / 60%, adding to 100% for one comrade with all three | **Matches**: Evelyn and Terry each 10+30, game shows +40%. Tier values match the `skill` table |
| Skill tiers unlock with **camp level** (camp expansion missions), for all comrades at once | Fits "four unlocked on every comrade". Where camp level sits in the save is **not found** |
| Comrade bonus: +20% per comrade above the minimum, max +40% | Not tested. Fits 2 of 2 → +0% |
| The three bonuses multiply ("compound") | **Conflicts with the game.** Owner screenshot: 1,100 base, +40% and +40% → "1.9K". Additive gives 1,980; multiplying gives 2,156. Use additive |
| Timers run on in-game time; resting advances them | Agrees with the save clock |

**Owner, 2026-11-11: camp level is 4.** Every comrade seen has exactly four skill slots unlocked (of five or
six). Working rule: **a comrade's first `camp level` skill slots are unlocked**, in the order they appear in
the comrade's `characterinfo` record. One camp level observed, so provisional. Camp level itself has not been
located in the save; until it is, it is a user setting.

The wiki has no per-mission duration table. Other guides found by search give only examples.

## Structured layouts (2026-11-12, replaces the pattern search above)

Checked on all owner-verified missions (four Hernand missions plus Burnhand Quarry): cost, members and base
reward all match.

- **Every static table** has a header file: `u16 count`, then `count x (u32 key, u32 offset)`. The DLC
  folder `dlc/dlc_01/0008` has its own copies (`<table>.dlc_01`); same header layout; DLC records win.
  Body vs header is told apart by the header's exact size, not by the extension index.
- **Operation cost entries are 30 bytes**, not 24: `u32 item, 8 bytes, u64 amount, 10 bytes`. The earlier
  24-byte reading misread every second cost (e.g. Burnhand's 270 Food).
- **Drop-set record** (`dropsetinfo`): `u32 len + comment` (e.g. `Item(Money_Camp_Stone,3000);...`), then
  `u32 key`, 13 bytes, `u32 entry count` at key+0x11, entries of 0x40 bytes from key+0x15:
  `u8 1, u32 item, ..., u32 chance (1,000,000 = 100%) at +0x1A, u64 min at +0x2A, u64 max at +0x32,
  FF FF at +0x3A, u32 item again at +0x3C`. The repeated item key is the self-check.
- Burnhand's reward reads 3,000 Stone + Fine_Stone x5 + IronStone x3, matching the owner's screen ("3K").
- Fishing has a second drop set of seven fish items at 14-15% each: the personal-storage reward.
- 1,012 operation records; 309 with a members range and a `Node_` name. The rest are combat-style records
  without a members marker. Camp "Build" missions reward entries that are not reserves (e.g. 150 of
  key 600, which is also the `Contribution_Graymane` sub-level key; unconfirmed).
- Character records (`characterinfo`) start with a length-prefixed internal name; so do item records.

## Not done
- Rewards, duration, skill requirements, display names (localisation lookup), which missions are unlocked
  or available to the player. The save's progress rate suggested the same 36,000,000 ticks for all four;
  no per-mission duration has been identified in the record yet.
- Decode the record properly from the table header instead of pattern matching.
- DLC table.

## Mission durations (2026-11-12, game 2.04.08)

**Found in static data, not per-save state.** Each operation record in `factionnode` has its duration as a
`u32` of in-game hours x 10 (the same unit as the save's `_operationCurrentProgress`), **90 bytes before the
operation key**, with a `0x02` byte 5 bytes before the value. Because names sit at the end of each operation
record, the value looks like it trails the previous mission's name; it belongs to the next operation.

How it was found: the owner-reported durations (South Quarry stone 12 h, Burnhand Quarry stone 22 h) were
searched in their location records under common encodings (h, h x 10, min, s, ms; integer and float). Each
record had exactly one match, `u32 h x 10`: 120 and 220. Both sit 90 bytes before their operation key.

Checks:
- South Quarry stone 12 h and Burnhand Quarry stone 22 h (owner's screen, earlier session).
- Oakenshield Manor guard: 18 h in data; owner saw 15 h 10 min remaining when elapsed was about 2.8 h
  (save progress 1.94 h, later 3.94 h). 18 - 15.17 = 2.83: consistent.
- Wiki figures (thegameswiki.com, Dispatch Missions): the first camp expansion takes "roughly 17" in-game hours
  (data: ExpandCamp_Lv2 18 h); short missions about 8 h (data: 20 missions at 8 h).
- All 309 missions with a members range get a duration (2 h to 360 h; most 4-24 h).

Not checked: whether bonuses, camp level or skills shorten durations. The wiki mentions none.
Live memory was searched first and did not show the total next to the running operation; not needed now.

## Build rewards, long drop sets and item costs (2026-11-13, game 2.05.00, Steam build 26636972)

Three missions on the owner's save showed "unknown" on the dashboard although the game data has everything:

| Mission | Shown | Cause | Now |
|---|---|---|---|
| Rocca's Hill Slave Camp / St. Halssius's House of Healing Bridge Construction (role Building) | reward unknown | drop set fails its self-check | reward: no reserves (contribution XP and a x0 item) |
| Senia Lift Repair Support | reward unknown | 51 entries; the reader capped sets at 32 | reward: no reserves; 51 food items at 1-2% each to storage |
| Vellua Shipbuilding | cost unknown | cost includes QuartzOre x30 and Malachite x20 | reserve cost known; items listed separately (`itemCost`) |

All 309 dispatch operations have at least one drop set, so "no reward table" was never the cause.

**Long drop-set entries.** Entry byte +0x9 is a type. Entries of type `0x0D` are **one byte longer** (a
`01` after the trailing item key). Seen on 1015062 (Rocca's bridge: `GamePlayVariableChange(BridgeConstruction_HellwoodSlaveCamp,
True);SubLevelExp(Contribution_Hernandian,700)`): entry 1 is item 1000017, type 0x0D, x0, with its `FF FF` mark at
+0x50 and the next entry's mark at +0x92 instead of +0x91. On 1015069 (Pond) the 0x0D entry is last, which is
why the pond read correctly before. Over every `dropsetinfo` record in the standard shape (4,179 of 17,186; the
others have a different header and are not used by dispatch missions):

| Rule | Records that parse | Footer marker `73 E1 C5 EA` after the last entry |
|---|---|---|
| fixed 0x41 entries | 4,157 (22 fail at entry 1 or 2) | +24 on 4,076, +25 on 77, +30 on 4 |
| 0x0D entries one byte longer | 4,179 | +24 on 4,175, +30 on 4 |

The 77 records whose footer moved to +25 are the ones ending with a 0x0D entry: with the rule they line up too.
Kept as `dropSet.longEntry` in `offsets/2.05.00/gamedata.json`; 2.04.08 has no such entry, so a 0x0D entry
there fails its next self-check and the set stays unknown (fail closed).

Entry types seen: 0 (6,281), 4 (1,822), 1 (352), 6 (169), 13 (99), 9 (20), 3 (15), 16 (4), 5 (4). Type 6 is a
sub-level XP grant: item 600 = sub-level `Contribution_Graymane`, 601 = `Contribution_Hernandian` (matches the
comments `SubLevelExp(Contribution_Graymane,150)` and `(Contribution_Hernandian,700)`). The reader now names
these by sub-level when the item has no name, and drops x0 entries from the storage text (they give nothing).

**Owner statement, 2026-11-13:** building missions give no reserve rewards. Consistent with the game data: none
of the 72 Building-role operations' drop sets contains a reserve item (11-15); they hold sub-level XP, game-play
variable changes, x0 placeholders and single items. The dashboard labels such a reward "none (build)".
