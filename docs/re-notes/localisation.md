# English display names (localisation)

Game 2.04.08, Steam. Checked 2026-11-12. Read-only; nothing extracted is kept in the repo.

## Where the strings are

- String tables: `gamedata/stringtable/binary__/<lang>/<table>` (77 tables per language, 15 languages:
  `eng`, `kor`, `ger`, `fre`, …). DLC copies are named `<table>.dlc_01`.
- Each language is its own small archive group: English is `0020` (base) and `dlc/dlc_01/0020`. Their
  `0.pamt` indexes are about 1.5 KB, so the reader indexes every group whose index is under 16 KB plus `0008`.
- PAMT flags for these entries are `0x01300000`: no compression at the archive level, but the bytes are
  encrypted.

## Decryption

Method published in lazorr410/crimson-desert-unpacker, `PAZ_DECRYPTION.md` (read 2026-11-12; reimplemented,
no code copied):

- ChaCha20, 32-byte key, 16-byte IV (OpenSSL layout: u32 counter then 12-byte nonce).
- `seed = hashlittle(lowercase file name, 0xC5EDE)` (Bob Jenkins lookup3).
- `iv = seed` (u32 LE) four times. `key[i] = seed ^ 0x60616263 ^ delta[i]`, deltas
  `0, 0x0A0A0A0A, 0x0C0C0C0C, 0x06060606, 0x0E0E0E0E, 0x0A0A0A0A, 0x06060606, 0x02020202`.
- The file name the key uses is the table name plus `.paloc`: `faction.paloc`, and for DLC copies the full
  name, `faction.dlc_01.paloc`. With the bare name (`faction`) the output is noise; with `.paloc` it starts
  with the magic `paloc`. All 77 English tables decrypt and parse.

## Container (after decryption)

| Offset | Content |
|---|---|
| 0 | `paloc` magic |
| 9 | u32 packed size |
| 13 | u32 raw size |
| 0x200 | LZ4 block, `packed` bytes; 0x200 + packed = file size on every table checked |

Raw body: entries of `u64 tag` (0x21 or 0x31 seen), `u32 length + key as decimal digits`, `u32 length +
UTF-8 text`, then a final `u32 entry count`. The count matched the parsed entries on all 77 tables
(faction: 1034, faction.dlc_01: 329, factionoperationgroup: 9).

Numeric keys are `(record key << 32) | field tag`. Examples: faction group Hernand = `0x4240 << 32 | 0x214`;
region Southern Quarry = `0xA0EA << 32 | 0x110`.

## Mission type names

Each operation record in `factionnode` has `u32 length + decimal digits` 13 bytes after the operation key
(noted earlier as "probably the localisation key"). It is: all 1,112 such keys found in base + DLC
`factionnode` exist in the English `faction` table. Examples: Burnhand Quarry stone → "Advanced Mining",
South Quarry → "Hasty Mining", Oakenshield guard → "Estate Protection".

## Location names

Not in `faction`. They are in `region` (and duplicated in `knowledge`). The link from a mission node:

1. The node's internal name, e.g. `Node_Her_SouthQuarry` (factionnode record 1000070).
2. The `regioninfo` record named `Region_Node_Her_SouthQuarry` (or, for some nodes such as
   `Node_Cal_CalphadeCastle`, a record with the node name itself). `regioninfo` uses **u16 keys**: its index
   entries are 6 bytes (u16 key, u32 offset), unlike the 8-byte entries of other tables.
3. That record holds `u32 length + "176926882791696"` = `0xA0EA << 32 | 0x110`, i.e. its own key with tag
   0x110. The reader only accepts a digit string that encodes the record's own key.
4. The English `region` table maps that key to "Southern Quarry".

Coverage: 266 of 321 non-recapture operations get a location (775 of 1021 counting recapture). The ones
without are mostly ocean islands, Greymane Camp's own builds and a few stations; for those `location` is null
and `name` falls back to the mission type.

## Verified against the game

The reader's snapshot of the owner's save on 2026-11-12 gave, for the running missions:

| name (location) | type | owner's sheet |
|---|---|---|
| Oakenshield Manor | Estate Protection | Oakenshield Manor, Estate Protection |
| Nas River Fishing Dock | Freshwater Fishing | Nas river Fishing Dock, Freshwater Fishing |
| Southern Quarry | Hasty Mining | Southern Quarry, Hasty Mining |
| Glenbright Farm | Simple Farming | Glenbright Farm, Simple Farming |
| (no location) | Howling Hill Camp Pond Construction | not in the sheet |

The sheet also lists Hillside Manor (Estate Protection) and Calphade Logging Camp (Large-Scale Logging);
both names come out of the tables the same way.
