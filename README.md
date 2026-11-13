# Camp Ledger

A read-only companion app for the PC version of **Crimson Desert** that keeps track of your
Greymane Camp: reserves, dispatch missions and comrades. It shows what each mission and your
whole set of running missions actually earns you per cycle, per hour and per day, so you
don't have to keep a spreadsheet by hand.

It's a small Windows app that runs next to the game. There's no account and no server, and
your camp data never leaves your PC.

> **Status: early release (0.2.x).** Supports game versions **2.04.08** and **2.05.00**
> (Steam).

## What it does

- **Reads your camp while you play.** Reserves (Food, Funds, Stone, Timber, Armaments) are
  read live from the game once it has loaded them, otherwise from the latest save. Missions,
  comrades and camp level come from the save and update each time the game saves.
- **Fills in mission details from the game's own data.** English names, region, duration,
  cost, reward with your bonuses included, and extra item drops with their chance.
- **Remembers your camp.** Open the app any time, even with the game closed, to see the last
  known state. If you played without it, it catches up from your newest save when it starts.
- **Shows it all in one window:**

| Tab | What you get |
|---|---|
| Reserves (top) | Balance, net per hour and per day, and hours until a reserve runs out |
| Missions | Running missions with cost, reward and net; one-off builds listed apart |
| Available | Missions you could start now, using the game's own start conditions. Search, filter by region and by what they produce, sort by best net per hour |
| Comrades | Who is free or busy, their skill slots, and which slots your camp level has unlocked |
| Plan | What-if: add available missions with chosen comrades or stop running ones, and see net per hour and per day before and after, plus problems like busy comrades or a negative Food or Funds budget |
| Upgrade | Enter an upgrade's cost to see the shortfall and how long until you can afford it |
| History | Reserves over time from past snapshots |

If something can't be read, Camp Ledger says so ("What couldn't be read" at the bottom of
the window). It never fills in a guessed number. "Per day" uses a hours-played-per-day
setting you choose (default 24), labelled as an assumption.

## Install

1. Download the latest `CampLedgerSetup-X.Y.Z.exe` from
   [Releases](../../releases/latest).
2. Run it. It installs for your Windows user only and needs no admin rights. The installer
   isn't code-signed, so Windows SmartScreen may warn the first time.
3. Optional: tick **Start Camp Ledger automatically with Crimson Desert** during setup (or later from the tray menu). See
   below.
4. Start the game and press Continue. Open Camp Ledger from the Start menu.

Needs Windows 10 (1809 or later) or 11. The window uses the Microsoft Edge WebView2 Runtime,
which Windows 11 already has. If it's missing, the installer tells you where to get it, and
you can use **Open in browser** from the tray icon meanwhile.

### Start with the game

With this option on, Camp Ledger starts in the tray whenever the game starts, records while
you play, and exits after the game closes. To do that, it places these files in
`<game install>\bin64`:

