# Missions available to start

Game 2.04.08, Steam. 2026-11-12. **Status: candidate rule, not yet checked against the game's dispatch board.**

## What the save holds per node

`FactionSaveData._factionNodeElementSaveDataList` (1,260 nodes). Field counts on the owner's newest save:

| Field | Nodes | Values |
|---|---|---|
| `_factionState` | 1,260 | 2 on 843, 0 on 417 |
| `_isBlock` | 417 | always True; exactly the state-0 nodes |
| `_blockadingFactionKey` | 533 | 1000000 on 129, other factions on the rest |
| `_conquerorFactionKey` | 137 | various |
| `_operationKey` etc. | 5 | the running missions |

Faction 1000000 is the player's (Greymanes): its `_factionElementSaveDataList` entry has
`_leaderCharacterKey = 1`.

## Rules tried

1. **Node held by the player** (`_factionState` 2, no `_isBlock`, blockading or conqueror faction 1000000):
   61 missions. **Wrong**: it leaves out missions the owner is running. Oakenshield Manor has
   `_conquerorFactionKey = 1000002`; Nas River Fishing Dock has neither field.
2. **Node not blockaded** (`_factionState` 2, no `_isBlock`): 186 missions at 121 nodes, 181 once the 5
   running ones are removed. Includes every mission in the owner's sheet. **This is what the reader uses**
   (`availableMissions`), labelled as a candidate list.

## Known gaps

- One-off missions (camp expansions, constructions) all sit on shared nodes; e.g. every Howling Hill Camp
  build is on node 1000003, which only records the build running now. Finished builds are not told apart, so
  they can appear as available. Where completion is stored is unknown (quest data is the next place to look).
- Unlock conditions beyond the node (camp level, research `_researchDataKey`, quests) are not applied.
- Gate skills (Building, Drawing, Ornamenting, Refining: "allows ... missions") are not checked against the
  comrades the owner has.
- Comrades are not assigned on candidates, so their skill and comrade bonuses are unknown until chosen.

## Next check (owner)

Compare the game's dispatch board with the reader's list for one region (Hernand: 66 candidates), noting
missions the reader lists that the game does not offer, and the reverse.
