# Missions available to start

Game 2.04.08, Steam. 2026-11-12. **Status: rule taken from the game's own data; the owner's save gives 47
startable missions (2 unconfirmed). Not yet compared screen-for-screen with the dispatch board** (the owner
cannot see a count in game).

## The rule

A dispatch mission can be started when all of these hold:

1. It is not running now.
2. **Node not blockaded**: save node `_factionState` 2 and no `_isBlock` (state 0 always has `_isBlock`).
3. **Node discovered**: the node's discovery knowledge is in `KnowledgeSaveData._list`.
4. **Every start condition in the mission's record is true** (game expressions, below).

Faction relations act through 2 (a hostile faction blockading the node sets `_isBlock`) and through the
`CheckFactionWantedLevel()<4` condition most missions carry. As a guard, a node held (conqueror, else
blockading faction) by a faction with an explicit hostile relation to the Greymanes is marked unconfirmed.
None of the 47 is.

## Evidence

### Discovery knowledge in the node record

`factionnode` record: u32 length + node name, u32 node key, then **u32 knowledge key 15 bytes after the node
key**.

| Node | Key | Knowledge key | knowledgeinfo name | In save |
|---|---|---|---|---|
| Node_Her_GlenbrightFarm | 1000311 | 1000350 | Knowledge_Node_Her_GlenbrightFarm | level 2 |
| Node_Her_SouthQuarry | 1000070 | 1002120 | Knowledge_Node_Her_SouthQuarry | level 2 |

On the owner's save, every node with a running mission has its knowledge learned (all at level 2). Of the 186
missions at unblockaded nodes, 109 (at 80 nodes) are at nodes whose knowledge is not in the save, e.g. North
Hernand Fishing Dock, Hayscent Farm.

### Start conditions

Between an operation's key and its internal name the record holds **u32 count n, then n entries of 13 bytes,
each starting with a `conditioninfo` key**. All 309 dispatch operations in base + DLC have such a list.

`conditioninfo` record: u32 length + name, u32 key, ... Most names are the expression followed by
`, parserType:N`. Named macros (e.g. `OperationPlayable`) hold the expression as the next string after the
key. `Macro(X)` refers to the record named X. `OperationPlayable` = `CheckGamePlayVariable(BaseCamp_Lv1)`.

Examples (from the records):

| Mission | Conditions |
|---|---|
| Glenbright Farm, Simple Farming | `Macro(OperationPlayable)`, `(CheckFactionWantedLevel()<4)` |
| Howling Hill Camp Expansion (Lv2) | `Macro(OperationPlayable)`, `CheckGamePlayVariable(BaseCamp_Lv1)`, `!CheckGamePlayVariable(BaseCamp_Lv2)`, `CompleteMission(Mission_ForGraymane_ShakatuLetter)` |
| Howling Hill - Farm Built | `... CheckGamePlayVariable(BaseCamp_Lv3)`, `!CheckGamePlayVariable(BaseCamp_Farm_Lv1)`, `CompleteSubMission(Mission_GreymaneCamp_BuildFarm_Expand, 1)`, ... |
| Festival Preparations | `CheckGamePlayVariable(BaseCamp_Lv5) && !CheckGlobalGameEvent(...) && ...` |

So one-off builds disappear once done: they test their own "built" game-play variable (`!CheckGamePlayVariable(...)`).
Counts over all 309 operations: `Macro(OperationPlayable)` 309, `CheckFactionWantedLevel()<4` 218, then
`CheckKnowledge` (research), `CompleteStage`/`CompleteMission`/`CompleteQuest`, `CheckGamePlayVariable`,
`GetCurrentSubLevel`, `CheckOperationing`, `IsHiredMercenary`, `GetFactionNodeState`, and a few others.

### Facts in the save

