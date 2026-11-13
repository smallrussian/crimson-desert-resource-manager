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

A hosted web app, sync client, hosting and auth provider: set aside by the local dashboard decision below.
They come back only if cloud sync is wanted later.

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

**Superseded for now:** the hosted web app (milestones 4-5) is replaced by the local dashboard below.

**Definitions** are copied from the owner's Google Sheet (tabs Missions, Portfolio, Upgrade Planner), read
2026-11-12. One deliberate difference: the sheet treats a mission with a blank duration as 0 per hour, which
quietly drops it from the per-hour totals. AGENTS.md says a missing duration means unknown, so the core
returns null for any reserve that such an active mission changes, and names the mission.

## Local dashboard instead of a hosted web app (decided 2026-11-12)

**Decision.** The owner wants something easy for people downloading from Nexus Mods. The reader serves the
dashboard itself at `http://localhost:5240` (this PC only). No accounts, no hosting, no data leaving the PC.
This replaces milestones 4 (web app with accounts) and 5 (device login and sync) for now; cloud sync can be
added later on top of the same snapshot log if wanted.

**Why.** One download for users; no server cost or upkeep; works offline; reuses the reader, the calc core and
the snapshot log. A browser page is less UI code than WPF/WinUI and also works on a phone if the owner later
opts in to serving on the home network (not done: it listens on localhost only).

**How it works.**
- `CampLedgerReader watch` (started by the launcher ASI with the game) also serves the dashboard.
  `CampLedgerReader dashboard` serves it without the game (e.g. after it closed) and opens the browser;
  `--log <file>` points it at another snapshot log, `--no-open` skips the browser.
- `DashboardServer` (System.Net.HttpListener, part of .NET, no new dependency): GET only, refuses any Host
  header other than `localhost:5240` (DNS rebinding) and sends a strict Content-Security-Policy. Page files are
  embedded in the exe (`reader/CampLedger.Reader/dashboard/`), so nothing can be served from disk.
  `GET /api/state?hoursPerDay=` (with ETag, polled every 3 s) and `GET /api/plan?food=&funds=...`.
- `SnapshotLog` follows `snapshots.jsonl`, reading only new bytes. `DashboardView` (pure, tested) builds what
  the page shows with the calc core: reserve balances (live memory when newer than the save), per cycle /
  hour / day, hours until empty, workers, running missions with bonus-included rewards, available missions,
  upgrade planner, history.
- **Which missions count toward the totals:** repeating missions (save `_operationShouldRepeat`). One-off
  builds are shown under Construction and count as busy comrades, but stay out of the per-hour totals, like
  the owner's sheet. A base reward of all zeros stays zero whatever the bonus (so an unknown bonus on a build
  does not make it unknown).
- **Available missions** show a minimum reward: base plus the region's conversion bonus. Skill and comrade
  bonuses depend on who is sent and can only add.
- Real-time estimates ("about 42 min real") use 250 real seconds per in-game hour, measured once
  (`mission-static-data.md`); labelled as approximate.
