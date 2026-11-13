# Releasing a new version of the desktop app

Installed copies of Camp Ledger update themselves from this repository's GitHub Releases, which are published
automatically when a version bump is merged into `main`. Users do not download or
run anything. They click **Update and restart** in the window's banner (or the tray menu). The app downloads the new
installer, checks it, installs it over the current version in the same folder, and starts again. Snapshots,
settings, window position and "start with the game" are all kept.

## Cutting a release

Releases happen when a pull request that bumps the version is merged into `main`.

1. In the pull request, set the new version, `X.Y.Z` (bump Z for fixes and new game-version offsets, Y for
   features), in all three places:
   - `<Version>` in `app/CampLedger.App/CampLedger.App.csproj` (the workflow reads this one),
   - `AppVersion` in `installer/CampLedger.iss`,
   - the default `-Version` in `installer/build.ps1`.
2. Merge it. The **Release** workflow (`.github/workflows/release.yml`) runs on every push to `main`:
   - if tag `vX.Y.Z` already exists, it stops (merges that do not bump the version release nothing);
   - otherwise it runs the core and reader tests, builds `CampLedgerSetup-X.Y.Z.exe` the same way as
     `installeruild.ps1`, and **publishes** release `vX.Y.Z` (tagged at the merged commit, marked latest, with
     generated notes). A failed test or build publishes nothing.
3. Installed apps see it within 20 seconds of starting, or within 6 hours if they are already running, and right
   away from **Check for updates** in the tray menu.

There is no manual test step any more: test the installer from the pull request's branch with `installeruild.ps1`
before merging (see "Testing an update without publishing"). The workflow can also be started by hand from the
Actions tab (`workflow_dispatch`); it follows the same rules.

Never re-upload a different file to a published release. Make a new version instead. To pull a bad release, mark
it as a prerelease or delete it on GitHub: apps then fall back to the previous latest release (they never
downgrade, so ship the fix as a higher version).

## What installed apps accept

The app reads `GET https://api.github.com/repos/smallrussian/crimson-desert-resource-manager/releases/latest`.
This returns the newest *published* release; drafts and prereleases are ignored. The app sends no data other than
the request itself (user agent `CampLedger/<version>`). The release is used only if all of these hold
(`reader/CampLedger.Reader/AppUpdates.cs`):

- the tag is exactly `vX.Y.Z` and is newer than the running version;
- it has an asset named `CampLedgerSetup-X.Y.Z.exe`, downloaded from this repository's release URL;
- GitHub reports a `sha256:` digest and a size for that asset.

The download must match that size and SHA-256 or it is deleted. Only then does it run with
`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /WAITPID=<pid> /RELAUNCH=window|watch` and an Inno log
in `%LOCALAPPDATA%\CampLedger\update-setup.log`. The installer waits for the old app to exit, replaces the files,
skips the "start with the game" step (already done, and refused while the game runs), and starts the new version. It
opens in the window if the window was open, otherwise in the tray, so recording continues.

**Trust:** the check guards against corrupted or swapped downloads, but anyone who can publish a release on this
repository can ship code to users. Keep the repository's write access and the GitHub account protected (2FA). The
installer is not code-signed, so Windows SmartScreen may warn on a *first* manual install. In-app updates do not go
through SmartScreen.

## User controls

Tray menu: **Check for updates (version X.Y.Z)**, **Check for updates automatically** (on by default; stored in
`%LOCALAPPDATA%\CampLedger\updates.json`), and **Update to version X.Y.Z and restart** once one is found. The window
banner's **Later** hides it until the app restarts.

## Testing an update without publishing

Install an older build, then serve a fake release from this PC and start the app with
`CAMPLEDGER_UPDATE_FEED=http://127.0.0.1:8765/latest`. The app accepts this variable only for `localhost` or `127.0.0.1`.
The feed is a GitHub-style release JSON (`tag_name`, `draft`, `prerelease`, `assets[]` with `name`,
`browser_download_url`, `size` and `digest: "sha256:..."`). The installer must be served from the same origin at
`/<tag>/CampLedgerSetup-X.Y.Z.exe`, for example with `python -m http.server 8765 --bind 127.0.0.1`.

## Building locally

`powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version X.Y.Z` builds the same installer. This needs
zig and `launcher\build\install\winmm.dll` (see the script's header). The workflow downloads the pinned Ultimate ASI
Loader v10.0.2 zip and checks the zip's and the DLL's SHA-256.
