# Live memory (tier 2): first findings

Date: 2026-11-11. Game **2.04.08** (Steam build 26480954), one session, pid 31428.
Status: **in progress**. Everything here was seen in one session only. AGENTS.md wants a second session
before anything is called known.

## Sign-off and limits

- The owner told the agent on 2026-11-11, after the tier 1 note was written, to "skip to tier 2, tier 1 is the
  fallback". The owner launched the game and pressed Continue. The agent did not launch, save or load.
- Access is external and read-only: `OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ)`,
  `VirtualQueryEx`, `ReadProcessMemory`. No write access is requested, nothing is injected.
- **The read-only open succeeded with Denuvo present.** Nothing was bypassed. Ban risk is still unknown.
- Tool: `tools/re/memscan.py` (`probe`, `near`, `pat`, `dump`). One pass over about 9.7 GiB of writable
  memory takes about 70 s in Python.

## Reserves: found

Each reserve is a heap object. Relative to the amount:

| Offset | Size | Content |
|---|---|---|
| −0x10 | 8 | heap pointer (differs per object) |
| −0x08 | 8 | `01 00 00 00 01 00 00 00` |
| +0x00 | 8 | **amount** (u64) |
| +0x08 | 4 | 0 |
| +0x0C | 4 | **item key** (11 Funds, 12 Food, 13 Timber, 14 Stone, 15 Armaments) |
| +0x10 | 8 | `0x145AB9800`, a pointer into the game executable, identical on all five |

Evidence: searching for `amount, 0, key` with the amounts from the newest save (slot 103) gave **exactly one
hit per reserve**, all five with this layout. The live values equalled the save values.

Value-independent signature: `00 00 00 00 <key u32> 00 98 AB 45 01 00 00 00`, amount at match −8.
It matches **two** objects per key: the real one, and one with amount 0 (purpose unknown).
Reader rule until that's understood: one non-zero match → use it; anything else → unknown (fail closed).

Not done yet:
- `0x145AB9800` is specific to this build and assumes image base `0x140000000`. Store it as a module-relative
  offset in `offsets/2.04.08/` and read the real module base.
- Confirm the value follows the game when a reserve changes (owner spends or collects something).
- Confirm in a second session.

### Second session: the signature failed (2026-11-11, pid 19392)

After the game was restarted, the same signature found only **Food** (7,304, plus its zero twin) and a zero
twin for Stone. Funds, Timber and Armaments were not found. So the pointer after the item key is **not** a
constant, or the objects are laid out differently between sessions. A looser search (header + amount + key,
no pointer) returned 361 candidates, almost all noise.

**Status: reserves in memory are not reliably locatable.** The C# reader's `MemorySource` is disabled and
returns `Unavailable`. `offsets/2.04.08/memory.json` records the layout seen in session one only.
Next step when this is picked up: capture the five objects again with known current amounts and compare the
bytes after the key across the two sessions.

## Dispatch operations: partly found

- The four `_operationKey` values from the save each appear 12–15 times in memory. The save's
  `_operationStartTimeRaw` values do not appear as u64, so timers are held differently in memory.
- A live node object for Oasis Reflection (key 4072557552) was found. Relative to the key:
  −0x18 f32 `+inf` (matches `_totalCombatPower` in the save), **+0x30 f32 progress**.
- Progress went 37.836 → 38.836 over 25 real seconds while the game ran. The save had 19.57 for this
  operation. So progress is live, and at that moment it moved about 1% per 25 s
  (about 14,400 save-clock ticks per real second, if 100% = 36,000,000 ticks holds).
- A second object with the same shape holds a fixed 34.89 and did not change. Probably a snapshot. Ignore it.
- The object called "Oasis Reflection" here is really `Node_Her_OakenshieldManor_Serkis_Work_Guard_Tier1`
  (see `mission-static-data.md`).
- Several other key hits were in scratch memory whose contents changed minutes later. Do not rely on them.
- Not yet found: a signature for node objects, the node key and worker list inside them, the live clock.

## Workers

Not looked for in memory yet. From the save (see `save-contents.md`): 52 human comrades, 38 with a camp spawn
position, 11 dispatched. The owner reports the game shows **37 workers including dispatched ones**. 38 is one
off; the rule for who counts is not known yet.

## Third session and C# reader (2026-11-11, pid 37568)

