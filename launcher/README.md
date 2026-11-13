# Launcher ASI

A native DLL that an ASI loader loads into the game. It starts the external reader and does nothing else:
no game memory access, no hooks, no saves (AGENTS.md hard rule 1, launcher-only exception).

Build (Zig 0.17, installed with `winget install zig.zig`). From the repo root, `make launcher` builds
`dist/launcher/` with an ini that points at `dist/reader/CampLedgerReader.exe`; or by hand:

    zig cc -shared -O2 -target x86_64-windows-gnu CampLedgerLauncher.c -o build/CampLedgerLauncher.asi -lkernel32 -luser32

Install: the desktop app's installer ("Start with the game") or its tray menu does this for you; see
`docs/architecture.md`. By hand: copy `build/CampLedgerLauncher.asi` to the folder your ASI loader reads, and put a
`CampLedgerLauncher.ini` beside it containing one line, the full path to `CampLedgerReader.exe`.
If the ini or the reader is missing, the launcher does nothing.

Remove: delete those two files.