- Page: plain HTML/CSS/JS, no framework or CDN (fast, offline). Dark crimson-and-gold theme, serif headings
  (Windows' Palatino Linotype), tabular numbers, keyboard shortcuts 1-4 and `/`, responsive down to phone width.

### Comrades and what-if plans (2026-11-12)

- Snapshot `comrades[]`: every dispatchable comrade (same rule as `workers.total`) with skill slots
  (`ComradeSkill`: type, tier, percent) and the running mission they are on, if any.
- Dashboard **Comrades** tab: status, slots with camp-level locks, filter by skill (sorted by unlocked total).
- Dashboard **Plan** tab and `GET /api/simulate?add=op:ck.ck,...&stop=op,...`: running repeating missions minus
  stopped ones plus available missions with chosen comrades, through the calc core (bonus from the chosen
  comrades' unlocked slots; +20% per comrade above the minimum). Shows per-reserve before/after per hour and day,
  free comrades before/after, and problems (busy comrade, too few, over capacity, operating budget). A mission
  with too few comrades is counted at its minimum with no matching skill. The plan lives in the browser's
  local storage only.

### How fresh the data is

- Reserves: live from game memory, written when they change (checked every 5 s while the game runs).
- Missions, comrades, available missions, camp level: from the save, so they update when the game saves
  (autosave or manual). The watcher checks for a newer save every 5 s.
- The page polls every 3 s (ETag; unchanged data costs a 304).

## Desktop app (decided 2026-11-13)

**Decision.** The owner chose a standalone, installable Windows app so the reader and dashboard work as one:
`app/CampLedger.App` (`CampLedger.exe`), WinForms + Microsoft Edge WebView2 showing the existing dashboard page,
with the reader (`Watcher`) and `DashboardServer` running in the same process. Installer: Inno Setup 6
(`installer/`), per-user, no admin prompt. New dependency: `Microsoft.Web.WebView2` (Microsoft's control for
showing the existing HTML dashboard in a native window; the runtime ships with Windows 11).

**Not chosen.** A WPF/XAML rewrite of the dashboard (all UI written twice), Electron or Tauri (second language,
bigger download), Velopack (would replace the Inno installer and its "start with the game" and uninstall steps;
GitHub Releases now serves as the update feed instead, see below).

**How it works.**
- `CampLedger.exe` opens the window; `CampLedger.exe watch` (what the launcher ASI runs) starts in the tray.
  One instance (mutex `CampLedger.App`); a second launch brings the window forward.
- It records only if it holds `Local\CampLedgerReader.watch`; if the console reader already does, the window
  just shows that reader's dashboard.
- Lifetime: stays while the window is open or the game runs. Closing the window while recording sends it to the
  tray; it then exits after the game closes. Task Manager / shutdown closes exit outright.
- **Values when the game is not running.** Every snapshot stays in `%LOCALAPPDATA%\CampLedger\snapshots.jsonl`,
  so the app shows the last known camp whenever it is opened. At start, if the newest save was written after the
  last save snapshot in the log (game played without the reader), it reads that save once (from a copy) and
  appends it (`Watcher.CatchUpFromSave`). The game version then comes from the install's `meta/0.paver`.
  Dashboard settings and plans persist in the app's WebView2 profile (`%LOCALAPPDATA%\CampLedger\WebView2`);
  they do not carry over from a browser.
- Only `http://localhost:5240/` opens inside the window; other links go to the default browser. Dev tools and
  the default context menu are off in Release builds.
- **Start with the game** (installer task, unticked by default; also a tray menu toggle): `LauncherFiles` copies
  `CampLedgerLauncher.asi`, writes `CampLedgerLauncher.ini` (path to `CampLedger.exe`) and adds `winmm.dll`
  (Ultimate ASI Loader) to `<game>\bin64` only if no `winmm.dll` is there. An existing different `winmm.dll` is
  never overwritten or deleted. What was placed is recorded in `%LOCALAPPDATA%\CampLedger\launcher.json`;
  removal (tray toggle or uninstall) deletes the ASI and ini, and the loader only while its hash still matches.
  Refused while the game is running. The launcher ASI itself is unchanged.
- Uninstall keeps `%LOCALAPPDATA%\CampLedger` (snapshots, log, settings).

**Build:** `powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-Version x.y.z]` →
`installer\out\CampLedgerSetup-<version>.exe` (about 48 MB; self-contained .NET 8, so users need no runtime).
Inno Setup 6.7 reports "non-commercial use only"; a free Nexus release is non-commercial.

**Checked 2026-11-13** (game 2.05.00 running, console reader recording): window renders the dashboard; single
instance; launcher-style `watch` start stays in the tray; closing exits when this app is not the recorder;
published app runs standalone. **Not yet checked:** the installer run end to end, the tray-on-close path while the
app itself records, `CatchUpFromSave` against a real save, and the "start with the game" task on the real
game folder.

## In-app updates from GitHub Releases (decided 2026-11-13)

**Decision.** The owner wants updates without reinstalling. The app checks this repository's latest published GitHub
release. When it finds a newer one, it offers **Update and restart**: it downloads `CampLedgerSetup-X.Y.Z.exe`,
checks its size and the SHA-256 GitHub reports for the asset, and runs the same Inno installer silently over the
existing install. The installer waits for the old process to exit and relaunches the app, in the window or in the
tray as it was. Releases are built and published by `.github/workflows/release.yml` when a version bump is merged
into `main` (owner's choice, 2026-11-13: publish as soon as tests pass, no draft step). Procedure and acceptance rules: [release.md](release.md).

**Why.** No new dependency and no hosting. One installer serves both first installs and updates, so the "start with
the game" task, the uninstall cleanup and the per-user folder all keep working. Game patches need new
`offsets/<version>/` files, which ship inside the app, so updating has to be easy.

**Network use.** This is the app's only outbound request: one unauthenticated GET to `api.github.com` 20 s after
start and every 6 h, plus the download when the user clicks update. No user or game data is sent. It can be turned off
from the tray menu (**Check for updates automatically**).

**Checked 2026-11-13** (game closed): 0.1.0 installed by its installer into a scratch folder, pointed at a local
test feed (`CAMPLEDGER_UPDATE_FEED`) serving a 0.1.1 release. It found the update 20 s after start and showed the
banner. **Update and restart** downloaded and verified the installer and the app exited. The silent install
succeeded and 0.1.1 reopened in the window, about 7 s after the click. Download folder cleaned on the new start.
**Not yet checked:** an update from a real published GitHub release (none exists yet), the GitHub Actions workflow
(first run happens on the first tag), relaunch into the tray (`/RELAUNCH=watch`), and an update over an install with
"start with the game" ticked.
