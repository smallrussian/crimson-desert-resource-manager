# Camp Ledger

A read-only companion for the PC version of **Crimson Desert** that keeps track of your
Greymane Camp: reserves, dispatch missions and workers. It works out what each mission and
your whole set of active missions actually earns you per cycle, per hour and per day, so
you don't have to keep a spreadsheet by hand.

> **Status: early, pre-release.** The save reader and the calculation core work on game
> version **2.04.08** (Steam). The web app, account login and sync are not built yet.
> There are no release downloads yet; you have to build from source.

## What it does

- **Reads your camp from the game's save.** Food, Funds, Stone, Timber and Armaments, the
  missions that are running, who is assigned to them, and your camp level.
- **Fills in mission details from the game's own data.** English names, region, duration,
  cost, base reward, extra item drops and their chance, and the comrade skill bonus inputs.
- **Lists the missions you could start**, using the game's own start conditions (node
  discovered, not blockaded, quest and camp-level requirements). Anything the save can't
  settle is marked as unconfirmed rather than guessed.
- **Calculates net gain.** Net per cycle, per hour and per day for each reserve, worker
  usage (owned, in use, free), and an upgrade planner that tells you the shortfall and how
  long it will take to afford an upgrade at your current net.
- **Records automatically while you play.** Started with the game, it writes a new snapshot
  each time the game autosaves (about every 8 to 12 minutes) and exits when the game closes.

If a value can't be read, Camp Ledger says "unknown". It never fills in a guessed number.

## Safety

Camp Ledger is built to stay out of the game's way.

- **Read-only.** It never writes to the game's memory, save files or install folder. Saves
  are copied to a temp file, the copy is read, and the copy is deleted.
- **No anti-tamper bypass.** It doesn't patch, disable or work around Denuvo.
- **Offline play only.** It doesn't touch any online features.
- **No game passwords.** It never asks for or stores a Steam or other platform login.
- **Optional launcher.** To start automatically with the game, a tiny launcher (`.asi`) can
  be placed in the game folder with an ASI loader. Its only job is to start the reader. It
  doesn't read game memory, hook anything or touch saves. You can skip it and run the reader
  yourself.

Reading another program's files or memory is never risk-free, and no official statement on
ban risk exists. Use it in offline play at your own risk.

## Getting started

### Requirements

- Windows 10 or 11, Crimson Desert on Steam, game version **2.04.08**
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- GNU make and a POSIX shell (Git for Windows' Git Bash works)
- [Zig](https://ziglang.org/) 0.17+ (only for the launcher: `winget install zig.zig`)

`make check-tools` reports what is installed.

### Build

```bash
make
```

| Command | Output |
|---|---|
| `make reader` | `dist/reader/CampLedgerReader.exe` |
| `make launcher` | `dist/launcher/CampLedgerLauncher.asi` and `.ini` |
| `make test` | runs every test project (no game needed) |
| `make clean` | removes `dist/` and build folders |

### Use the reader

```bash
dist/reader/CampLedgerReader.exe snapshot
```

| Command | What it does |
|---|---|
| `snapshot` | prints one snapshot from your newest save as JSON |
| `snapshot <file>` | same, from a given save file (a copy is read) |
| `watch` | waits for the game, records while it runs, exits when it closes |
| `watch --stay` | same, but goes back to waiting after the game closes |

Snapshots are appended to `%LOCALAPPDATA%\CampLedger\snapshots.jsonl`, and the reader logs to
`%LOCALAPPDATA%\CampLedger\reader.log`.

### Start it with the game (optional)

1. Get the [Ultimate ASI Loader](https://github.com/ThirteenAG/Ultimate-ASI-Loader) (x64) and
   put it in `<game install>\bin64` as `winmm.dll`.
2. Copy `dist/launcher/CampLedgerLauncher.asi` and `CampLedgerLauncher.ini` into the same
   folder. The ini holds one line: the full path to `CampLedgerReader.exe`.
3. Start the game. The reader starts with it and stops when the game closes.

To remove it, delete those three files. See [launcher/README.md](launcher/README.md).

## Roadmap

| | Milestone | Status |
|---|---|---|
| 1 | Feasibility: is camp state in the save? | Done ([notes](docs/re-notes/save-contents.md)) |
| 2 | Save reader prints a snapshot | Working; checking values against the game UI |
| 3 | Calc core: net, per hour, per day, upgrade planner | Done, with tests |
| 4 | Web app: accounts, snapshot upload, dashboard | Not started |
| 5 | Device login and sync from the reader | Not started |
| 6 | History: predicted vs actual change between snapshots | Not started |
| 7 | Live memory reader (only if saves aren't enough) | Research only |

The planned web app will also import and export the original spreadsheet layout.

## Game updates

Patches change the save format and game data, so each game version has to be checked before
the reader trusts it. On a version it doesn't know, the reader reports that and stops
instead of guessing. Expect a delay after each game patch.

## Repository layout

```
reader/      the reader (C# / .NET 8): save decoding, game data, snapshots
core/        calculation core (pure C#, no I/O) and its tests
launcher/    the launcher-only ASI (C, built with Zig)
offsets/     per-game-version layout data
docs/        architecture decisions and research notes (docs/re-notes/)
tools/       research scripts (Python) used to work out the save format
fixtures/    small hand-written test data
```

## Contributing and research

Every claim about where data lives in the save or in memory has a write-up in
[docs/re-notes/](docs/re-notes/), with how it was found and which game version it was
checked on. Architecture decisions are in [docs/architecture.md](docs/architecture.md),
and the project rules for contributors (human or AI) are in [CLAUDE.md](CLAUDE.md).

The repository contains no game files, extracted assets or real save files. Game data is
read from your own install at run time and is never written to disk.

## Credits

The save format work builds on public research by
[NattKh](https://github.com/NattKh/CRIMSON-DESERT-SAVE-EDITOR-AND-GAME-MODS) and
[bbfox0703](https://github.com/bbfox0703/crimsonatomtic). Their code was read, not run;
the decoding here is a fresh implementation.

## Disclaimer

Camp Ledger is a fan project. It isn't affiliated with or endorsed by Pearl Abyss.
Crimson Desert is a trademark of Pearl Abyss.