- A by-value scan found all five reserves, one hit each, with the same layout and the same pointer
  (`0x145AB9800`) as session one. So the signature holds; the session-two miss is attributed to the Python
  research scanner, which drops a whole 32 MB block when any page in it cannot be read.
- `reader/CampLedger.Reader/MemorySource.cs` implements the scan in C# with a page-by-page fallback. First
  run against the live game: **located 5 of 5 reserves in 31.4 s.** Live values equalled the save, so no
  separate "process" snapshot was written (it only writes when the live reserves differ from the last one).
- Not yet seen: a live value changing and being picked up, behaviour across fast travel or loading, and a
  launch through the ASI with this build.

## First full launcher session (2026-11-12, game 2.04.08)

The launcher ASI started the reader with the game (15:00:43) and it exited when the game closed (15:05:05).
Game data loaded (1,012 missions). Save snapshots were written at load and at two saves, the second showing the
owner's funds change (31,746 -> 71,756).

Live memory produced **no** snapshot, in this session or the 2026-11-11 one. Scans located the objects (5 of 5
on 11-11, 4 of 5 on 11-12), but the reader threw the whole result away: one address that stopped validating
made every reserve unknown, 4 of 5 counted as nothing, and stale copies with the same value counted as
conflicts. Scans take 30-40 s while the game keeps running, so some objects are freed or moved before the
scan ends. Changed: invalid addresses are dropped one by one, each located reserve is reported on its own,
copies that agree are accepted (disagreeing copies are still unknown), and partial scans retry every 5 minutes.
Not yet verified in a game session.

Note for checking runs: the Claude desktop app sees a private copy of %LOCALAPPDATA%, so the real
`%LOCALAPPDATA%\CampLedger` written by the game-launched reader must be read from outside that sandbox.

## Item object layout and owner chain (2026-11-12, game 2.04.08, one session so far)

Found with `tools/probe` (read-only, query + VM-read only) while the owner played. Steps: searched for the
owner-reported funds value (51,756), found an object with the 2026-11-11 header and key 11, and walked back to its
start; then compared all objects of that class.

- **The 2026-11-11 signature was wrong.** The "pointer 0x5AB9800" sits in a field that varies between objects
  (in one session it held `0x12_00000000`). That explains the misses and "0 of 5" scans.
- **Item object** (offsets from object start): class pointer `exe+0x591EA30` at +0, parent pointer at +0x28,
  header `01000000 01000000` at +0xD0, signed i64 amount at +0xD8, u32 0 at +0xE0, u32 item key at +0xE4.
  290 objects of this class existed; 18 had keys 11-15.
- **Owner chain:** parent has class `exe+0x5A50FA0`; the parent's own +0x28 is a single root object of class
  `exe+0x59FCB20` (shared by all five reserves; no parent of its own).
- Per reserve: one owned object with the balance; owned objects with negative amounts (a cost being paid:
  -20,000 funds, -1,850 food, -500 timber, -850 stone); two unowned positive copies of each cost (display);
  armaments had one owned 0 placeholder. Rule: owned and > 0 gives exactly one object per reserve.
- Values matched the owner's screen (funds 51,756) and the last save minus the costs shown.
- **No static pointer chain yet:** 61 objects point at the root, 1,431 at those, none in the exe. A chain to a
  static is deeper than two levels. The reader uses a scan for the class pointer plus the owner-chain check
  instead: about 10 s once (4 threads, private memory only), then direct re-reads.

Needs: a second session to confirm the class RVAs and offsets, and a check that the balance object follows a
live change.

## Second session and live change (2026-11-12 15:51-15:57, game 2.04.08)

- **Second session:** after a restart, the same class RVAs, owner chain and offsets located 5 of 5 reserves
  (15:54:05, 10.2 s). Objects appear a few minutes after load; earlier scans find none of the class at all.
- **Live change:** the owner gained 24 armaments (3,183 -> 3,207 on screen). Memory then held an owned balance
  object of 3,207 and an owned **positive** pending object of +24, so "positive means balance" was wrong.
- **Balance marker:** balance objects have a non-empty list at +0x30 (pointer, then u32 2, u32 2 at +0x38).
  Pending changes (-20,000 funds and +24 armaments) and zero placeholders have zeros there. Checked on objects
  from both sessions. Added to offsets/2.04.08/memory.json as `balanceListOffset`.
- With the marker, the game-launched reader wrote its first live snapshot (`source: process`) at 15:57:09:
  armaments 3,207 while the save still said 3,183. Values matched the owner's screen.
