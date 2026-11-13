# Mission regions and the church (conversion) bonus

Game **2.05.00** (Steam build 26636972). Checked 2026-11-13 with the owner's save (written 16:41 local) and the
install's `regioninfo` table, read-only. Probe: a scratch program using the reader's `Archive` and `SaveBody`
(nothing written to the game or the save; the save was read from a temp copy).

## Problem

The reader took a mission's region from its node's internal-name prefix (`Her_` Hernand, `Dem_` Demeniss,
`Kwe_` Pailune, ...). `Cal_` (Calphade) had no mapping, so every Calphade mission had conversion "unknown",
and Pailune had no church, so Beighen was "unknown" too. Owner, in game, 2026-11-13: **Calphade Logging Camp
shows Conversion +40%, Beighen (Advanced Logging) +0%.**

## Finding: regioninfo is a hierarchy that ends at a territory

Each `regioninfo` record starts `u32 name length + name`; the **u16 key of its parent region sits 13 bytes after
the name**. Found by scanning five node records for u16 values that are other records' keys: each had exactly
one, always at name end + 13. Following the links (keys as on 2.05.00):

| Node record | Chain |
|---|---|
| `Region_Node_Her_SouthQuarry` (41194) | `Region_Her_Grace` → `Region_Hernandian_Territory` (13) → `Region_Akapen` → `Region_Pywel` |
| `Region_Node_Her_GlenbrightFarm`, `_SeniaVillage` | same as above |
| `Node_Cal_CalphadeCastle` (41230) | `Region_Calphade` (10033) → `Region_Hernandian_Territory` → … |
| `Region_Node_Cal_CalphadeLoggingCamp` (41236), `_CalphadeFarm` | `Region_Calphade` → `Region_Hernandian_Territory` → … |
| `Region_Node_Kwe_BeighenVillage` (43040) | `Region_Beighen` (14030) → `Region_Pailunese_Territory` (12) → `Region_Kweiden` → `Region_Pywel` |
| `Region_Node_Dem_LacusFarm` (44082) | `Region_Azerian` → `Region_Demenissian_Territory` (14) → `Region_Delpheon` → `Region_Pywel` |
| `Region_Node_Dem_BurnhandQuarry` (44024) | `Region_Byron` → `Region_Demenissian_Territory` → … |

Territories in the table: Pailunese (12, 114 records under it), Hernandian (13, 226), Demenissian (14, 177),
Delesyian (15, 132), Tashkalpan (17, under `Region_Crimson_Desert`, 119).

Churches: `sublevelinfo` has only `Religion_Hernand`, `Religion_Demenissian`, `Religion_Delesyian`. The save holds
`Religion_Hernand` = 20 and no other church entry.

## Check against the game

| Mission | Territory (hierarchy) | Church level | Reader | Game (owner) |
|---|---|---|---|---|
| Calphade Logging Camp | Hernandian | 20 | +40% | **+40%** |
| Beighen, Advanced Logging | Pailunese | none (no church) | +0% | **+0%** |
| Hernand missions (e.g. Glenbright Farm) | Hernandian | 20 | +40% | +40% (comrade-bonuses.md) |
| Burnhand Quarry | Demenissian | no entry = 0 | +0% | +0% (comrade-bonuses.md) |

Over the whole save (6 running + 48 available): every mission whose prefix region was known gets the same region
from the hierarchy. Only the nine with no prefix region changed: seven Calphade missions and South Thalwynd house
construction → Hernand; Greymane Shrine ritual → Demeniss (reward 0, so no number changes).

The owner said Beighen is in Demeniss; the game data files it under Pailunese territory. Both have no church level
in this save, so the bonus (+0%) is the same either way. Not settled; it does not affect any number today.

## What the reader does now

- `offsets/2.05.00/gamedata.json` `regionInfo.parentAfterName` = 13. Versions without it (2.04.08) keep the prefix rule.
- `DisplayNames.Territory` follows the links (at most 12) from `Region_<node>` (or the node name); a missing record
  or link gives null and the prefix rule is used.
- `Catalogue.NodeRegion` maps territories: Hernandian → Hernand, Demenissian → Demeniss, Delesyian → Delesyia,
  Pailunese → Pailune, Tashkalpan → Crimson Desert.
- Conversion: church regions as before (level × 2%, no save entry = 0). **Pailune: 0%** (no church; Beighen +0% in
  game). **Crimson Desert (Tashkalpan): still unknown** until a mission there is checked in game.

## Still open

- A Pailune mission other than Beighen, and any Tashkalpan mission, checked in game.
- Whether Beighen is shown as Demeniss anywhere in the game UI.
- `parentAfterName` on other game versions (check on each patch; `docs/patch-update.md`).
