# Camp Ledger

A read-only companion for the PC version of **Crimson Desert** that keeps track of your
Greymane Camp: reserves, dispatch missions and comrades. It shows what each mission and
your whole set of running missions actually earns you per cycle, per hour and per day, in a
dashboard in your browser, so you don't have to keep a spreadsheet by hand.

Everything runs on your PC. There's no account, no server, and nothing leaves your machine.

> **Status: early, pre-release.** Works on game version **2.04.08** (Steam). There are no
> release downloads yet; you have to build from source.

## What it does

- **Reads your camp while you play.** Reserves (Food, Funds, Stone, Timber, Armaments) are
  read live from the game when it can find them, otherwise from the latest save. Missions, comrades and camp level come from the game's save and
  update each time the game saves.
- **Fills in mission details from the game's own data.** English names, region, duration,
  cost, reward with your bonuses included, and extra item drops with their chance.
- **Shows it all in a dashboard** at `http://localhost:5240`:

| Tab | What you get |
|---|---|
| Reserves (top) | Balance, net per hour and per day, and hours until a reserve runs out |
| Missions | Running missions with cost, reward and net; one-off builds listed apart |
| Available | Missions you could start now, using the game's own start conditions. Search, filter by region and by what they produce, sort by best net per hour |
| Comrades | Who is free or busy, their skill slots, and which slots your camp level has unlocked |
| Plan | What-if: add available missions with chosen comrades or stop running ones, and see per-hour and per-day net before and after, plus problems like busy comrades or a negative Food or Funds budget |
| Upgrade | Enter an upgrade's cost to see the shortfall and how long until you can afford it |
| History | Reserves over time from past snapshots |

If something can't be read, Camp Ledger says so ("What couldn't be read" at the bottom of
the page). It never fills in a guessed number. "Per day" uses a hours-played-per-day setting
you choose (default 24), labelled as an assumption.

## Safety

- **Read-only.** Camp Ledger never writes to the game's memory, save files or install folder.
  Saves are copied to a temp file, the copy is read, and the copy is deleted. Live reserves
  are read from outside the game with Windows' standard read-only process access; nothing is
  injected into the game.
- **No anti-tamper bypass.** It doesn't patch, disable or work around Denuvo.
- **Offline play only.** It doesn't touch any online features.
- **No game passwords.** It never asks for or stores a Steam or other platform login.
- **Local only.** The dashboard only answers on `localhost` from your own PC, and nothing is
  sent anywhere.
- **Optional launcher.** To start automatically with the game, a tiny launcher (`.asi`) can
  be placed in the game folder with an ASI loader. Its only job is to start Camp Ledger. It
  doesn't read game memory, hook anything or touch saves. You can skip it and start Camp
  Ledger yourself.

Reading another program's files or memory is never risk-free, and there's no official
statement on ban risk. Use it in offline play at your own risk.

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
| `make reader` | `dist/reader/CampLedgerReader.exe` (includes the dashboard) |
| `make launcher` | `dist/launcher/CampLedgerLauncher.asi` and `.ini` |
| `make test` | runs every test project (no game needed) |
| `make clean` | removes `dist/` and build folders |

### Run

Start the game, then:

```bash
dist/reader/CampLedgerReader.exe watch
```

and open `http://localhost:5240`. Camp Ledger records while the game runs and exits when
the game closes.

| Command | What it does |
|---|---|
| `watch` | waits for the game, records while it runs, serves the dashboard, exits when the game closes |
| `watch --stay` | same, but goes back to waiting after the game closes |
| `dashboard` | serves the dashboard from what was already recorded, without the game, and opens your browser (`--no-open` to skip that) |
| `snapshot` | prints one snapshot from your newest save as JSON |
| `snapshot <file>` | same, from a given save file (a copy is read) |

Snapshots are kept in `%LOCALAPPDATA%\CampLedger\snapshots.jsonl` and the log in
`%LOCALAPPDATA%\CampLedger\reader.log`. Your what-if plan is kept in your browser only.

### Start it with the game (optional)

1. Get the [Ultimate ASI Loader](https://github.com/ThirteenAG/Ultimate-ASI-Loader) (x64) and
   put it in `<game install>\bin64` as `winmm.dll`.
2. Copy `dist/launcher/CampLedgerLauncher.asi` and `CampLedgerLauncher.ini` into the same
   folder. The ini holds one line: the full path to `CampLedgerReader.exe`.
3. Start the game. Camp Ledger starts with it, and the dashboard is at
   `http://localhost:5240` while you play.

To remove it, delete those three files. See [launcher/README.md](launcher/README.md).

## Game updates

Patches change the save format, game data and memory layout, so each game version has to be
checked before Camp Ledger trusts it. On a version it doesn't know, it reports that and stops
instead of guessing. Expect a delay after each game patch.

## Roadmap

| Milestone | Status |
|---|---|
| Read camp state from the save | Done ([research notes](docs/re-notes/save-contents.md)) |
| Mission details from game data, English names, bonuses | Done |
| Calculation core: net, per hour, per day, upgrade planner | Done, with tests |
| Live reserves from game memory | Working; still being checked across sessions |
| Local dashboard with comrades and what-if plans | Done |
| History: predicted vs actual change between snapshots | Not started |
| Import and export of the original spreadsheet layout | Not started |
| Release download for Nexus Mods | Not started |

An online version with accounts and sync was planned at first and set aside in favour of the
local dashboard. See [docs/architecture.md](docs/architecture.md).

## Repository layout

```
reader/      Camp Ledger itself (C# / .NET 8): save and game-data reading, live reserves,
             snapshots, and the dashboard (reader/CampLedger.Reader/dashboard/)
core/        calculation core (pure C#, no I/O) and its tests
launcher/    the launcher-only ASI (C, built with Zig)
offsets/     per-game-version memory layout data
docs/        architecture decisions and research notes (docs/re-notes/)
tools/       research scripts used to work out the save format
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
