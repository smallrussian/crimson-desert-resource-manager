# Updating Camp Ledger for a new game patch

Do this every time Crimson Desert updates. One branch per game version (`game-<version>`, e.g. `game-2.05.00`).
Work through the steps in order and stop at the first one that fails. A full run where nothing moved takes about
15 minutes. Worked example: [`re-notes/patch-2.05.00.md`](re-notes/patch-2.05.00.md).

## Why this is needed

The reader refuses to guess (AGENTS.md hard rule 6). For an unknown version it reports "unknown":

- **No `offsets/<version>/gamedata.json`**: no game data is loaded, so there are no mission names, costs, durations,
  rewards or comrade skills. Save values (reserves, workers, active missions) still come through.
- **No `offsets/<version>/memory.json`**: no live memory, only save snapshots.

Patches usually rebuild the exe, which moves the memory class RVAs. Sometimes they add fields to game-data records.
In 2.05.00 that broke rewards and skill slots. The save format is read by field name and has held so far.

## Rules while doing this (from AGENTS.md)

- The **owner** starts the game and presses **Continue**. Never load a manual save. **Never save.**
- Everything is read-only: save *copies*, game data opened read-only, process opened with query + VM-read only.
- Do not touch Denuvo. If the read-only open is refused, stop and report.
- Nothing extracted from the game goes in the repo. Notes and small hand-written fixtures only.
- The running reader (started by the launcher ASI) locks `reader/CampLedger.Reader/bin/Release`. Build and run
  everything from **Debug** while the game is open (`tools/probe/bin/Debug/net8.0-windows/` holds both the probe
  and the reader). Rebuild Release after the game closes.

## Step 0: branch, version, baseline

```bash
git checkout -b game-<version>
```

1. Read the version: `meta/0.paver` in the install folder, first 6 bytes = three u16 (e.g. `02 00 05 00 00 00` =
   2.05.00). Record the Steam build id from `steamapps/appmanifest_3321460.acf` (`buildid`).
2. Baseline: the last snapshot from the **previous** version in the snapshot log (`%LOCALAPPDATA%\CampLedger\snapshots.jsonl`).
   Note: the Claude desktop app sees a private copy of AppData. The game-launched reader's log may only be visible
   if the owner copies it out.
   If the owner has not saved since the patch, the save holds the same data, so every value must match exactly.

## Step 1: build the tools (Debug)

```bash
dotnet build tools/probe -c Debug
```

`D=tools/probe/bin/Debug/net8.0-windows` below.

## Step 2: save reader

```bash
$D/CampLedgerReader.exe snapshot > snap-new.json
```

Expect a JSON snapshot with the new `gameVersion`. Compare with the baseline:

- `reserves`, `workers`, `campLevel`, `missions[].active/workers/elapsedHours/repeat`, `comrades` (names).
- If the command fails or fields are missing: the save schema changed. Start from `re-notes/save-contents.md`
  (the body has its own schema, so look for renamed fields). Changes go in `SaveSource.cs`, with a re-note.

Without a `gamedata.json` for the new version the snapshot has no game-data fields. Go on to step 3.

## Step 3: game data (names, costs, durations, rewards, skills)

Start from the previous version's layout:

```bash
mkdir offsets/<version>
cp offsets/<previous>/gamedata.json offsets/<version>/gamedata.json   # then set gameVersion, checked, evidence
dotnet build tools/probe -c Debug
$D/CampLedgerReader.exe snapshot > snap-new.json
```

Compare against the baseline, field by field, for every mission in `missions` and `availableMissions` and every
comrade:

```bash
python tools/re/compare_snapshots.py snap-new.json <baseline.json or snapshots.jsonl> --version <previous>
```

It prints every difference in save state, static mission data and skill slots, plus any new `unknowns`.
What to look at, and what each failure looks like:

| Data | Read by | Failure looks like | Where to look |
|---|---|---|---|
| reward | `Catalogue.DropSet`, `gamedata.json` `dropSet` | `unknowns`: "reward table N did not validate" | `cdprobe rec dropsetinfo <key> 0 200`, `cdprobe multidrop`; compare record size per item count (see below) |
| skill slots | `Catalogue.ParseSkillSlots`, `gamedata.json` `skillSlots` | comrades' `slots` all null | `cdprobe recbin characterinfo <key> out.bin` + `cdprobe campskills`; find the distance from each slot marker back to a camp skill key |
| cost, members, category | `Catalogue.ParseOperation` (code) | missions vanish or costs differ | `cdprobe rec factionnode <node>`; `mission-static-data.md` |
| duration | `Catalogue.Duration` (code, 90 bytes before key) | `durationHours` null | `cdprobe static <name>=<hours>`; `mission-static-data.md` |
| node knowledge, conditions | `Catalogue.NodeKnowledgeKey`, `ConditionKeys` (code) | available-mission count changes | `available-missions.md` |
| English names | `DisplayNames`, `Localisation` (code) | names null or wrong | `localisation.md` |
| skill percent | `Catalogue.ParseCampSkill` (code) | `cdprobe campskills` shows other percents | `comrade-bonuses.md` |

