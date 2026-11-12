# AGENTS.md

Guidance for AI coding agents (Claude Code and others) working on this repository.
Read this whole file before changing anything. When something here conflicts with a
request, stop and ask.

## Project

**Camp Ledger** (working name): a read-only companion for the PC version of
*Crimson Desert* that reads Greymane Camp state (reserves, dispatch missions, workers)
and shows net resource gain per mission and per portfolio in a web app, replacing a
hand-maintained Google Sheet.

Three parts:

1. **Reader**: a local Windows program that obtains camp state from the game.
2. **Sync**: an authenticated client that sends snapshots to the web app.
3. **Web app**: accounts, a dashboard, history, and the net-gain calculations.

## Status

Pre-code. Nothing below has been built or verified against the game. Every command,
path and data layout in this file marked *planned* or *unknown* must be confirmed
before you rely on it. Do not invent working commands. If you add a real command,
move it out of "planned" and keep it accurate.

## Hard rules

These are not negotiable and apply to every task.

1. **Read-only toward the game.** Never write to the game's process memory, save
   files, or install directories. Saves are opened read-only, and any save you read
   must be a copy.
   **One exception, approved by the owner on 2026-11-11:** a *launcher-only* ASI may be
   placed in the game's install folder, together with the ASI loader it needs. Its only
   job is to start the external reader when the game loads. It must not read or write
   game memory, hook or call game functions, or touch saves. Anything more than that is
   still tier 3 and needs fresh written approval.
2. **Never bypass, patch, or disable anti-tamper.** The Steam build ships with
   Denuvo Anti-Tamper. Do not work around it. If the reader cannot function with
   it present, stop and report; do not look for a way around.
3. **Offline play only.** Do not touch networked or multiplayer features. Do not
   assume that offline play makes memory access risk-free: no authoritative
   statement on ban risk has been found, so treat the risk as unknown.
4. **No game assets in the repo.** No extracted game files, textures, data tables
   copied wholesale from the game, or save files from real players. Small
   hand-written fixtures only.
5. **Do not collect game or platform credentials.** The "login" in this project is
   the web app account. The tool never asks for, reads, or stores a Steam or other
   platform password or token.
6. **Fail closed.** If the game version is unknown or a value fails validation, report
   "unknown" instead of guessing. Never publish a number we did not read.
7. **Evidence before claims.** Any statement about where data lives (a save field, a
   memory path, a signature) needs a note in `docs/re-notes/` with how it was found
   and which game version it was checked against.
8. **DO NOT EVER LOAD A MANUAL SAVE.** only ever press continue on the title screen
9. **DO NOT EVER SAVE THE GAME.** EVER

## Architecture

```
 Game (offline)
     |
     |  read-only
     v
 +-----------+     snapshot      +--------------+   HTTPS + device token   +-----------+
 |  Reader   | ----------------> |  Sync client | -----------------------> |  Web app  |
 | (sources) |   (local queue)   |  (SQLite)    |   idempotent ingest      |  API + UI |
 +-----------+                   +--------------+                          +-----------+
```

### Reader: sources behind one interface

The rest of the system must not care how data was obtained. Define one `Source`
interface that returns a `Snapshot` (see Data model) or an explicit `Unavailable`
with a reason. Implement in this order and stop at the first tier that works:

| Tier | Source | Live? | Risk | Notes |
|---|---|---|---|---|
| 1 | Save-file parser | No, updates when the game saves | Lowest | Community editors already decode the encrypted save. Unknown whether camp resources and dispatch state are in the decoded fields. |
| 2 | External process reader | Yes | Unknown | Separate program, read-only, no injection. Needs address discovery per game version. |
| 3 | In-game plugin (ASI) | Yes | Highest | Only with explicit owner approval in writing in the task. The launcher-only ASI under hard rule 1 is not a data source and does not count as this tier. |

Start with tier 1. Do not begin tier 2 until the tier 1 findings are written up in
`docs/re-notes/` and the owner has read them.

### Version handling

- Detect the game version at startup. Keep per-version layouts and signatures in
  data files under `offsets/<game-version>/`, not hard-coded.
- Unknown version: do nothing and say so. Patches shift layouts and the community
  parsers needed updates for many patches, so expect to redo work per patch.
- Every layout file records the evidence it came from and the date checked.

### Sync client

- Polls the reader on an interval and emits a snapshot only when something changed.
- Buffers snapshots in a local SQLite file so nothing is lost while offline.
- Each snapshot has a client-generated UUID so the server can ingest idempotently.

### Auth: how the tool logs in to the web app

Use the OAuth 2.0 device authorization flow (RFC 8628), the same pattern CLI tools use.

1. Tool requests a device code and shows the user a short code and a URL.
2. User opens the URL in a browser, signs in to the web app, and approves the code.
3. Tool receives a per-device token and stores it in Windows Credential Manager.
4. Tool sends snapshots with `Authorization: Bearer <token>`.
5. The user can list and revoke devices in the web app. Revoked tokens fail closed.

Never log tokens. Never put tokens in config files, the repo, or crash reports.

### Web app

Mirrors the spreadsheet this replaces, so keep the definitions identical:

- **Net per cycle** = rewards minus costs, per reserve, over the missions marked active.
- **Net per hour** = net per cycle divided by mission duration in hours; sum over
  active missions. Guard division by zero (duration of 0 or missing means "unknown",
  not 0).
- **Net per day** = net per hour times hours per day. Hours per day is a user setting,
  default 24, and must be labelled as an assumption.
