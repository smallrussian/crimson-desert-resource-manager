# Mission reward bonuses: inputs and formula

Game 2.04.08, Steam. Checked 2026-11-12. Builds on `mission-static-data.md` ("Bonuses", "Comrade skills",
"Bonus rules from a public wiki").

## Formula (unchanged)

`reward = base x (1 + conversion + skill + comrade)`. The parts **add** (owner screenshot: 1,100 Food base,
+40% and +40% shown as "1.9K" = 1,980; multiplying would give 2,156 = "2.1K").

- **Conversion** = church blessing level of the mission's region x 2%.
- **Skill** = the best single assigned comrade's total of matching skills in their unlocked slots.
- **Comrade** = 20% per comrade above the mission's minimum, at most 40%.

Implemented in `core/CampLedger.Core/Bonus.cs`. Rounding: down to a whole number; not confirmed (every case
seen divides exactly).

## Inputs and where the reader gets them

| Input | Source | Notes |
|---|---|---|
| Church level | Save `SubLevelSaveData._list[]` (`_key`, `_level`), names from `sublevelinfo` | `Religion_Hernand` (1000), `Religion_Demenissian` (1001), `Religion_Delesyian` (1002). No save entry = level 0 (Burnhand, Demeniss: +0% in game). Regions without a church sub-level (Pailune, Crimson Desert, Calphade-prefixed nodes): conversion unknown |
| Mission's region | Node internal-name prefix (`Her`, `Dem`, `Del`, …) | As before |
| Mission's skill type | `factionnode` operation record: the u32 right after the category string, e.g. `Farming` then `76 42 0f 00` = 1000054 | Matched to the skill whose status key is the same |
| Skill status and percent | `skill` table, `Skill_<Type>_I/II/III` | See below |
| Assigned comrades | Save node `_operatorMercenaryList[]._mercenaryNo` → `MercenaryClanSaveData._mercenaryDataList[]` (`_mercenaryNo`, `_characterKey`) | |
| Comrade skill slots | `characterinfo` record of the character (DLC copy wins) | See below |
| Camp level | Save game-play variables `BaseCamp_Lv0`..`Lv5`: highest set (owner: 4, matches). See `available-missions.md` | Slots unlocked = camp level |
| Comrade count and minimum | Save worker count; record's members min | As before |

### Skill records

Each reward skill record holds exactly one pair `u32 status key` (1,000,000–1,100,000), `u32 amount`
(multiple of 1,000; 1,000 = 1%). It is 178 bytes after the name on most records, but 171 (Foodprocessing I,
Service I, Smelting I) or 181 (Forging I–III) on others, so the reader searches the record for it. All 48
reward skills in base + DLC have exactly one pair and come out as 10% / 30% / 60% for tiers I / II / III.

Gate skills (Building, Drawing, Ornamenting, Refining) have no pair; the game describes them as allowing
missions. They are kept with 0%.

### Skill slots in `characterinfo`

A comrade record has five slot blocks about 1,504 bytes apart (1,502–1,510 seen). Each block contains a run of
16-byte entries starting `FF FF FF FF`, 8 zero bytes, `u32 100000`. **The slot's skill key is 696 bytes
before the first entry of the run.** Value 0 means no skill.

Checked on all 59 unique comrades whose records have this layout: every slot is 0 or a `Skill_*` key.

**The DLC copy of `characterinfo` must win.** The base copy has 0 in some slots that the DLC copy fills:

| Comrade | Base copy | DLC copy (used) | Owner's screen |
|---|---|---|---|
| Evelyn | Farming I, 0, Farming II, 0, Farming III | Farming I, Food Processing I, Farming II, Food Processing II, Farming III | Farmer I, Cooking I, Farmer II, Cook II, Farmer III (locked) |
| Terry | Farming I, Farming II, 0, 0, Engineering I | Farming I, Farming II, Crafting I, Food Processing I, Engineering I | Farming I and II seen |

So the game's "Cooking" skills are `Skill_Foodprocessing_*`.

Comrade display names: English `character` string table, key `(character key << 32) | 0x30` (Evelyn, Terry).

## Verified against the game

Glenbright Farm "Simple Farming", owner's save 2026-11-12, camp level 4: the reader gives conversion 40
(`Religion_Hernand` 20), skill type Farming, Evelyn's first four slots Farming 10 + 30 = 40, Terry's
Farming 10 + 30 = 40, best = 40, comrades 2 of minimum 2 = 0. Total 80%: 1,100 → 1,980. The game screen showed
Conversion +40%, Skills +40%, Comrade +0%, Total 1.9K.

Other running missions on the same save (not yet checked against the screen): Oakenshield (Strengthening;
Brant 10+30, Matilda 10+30 → 40), Nas River Fishing Dock (Fishing; Laurent 10+30, Lars 10+30 → 40), Southern
Quarry (Mining; Duncan 10+30 → 40).

## Still open

- Whether the bonus also applies to the personal-storage item rewards (the reader applies nothing to them).
- Whether Howling Hill Camp builds (no skill type, Pailune) take any bonus.