A changed **value** is not necessarily a reader bug: the patch may have rebalanced a mission or renamed it.
Check the record's own text (drop sets carry an `Item(Money_Camp_Food,1100)` comment) or ask the owner to read the
value off the screen. Write down every value change in the re-note.

**Finding a moved layout (drop sets as the example).** For each item count, the record size after the comments
grows by one entry size. So take records with 1, 2 and 3 items and subtract to get the entry size. Then find the
item key, `FF FF` mark and amounts inside one entry, and confirm against the comment string on all three. If a
record layout that is still in code moves, move it into `gamedata.json` (add a field to `GameDataLayout` in
`reader/CampLedger.Reader/Offsets.cs`) instead of changing the number in code. Then older versions keep working.

## Step 4: live memory (reserves)

The game must be in the world with the camp loaded. Reserve objects appear a few minutes after load. Before that a
scan finds none or only some.

```bash
$D/cdprobe.exe verify offsets/<previous>/memory.json        # usually 0 of 5 after a patch: RVAs moved
$D/cdprobe.exe relocate offsets/<previous>/memory.json funds=<n> food=<n> stone=<n> timber=<n> armaments=<n>
```

Use the amounts from the step 2 snapshot (or from the screen). `relocate` searches for the object **shape**
(header, amount, zero, key 11-15 at the previous offsets), ignoring the old RVAs. It follows the owner route
(item `+0x28` → owner, owner `+0x28` → root) and groups the hits by their three class pointers. The good group:

- is marked `<- full route` (all three classes inside the exe),
- covers keys 11-15, with one `balance` object per reserve whose amount shows `= expected`.

It prints `candidate: classRva ... ownerClassRva ... rootClassRva ...` when exactly one group has a balance for all
five reserves. If one reserve has only a placeholder (it happened with funds on 2.05.00), wait a minute and run it
again, or run `verify` with the new file.

```bash
cp offsets/<previous>/memory.json offsets/<version>/memory.json   # set the three RVAs, gameVersion, checked, evidence, status
dotnet build tools/probe -c Debug                                  # copies offsets/ next to the tools
$D/cdprobe.exe verify offsets/<version>/memory.json                # must say: located 5 of 5, values = screen/save
$D/CampLedgerReader.exe memory                                     # same, picking the file by detected version
```

**If `relocate` finds no full-route group**, the object layout itself changed:

1. `cdprobe val <funds amount>` lists every u64 equal to the funds value, with neighbours.
2. Look for a hit with `01000000 01000000` 8 bytes before and the item key 12 bytes after. Walk back to the object
   start, where a qword points into the exe (`cdprobe dump <addr> 256 64`).
3. Re-derive `headerOffset`, `amountOffset`, `keyOffset`, `parentOffset` and the balance marker
   (`balanceListOffset`: non-empty list on the balance, zeros on pending changes). Follow `live-memory.md`
   "Item object layout and owner chain", then rerun `relocate` with the new offsets.

There is no static pointer chain to the reserves (none within two levels on 2.04.08). The class-pointer scan plus
owner route **is** the route, so it is what has to be re-found each patch.

## Step 5: tests

```bash
dotnet test reader/CampLedger.Reader.Tests -c Debug
```

`OffsetsTests` checks that every `offsets/<version>/` folder has both files and names its own version. It also
checks that each file's `evidence` points at a re-note that exists. If a layout moved, add a fixture test in
`ReaderTests.cs` with the new layout. Add a second test showing that the old layout on new data gives *unknown*,
not wrong numbers (see the 2.05.00 drop-set and skill-slot tests).

## Step 6: write it up (same day)

Create `docs/re-notes/patch-<version>.md` using the 2.05.00 note as the template:

- version, build id, date, pid, and who launched the game;
- a table of every data source: held / moved / changed value;
- for each moved layout: the symptom, how it was found (commands), old vs new offsets, and what it was checked on;
- **Not yet confirmed**: second session (restart), a live reserve change, a launcher session, any value changes
  the owner still needs to check on screen.

Both JSON files' `evidence` must point at it.

## Step 7: after the game closes

1. `dotnet build reader/CampLedger.Reader -c Release` (the launcher ASI runs this build; see
   `bin64/CampLedgerLauncher.ini`).
2. Next game session: check the reader log for `game data loaded: N missions`, with no
   `no game-data layout` or `live memory unavailable` lines. Check that a `source: process` snapshot appears.
3. Confirm the memory RVAs after this restart (second session) and update the re-note and the `status` in
   `memory.json`.
4. Commit on the version branch (one concern per commit: tools, reader, offsets + note), then open a PR to `main`.

## Checklist

- [ ] Branch `game-<version>`; version and build id recorded
- [ ] Save snapshot matches the baseline
- [ ] `offsets/<version>/gamedata.json`; every mission and comrade matches the baseline, or the difference is explained
- [ ] `offsets/<version>/memory.json`; `verify` gives 5 of 5 equal to save/screen
- [ ] Tests pass
- [ ] `docs/re-notes/patch-<version>.md` written
- [ ] Release rebuilt after the game closed; launcher session checked
- [ ] Second session confirmed (memory RVAs after restart, a live change)