- **Reserves**: Food, Funds, Stone, Timber, Armaments. Food and Funds are the
  operating budget, so flag a negative there first.
- **Upgrade planner**: total needed = upgrade cost + one operating cycle of active
  mission costs; shortfall = max(0, total needed - balance); hours to afford =
  shortfall / net per hour, or "not at current net" when net per hour is zero or negative.
- **Workers**: total owned, in use by active missions, free. Over-assigned is an error state.
- **History**: auto-recorded from snapshots. This replaces the manual cycle log.
  Compare predicted change to actual change between snapshots and show the variance.
  Note that dispatches advance on in-game time, so elapsed time must come from the
  game clock if it can be read. Wall-clock time is not a valid substitute. Unknown.

Also provide import and export of the existing spreadsheet layout so nothing is
locked in.

## Data model

```json
{
  "id": "uuid",
  "capturedAt": "2026-11-11T22:00:00Z",
  "gameVersion": "1.18.00",
  "source": "save | process",
  "reserves": { "food": 0, "funds": 0, "stone": 0, "timber": 0, "armaments": 0 },
  "workers": { "total": 0, "inUse": 0 },
  "missions": [
    {
      "name": "string",
      "type": "string",
      "region": "string | null",
      "active": true,
      "workers": 0,
      "durationHours": 0,
      "cost":   { "food": 0, "funds": 0, "stone": 0, "timber": 0, "armaments": 0 },
      "reward": { "food": 0, "funds": 0, "stone": 0, "timber": 0, "armaments": 0 },
      "personalStorageReward": "string | null"
    }
  ],
  "inGameClock": "unknown until a source can read it"
}
```

Any field a source cannot read is `null`, never a guessed default. The UI shows "unknown".
Mission cost and reward figures may turn out to be static game data rather than
per-save data. If so, ship them as versioned reference data and keep only per-save
state (active, workers, timers) in snapshots.

## Suggested repo layout

Planned. Adjust when the real stack is chosen.

```
AGENTS.md
docs/
  re-notes/          findings, one file per question, with evidence and game version
  architecture.md
offsets/             per-version layouts (data, not code)
reader/              Source implementations, one directory per tier
sync/                snapshot queue, device auth, HTTP client
web/                 API, database migrations, UI
fixtures/            small hand-written sample snapshots and saves
```

Stack is **undecided**. Pick one per component and record the decision and reason in
`docs/architecture.md` before building. Preference to weigh: a language the owner can
debug comfortably, a single-binary reader for easy distribution, and a typed API
contract shared between sync and web (generate types from one schema).

## Milestones

Each milestone has an exit check. Do not start the next one until it passes.

1. **Feasibility note.** Written answer, with evidence, to: are camp reserves and
   dispatch state readable from a save copy? Exit: `docs/re-notes/save-contents.md`.
2. **Reader v0.** Tier 1 `Source` prints a snapshot as JSON for one real save copy.
   Exit: values match what the game UI shows, verified by the owner.
3. **Calc core.** Pure functions for net, per hour, per day, upgrade planner, with
   unit tests that reproduce the spreadsheet's results on a shared fixture.
4. **Web app v0.** Accounts, manual snapshot upload, dashboard.
5. **Device login and sync.** Device flow, token storage, idempotent ingest, offline queue.
6. **History.** Predicted vs actual from snapshots.
7. **Live reader (only if tier 1 is not enough).** Tier 2, with owner sign-off first.

## Reverse-engineering workflow

When working out where data lives:

1. Start from public prior art and read it before writing anything. Candidates
   (unvetted, read the source, do not run binaries from untrusted authors):
   - https://github.com/NattKh/CRIMSON-DESERT-SAVE-EDITOR-AND-GAME-MODS
   - https://github.com/bbfox0703/crimsonatomtic
   - https://www.nexusmods.com/crimsondesert/mods/2189 (an external overlay that
     reads process memory; useful as a reference for what has been mapped)
2. Change one thing in the game, capture before and after, and diff. Record the
   change, the diff, and the game version.
3. Confirm a finding on a second save or a second session before calling it known.
4. Write it up in `docs/re-notes/` the same day. Unwritten findings do not exist.

## Conventions

- Keep the calc core pure and free of I/O so it can be tested against fixtures.
- Numbers are integers as the game shows them. Do not round or infer.
- Prefer small, reviewable changes. One concern per commit.
- Tests: every calc function needs a test with at least one case taken from the
  original spreadsheet. Reader code needs fixture-based tests; never require the game
  to be installed to run the test suite.
- Do not add dependencies without a one-line reason in the PR description.
- Security: validate all ingest payloads against the schema, rate limit per device,
  and scope every query to the authenticated user.

## Open questions

Track answers in `docs/re-notes/`, not here.

- Are camp reserves, dispatch state, and worker counts present in the decoded save?
- Is the in-game clock readable, and how are dispatch timers stored?
- Are mission costs and rewards static game data or per-save values?
- What is the real ban and anti-tamper risk for an external read-only reader? Treat
  as unknown until there is an authoritative answer.
- Does the Steam, Epic or other build differ in save location or format?

## When to stop and ask the owner

- Any step that would write to the game, its saves, or its process.
- Anything that touches Denuvo or other anti-tamper.
- Moving to tier 2 or tier 3.
- Choosing a stack, a hosting provider, or an auth provider.
- Storing any user data beyond what the data model above lists.
