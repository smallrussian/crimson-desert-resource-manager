# Camp Ledger desktop app

`CampLedger.exe`: the dashboard in its own window, with the reader running inside it. See
`docs/architecture.md` ("Desktop app").

Run from source (needs the .NET 8 SDK):

    dotnet run --project app/CampLedger.App

Command line:

    CampLedger.exe                                   open the window (or bring it forward)
    CampLedger.exe watch                             start in the tray (used by the launcher ASI)
    CampLedger.exe --install-launcher [--game dir]   start with the game (adds the launcher files to bin64)
    CampLedger.exe --remove-launcher                 undo that

Installer: `powershell -ExecutionPolicy Bypass -File installer\build.ps1`.

Updates: the app updates itself from GitHub Releases (window banner or tray menu). To publish a version, see
`docs/release.md`.