| File | What it is |
|---|---|
| `winmm.dll` | [Ultimate ASI Loader](https://github.com/ThirteenAG/Ultimate-ASI-Loader), added only if there's no `winmm.dll` there already |
| `CampLedgerLauncher.asi` | Camp Ledger's launcher: starts Camp Ledger and does nothing else |
| `CampLedgerLauncher.ini` | One line: where Camp Ledger is installed |

Turning the option off, or uninstalling, removes them again. An existing `winmm.dll` from
another mod is never replaced or deleted. The game must be closed while the files are added
or removed.

### Updates

Camp Ledger checks this repository's GitHub Releases for a new version shortly after it
starts and every 6 hours. When there is one, click **Update and restart**. It downloads the
new installer, checks its size and SHA-256 against what GitHub reports, and installs over the
current version, keeping your snapshots and settings. You can turn automatic checks off in
the tray menu.

Game patches need an app update before Camp Ledger can read the new version, so expect a
short delay after each patch. On a game version it doesn't know, it shows "unknown" instead
of guessing.

### Uninstall

Use **Installed apps** in Windows Settings. Your snapshots, log and settings stay in
`%LOCALAPPDATA%\CampLedger`; delete that folder too if you want them gone.

## Safety

- **Read-only.** Camp Ledger never writes to the game's memory or save files. Saves are
  copied to a temp file, the copy is read, and the copy is deleted. Live reserves are read
  from outside the game with Windows' standard read-only process access; nothing is injected.
  The only files it ever puts in the game folder are the three "start with the game" files
  above, and only if you turn that on.
- **No anti-tamper bypass.** It doesn't patch, disable or work around Denuvo.
- **Offline play only.** It doesn't touch any online features of the game.
- **No game passwords.** It never asks for or stores a Steam or other platform login.
- **Your data stays local.** The dashboard only answers on `localhost`. The app's one network
  request is the update check to GitHub, which sends no game or personal data, and you can
  turn it off.

Reading another program's files or memory is never risk-free, and there's no official
statement on ban risk. Use it in offline play at your own risk.

## Build from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). For the launcher
and the Makefile also GNU make, a POSIX shell (Git Bash works) and
[Zig](https://ziglang.org/) 0.17+ (`winget install zig.zig`). For the installer,
[Inno Setup 6](https://jrsoftware.org/isinfo.php).

Run the desktop app:

```bash
dotnet run --project app/CampLedger.App
```

Build the installer (into `installer\out\`):

```bash
powershell -ExecutionPolicy Bypass -File installer/build.ps1
```

Run the tests (no game needed):

```bash
make test
```

The Makefile also builds the standalone console reader (`make reader`) and the launcher
(`make launcher`) into `dist/`.

### Console reader

`CampLedgerReader.exe` is the same reader without a window, for scripting and debugging:

| Command | What it does |
|---|---|
| `watch` | waits for the game, records while it runs, serves the dashboard at `http://localhost:5240`, exits when the game closes |
| `watch --stay` | same, but goes back to waiting after the game closes |
| `dashboard` | serves the dashboard from what was already recorded, without the game |
| `snapshot` | prints one snapshot from your newest save as JSON |
| `snapshot <file>` | same, from a given save file (a copy is read) |

Snapshots are kept in `%LOCALAPPDATA%\CampLedger\snapshots.jsonl` and the log in
`%LOCALAPPDATA%\CampLedger\reader.log`.

## Roadmap

| Milestone | Status |
|---|---|
| Read camp state from the save | Done ([research notes](docs/re-notes/save-contents.md)) |
| Mission details from game data, English names, bonuses, regions | Done |
| Calculation core: net, per hour, per day, upgrade planner | Done, with tests |
| Live reserves from game memory | Done |
| Dashboard with comrades and what-if plans | Done |
| Desktop app, installer, start with the game, in-app updates | Done |
| History: predicted vs actual change between snapshots | Not started |
| Import and export of the original spreadsheet layout | Not started |
| Nexus Mods page | Not started |

## Repository layout

```
app/         the desktop app (WinForms + WebView2): window, tray, updates
reader/      the reader (C# / .NET 8): save and game-data reading, live reserves,
             snapshots, and the dashboard page (reader/CampLedger.Reader/dashboard/)
core/        calculation core (pure C#, no I/O) and its tests
launcher/    the launcher-only ASI (C, built with Zig)
installer/   Inno Setup script and build script
offsets/     per-game-version layout data (one folder per supported version)
docs/        architecture, release and patch-update procedures, research notes
tools/       research and probe tools used to work out the formats
fixtures/    small hand-written test data
```

## Contributing and research

Every claim about where data lives in the save, the game data or memory has a write-up in
[docs/re-notes/](docs/re-notes/), with how it was found and which game version it was
checked on. Supporting a new game patch follows [docs/patch-update.md](docs/patch-update.md),
and releases follow [docs/release.md](docs/release.md). Architecture decisions are in
[docs/architecture.md](docs/architecture.md), and the project rules for contributors (human
or AI) are in [CLAUDE.md](CLAUDE.md).

The repository contains no game files, extracted assets or real save files. Game data is
read from your own install at run time and is never written to disk.

## Credits

Camp Ledger is built on other people's work. Thank you to everyone below.

### Research

The save format work builds on public research by
[NattKh](https://github.com/NattKh/CRIMSON-DESERT-SAVE-EDITOR-AND-GAME-MODS) and
[bbfox0703](https://github.com/bbfox0703/crimsonatomtic). Their code was read, not run; the
decoding here is a fresh implementation.

### Shipped with the app

These are inside the installer or placed on your PC by it. Their licence texts are in
[installer/THIRD-PARTY-NOTICES.txt](installer/THIRD-PARTY-NOTICES.txt), installed next to
the app.

| Software | Used for | By | Licence |
|---|---|---|---|
| [Ultimate ASI Loader](https://github.com/ThirteenAG/Ultimate-ASI-Loader) v10.0.2 | Loads the launcher so Camp Ledger can start with the game (optional) | ThirteenAG | MIT |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | The installer, uninstaller and in-app updates | Jordan Russell and Martijn Laan | Inno Setup License |
| [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) SDK | Shows the dashboard in the app window | Microsoft | BSD-style |
| [.NET](https://dotnet.microsoft.com/) 8 runtime | Runs the app (bundled, so you don't need to install it) | .NET Foundation and contributors | MIT |

### Used to build and test

| Software | Used for | By | Licence |
|---|---|---|---|
| [.NET SDK](https://github.com/dotnet/sdk) | Builds the reader, app and tests | .NET Foundation and contributors | MIT |
| [Zig](https://ziglang.org/) | Compiles the launcher ASI | Zig Software Foundation | MIT |
| [xUnit.net](https://xunit.net/) | Test framework | .NET Foundation and contributors | Apache 2.0 |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | Test runner | Microsoft | MIT |
| [GNU Make](https://www.gnu.org/software/make/) | The top-level Makefile | Free Software Foundation | GPL 3.0 |
| [actions/checkout](https://github.com/actions/checkout), [actions/setup-dotnet](https://github.com/actions/setup-dotnet) | CI and release builds on GitHub Actions | GitHub | MIT |
| [mlugg/setup-zig](https://github.com/mlugg/setup-zig) | Installs Zig in release builds | mlugg | MIT |
| [python-lz4](https://github.com/python-lz4/python-lz4) | Research scripts in `tools/re/` | python-lz4 contributors | BSD 3-Clause |
| [cryptography](https://github.com/pyca/cryptography) | Research scripts in `tools/re/` | Python Cryptographic Authority | Apache 2.0 or BSD 3-Clause |

The reader's own LZ4 decompression and ChaCha20 decryption are written from the public
specifications of [LZ4](https://github.com/lz4/lz4) (Yann Collet) and
[ChaCha20](https://datatracker.ietf.org/doc/html/rfc8439) (Daniel J. Bernstein), with no
library code copied.

## Disclaimer

Camp Ledger is a fan project. It isn't affiliated with or endorsed by Pearl Abyss.
Crimson Desert is a trademark of Pearl Abyss.
