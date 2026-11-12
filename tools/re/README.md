# tools/re: research scripts

Scratch tools used for `docs/re-notes/save-contents.md`. They are **not** the product reader: the stack for
the reader is still undecided (see AGENTS.md). Only run them on a **copy** of a save. They only read.

- `cdsave.py`: decrypt and decompress a `.save` file, then dump its embedded schema and TOC.
- `cdbody.py`: strict decoder for every top-level block. Raises on anything it does not expect.
- `camp.py`: pulls out camp reserves, active operations, the clock and mercenary counts as JSON lines.

Dependencies: `lz4` (LZ4 block decompression), `cryptography` (ChaCha20).
