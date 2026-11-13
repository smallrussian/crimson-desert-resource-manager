# Reader

C# / .NET 8. Read-only toward the game. See `docs/architecture.md`.

Build (needs the .NET 8 SDK). From the repo root, `make reader` publishes to `dist/reader/`; or:

    dotnet build reader/CampLedger.Reader -c Release

Run (`reader/CampLedger.Reader/bin/Release/net8.0-windows/CampLedgerReader.exe`):

    CampLedgerReader snapshot              one snapshot from the newest save, as JSON (with mission details
                                           from the game data when the install is found)
    CampLedgerReader snapshot <file>       same, from a given save file (a temp copy is read)
    CampLedgerReader watch                 wait for the game, record while it runs, exit when it closes
    CampLedgerReader watch --stay          same, but go back to waiting after the game closes
    (the launcher ASI in launcher/ starts "watch" when the game loads)

Snapshots are appended to `%LOCALAPPDATA%\CampLedger\snapshots.jsonl`.

Game versions: the reader only uses game data and live memory for versions with a folder in `offsets/`
(`gamedata.json` and `memory.json`). After a game patch, follow `docs/patch-update.md`.

Tests (no game needed):

    dotnet test reader/CampLedger.Reader.Tests