| Function | Save source | Notes |
|---|---|---|
| `CheckGamePlayVariable(X)` | `GamePlayVariableSaveData._gamePlayVariableElementSaveData[]` (`_gamePlayVariableKey`, `_currentVariable`) | names: `gameplayvariableinfo` |
| `CheckKnowledge(X)`, `KnowledgeKey(X)` | `KnowledgeSaveData._list[]._key` | names: `knowledgeinfo` |
| `CompleteMission(X)` | `QuestSaveData._missionStateList[]`, `_state` 5 | names: `missioninfo` |
| `CompleteQuest(X)` | `QuestSaveData._questStateList[]` (`_questKey`), `_state` 5 | names: `questinfo` |
| `CompleteStage(X)` | `QuestSaveData._stageStateData[]`, `_state` 5 | names: `stageinfo` |
| `CompleteSubMission(X, n)` | true if mission X is complete, false if the save has no entry for it, else **unknown** | sub-mission progress not decoded |
| `GetCurrentSubLevel(X)` | `SubLevelSaveData._list[]`; missing = 0 | names: `sublevelinfo` |
| `IsHiredMercenary(X)`, `GetMercenaryHiredCount(X)` | `MercenaryClanSaveData._mercenaryDataList[]._characterKey` | names: `characterinfo` |
| `GetFactionNodeState(X)==Normal/Block` | node `_factionState` 2 without `_isBlock` = Normal; 0 with `_isBlock` = Block | mapping inferred from the counts above |
| `CheckOperationing(node, op)` | node `_operationKey` | |
| `CheckFactionManagementRent(...)` | false while the player faction has no `_factionManagementSaveDataList` entries (no faction has any on the owner's save) | otherwise unknown |
| `CheckFactionWantedLevel()<n` | **not in the save** (no wanted/crime field in the schema). Assumed true and listed in the mission's `assumptions` | live memory could settle it |
| anything else (`CheckGlobalGameEvent`, `QuestGaugePercent`, ...) | unknown | |

State 5 = completed: entries with state 5 carry `_completedTime`; state 2 entries do not.

`questinfo` and `stageinfo` index headers use a **u32** entry count (e.g. questinfo 9,388 bytes = 4 + 8 x 1,173);
other tables use u16. The table loader detects both.

### Camp level

`BaseCamp_Lv0` ... `BaseCamp_Lv4` are set on the owner's save, `BaseCamp_Lv5` is not: camp level 4, as the owner
reported. Completing an expansion sets the next one (dropset of the Lv3 build:
`GamePlayVariableChange(BaseCamp_Lv3, True)`). Also set: `BaseCamp_Farm_Lv1`, `BaseCamp_Ranch_Lv1`,
`BaseCamp_InsectStorage`; not set: `BaseCamp_GraymanePond` (the pond build is running). The snapshot carries
this as `campLevel`.

### Faction relations

Player faction 1000000 (`Faction_Graymane`), `_factionRelationDataList`:

| Relation | Factions |
|---|---|
| `_relationTypeRaw` 3 | Roberts, Serkis, GoldleafGuildhouse, PororinVillage, ShieldLanford, Celeste_Hernand |
| 2 | Alfonso |
| 0 (field omitted) | Marchell, Byron, Elimoor, SilvermoonTroll, Thorel_Demeniss |

Factions not listed fall back to relation groups (`factionrelationgroup`, not decoded). A missing entry is not
hostile: Hillside Manor (conqueror Felix, no entry) is a mission the owner runs.

## Result on the owner's save (2026-11-12, game running)

47 startable missions (6 running): 32 in Hernand, 6 in Demeniss, 9 with no region prefix (7 at Calphade, a
Greymane Shrine ritual, a Thalwynd house construction). Every node with a running mission is discovered and not
blockaded; Calphade Logging Camp, the one sheet mission not running, is listed. Two are unconfirmed: the Rocca's Hill Slave Camp and St. Halssius's House of Healing bridge
constructions (`CompleteSubMission(..._BridgeConstruction_Start, 0)` with the mission in progress).

Rules tried before (superseded):

1. Node held by the player (blockading or conqueror 1000000): 61. Wrong (left out Oakenshield, Nas River).
2. Node not blockaded only: 181. Too many (undiscovered nodes, finished one-off builds).

## Still open

- Sub-mission progress (two bridge constructions).
- Wanted level (live memory only).
- Default relations from `factionrelationgroup`.
- Gate skills (Building, Drawing, ...) that the game says "allow" missions: not a start condition in the
  records; probably needed to assign comrades.
