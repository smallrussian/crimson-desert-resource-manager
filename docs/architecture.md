# Architecture decisions

## Reader: C# on .NET 8 (decided 2026-11-11)

**Decision.** The reader is a C# console program, `reader/CampLedger.Reader`, targeting `net8.0-windows`.
No NuGet dependencies: ChaCha20 and LZ4 block decompression are implemented in `SaveCrypto.cs`.

**Why.** The owner asked for .NET. It gives a single Windows executable, fast memory scans when tier 2 is
ready, and direct access to the Windows process APIs. The research scripts in `tools/re/` stay in Python.

**Not chosen.** Python (quickest, but slower scans and needs packaging). A full in-game ASI reader (tier 3):
not approved. Only the launcher-only ASI below is.

## Reader shape

- `ISource.Read()` returns a `Snapshot` or `Unavailable(reason)`. Two sources:
  - `SaveSource` (tier 1): copies the newest `save.save` to a temp file, decodes the copy, deletes the copy.
    Fields are looked up by name from the save's own schema.
  - `MemorySource` (tier 2): **placeholder that always returns `Unavailable`.** The reserve signature found
    all five reserves in one game session and only one in the next (see `docs/re-notes/live-memory.md`).
- `watch`: waits for `CrimsonDesert.exe`, emits a snapshot each time a newer save appears, and exits when
  the game closes. `watch --stay` goes back to waiting instead of exiting.
- **Starting with the game: launcher-only ASI** (owner decision, 2026-11-11; AGENTS.md hard rule 1 was
  amended for it). `launcher/CampLedgerLauncher.c` is a small native DLL. An ASI loader loads it into the
  game; it reads the reader's path from `CampLedgerLauncher.ini` next to itself and starts
  `CampLedgerReader.exe watch`. It does nothing else: no memory access, no hooks, no saves. The reader exits
  when the game closes, and a named mutex keeps it to one watcher. A Windows sign-in entry and a Steam
  launch-option wrapper were both considered and rejected by the owner.
  **Status: source written, not compiled, not installed.** There is no C compiler on this machine and no
  ASI loader in the game folder.
- Output: JSON lines in `%LOCALAPPDATA%\CampLedger\snapshots.jsonl`. This is a stand-in for the sync client's
  SQLite queue, which is not built.

## Freshness

With only the save source, a snapshot is as new as the last save. On 2026-11-11 the game wrote a save about
every 8 to 12 minutes during play (rotating `slot0`–`slot2`), so that is the current update interval.

## Snapshot fields beyond AGENTS.md's model

`missions[].nodeKey`, `operationKey`, `elapsedHours`, `repeat`, and top-level `unknowns`. These are game
state identifiers and a plain list of what could not be read; no user data.

## Undecided

Sync client, web app, hosting, auth provider, and the typed API contract.

## Game data in the reader (2026-11-12)

`Archive` (GameData.cs) reads the install's data index and tables read-only; `Catalogue` parses mission,
drop-set, item and character records (base and DLC copies, DLC wins). Nothing extracted is written to disk.
Snapshots now carry, per running mission: internal name, category, role, region, members min/max, cost,
base reward, and extra item rewards with their chance. `workers.total` uses the dispatchable-comrade rule
(always excluding Marius). Loading the game data takes well under a second.

Still null at the time: mission duration (since found), display names and bonus-included rewards (since
added; see below).

## Names, bonuses and available missions (2026-11-12)

- **English names.** The archive reader also indexes the small language groups (`0019`-`0033`; English is
  `0020`) and decrypts string tables (ChaCha20 keyed by file name; `EntryCrypto`, `StringTable` in
  `Localisation.cs`). Missions get `type` (e.g. "Hasty Mining"), `location` ("Southern Quarry") and `name`
  (location, else type); the internal name moved to `internalName`. Evidence: `docs/re-notes/localisation.md`.
- **Bonus inputs.** Each mission carries `bonus`: conversion percent, skill type and the assigned comrades'
  skill slots. The total is computed in the calc core (`core/CampLedger.Core/Bonus.cs`) with the camp
  level, which the snapshot now carries as `campLevel` (read from the save; see below). Evidence:
  `docs/re-notes/comrade-bonuses.md`.
- **Available missions.** `availableMissions` applies the game's own start rules: node discovered (its
  `Knowledge_Node_` knowledge learned), node not blockaded, and every condition in the mission's record
  (`conditioninfo` expressions such as `CheckGamePlayVariable(BaseCamp_Lv3)`, `CompleteQuest(...)`) true.
  `ConditionEvaluator` (`Conditions.cs`) uses three-valued logic: anything the save cannot settle stays on
  the list with `unconfirmed` reasons; assumptions (wanted level) are listed in `assumptions`. Evidence:
  `docs/re-notes/available-missions.md`.
- Snapshot and game-data load take about 0.6 s together.

## Tests

`reader/CampLedger.Reader.Tests` (xUnit). Hand-built byte fixtures for LZ4, the save body decoder, mission
records and drop sets; no game files needed. Run: `dotnet test reader/CampLedger.Reader.Tests`.
Dependencies added for tests only: xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk (the standard
.NET test runner).

## Files placed in the game folder (launcher-only exception)

Staged in `launcher/build/install/` on 2026-11-11, to be copied into `<install>\bin64` by the owner:

| File | What it is |
|---|---|
| `winmm.dll` | Ultimate ASI Loader v10.0.2 (ThirteenAG), x64, from the official GitHub release asset `Ultimate-ASI-Loader-NoPDB_x64.zip` (902,143 bytes, SHA-256 `fe964adf…afa2f`, matching GitHub's published digest). The zip's `dinput8.dll` renamed to `winmm.dll`; DLL SHA-256 `57ec5639…8a12` |
| `CampLedgerLauncher.asi` | Built from `launcher/CampLedgerLauncher.c` |
| `CampLedgerLauncher.ini` | One line: the full path to `CampLedgerReader.exe` |

To undo: delete those three files from `bin64`.

**Checked 2026-11-11 20:35.** The owner copied the files and restarted the game. The game started normally.
`CampLedgerReader.exe` was created in the same second as `CrimsonDesert.exe`, with the game as its parent
process, so the loader and launcher work. **But that reader wrote no snapshot.** Run by hand a minute later,
the same build read the save fine, so the first read most likely failed while the game was still starting
(for example the save file being open), and the reader then waited for a newer save instead of retrying.
Fixed the same evening: a failed read is retried, and the reader now logs to
`%LOCALAPPDATA%\CampLedger
eader.log`. **The fix has not yet been through a real game launch.**
Also still to confirm: the reader exits when the game closes when started by the launcher.

## Calc core and web app: C# / ASP.NET Core (decided 2026-11-12)

**Decision.** The owner chose C# everywhere. The calc core is `core/CampLedger.Core`, a plain `net8.0` class
library with no I/O and no dependencies, so the reader, a future sync client and an ASP.NET Core web app can
all reference it and share types. Tests: `core/CampLedger.Core.Tests` (xUnit), run with
`dotnet test core/CampLedger.Core.Tests`.

**Why.** One language with the reader; nothing written twice; the owner already debugs this code.

**Still undecided:** hosting and auth provider (ask the owner before milestone 4).

**Definitions** are copied from the owner's Google Sheet (tabs Missions, Portfolio, Upgrade Planner), read
2026-11-12. One deliberate difference: the sheet treats a mission with a blank duration as 0 per hour, which
quietly drops it from the per-hour totals. AGENTS.md says a missing duration means unknown, so the core
returns null for any reserve that such an active mission changes, and names the mission.
