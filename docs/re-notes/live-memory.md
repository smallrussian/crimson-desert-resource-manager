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
