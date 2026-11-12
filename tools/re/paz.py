"""Read-only reader for the game's PAZ archives (index: <group>/0.pamt, data: <group>/<n>.paz).

Format knowledge from reading NattKh paz_parse.py; reimplemented. Never writes to the install.
Extracted bytes are game data: keep them out of the repo (AGENTS.md hard rule 4).

  paz.py list <game dir> [substring ...]
  paz.py extract <game dir> <path substring> <out dir>
"""
import glob, os, struct, sys


def _names(d, off, size):
    """Name trie (2.04 layout): each entry is LEB128 back-distance to its parent (0 = root), u8 length, bytes."""
    start, nodes = off, {}
    while off < start + size:
        rel = off - start
        dist = shift = 0
        while True:
            b = d[off]; off += 1
            dist |= (b & 0x7F) << shift; shift += 7
            if not b & 0x80:
                break
        n = d[off]; off += 1
        nodes[rel] = (rel - dist if dist else None, d[off:off + n].decode("utf-8", "replace"))
        off += n

    def path(ref):
        parts = []
        while ref is not None and len(parts) < 128:
            ref, name = nodes[ref]
            parts.append(name)
        return "".join(reversed(parts))
    return path, off


def parse_pamt(pamt):
    """Layout as observed on game 2.04.08 (differs from the older layout in public parsers)."""
    with open(pamt, "rb") as f:
        d = f.read()
    paz_count = struct.unpack_from("<I", d, 4)[0]
    off = 12 + paz_count * 12
    fsize = struct.unpack_from("<I", d, off)[0]; off += 4
    fpath, off = _names(d, off, fsize)
    nsize = struct.unpack_from("<I", d, off)[0]; off += 4
    npath, off = _names(d, off, nsize)
    nfold = struct.unpack_from("<I", d, off)[0]; off += 4
    folders = [struct.unpack_from("<4I", d, off + i * 16) for i in range(nfold)]  # hash, name, first, count
    off += nfold * 16
    nfiles = struct.unpack_from("<I", d, off)[0]; off += 4
    recs = off
    exts = d[recs + nfiles * 20:].split(bytes(1))
    owner = {}
    for _, name, first, count in folders:
        for i in range(first, first + count):
            owner[i] = name
    stem = int(os.path.splitext(os.path.basename(pamt))[0])
    out = []
    for i in range(nfiles):
        ref, poff, csize, osize, flags = struct.unpack_from("<5I", d, recs + i * 20)
        out.append({"path": f"{fpath(owner[i])}/{npath(ref)}" if i in owner else npath(ref),
                    "paz": os.path.join(os.path.dirname(pamt), f"{stem + (flags & 0xFF)}.paz"),
                    "offset": poff, "csize": csize, "osize": osize, "flags": flags, "exts": exts})
    return out


def index(game):
    out = []
    pats = [os.path.join(game, "[0-9]*", "0.pamt"), os.path.join(game, "dlc", "*", "[0-9]*", "0.pamt")]
    for pamt in sorted(p for pat in pats for p in glob.glob(pat)):
        try:
            out += parse_pamt(pamt)
        except Exception as e:  # report, do not guess
            print(f"could not parse {pamt}: {e}", file=sys.stderr)
    return out


def read_entry(e):
    with open(e["paz"], "rb") as f:
        f.seek(e["offset"])
        data = f.read(e["csize"])
    if e["csize"] != e["osize"]:
        ctype = (e["flags"] >> 16) & 0x0F
        if ctype != 2:
            raise ValueError(f"unsupported compression type {ctype}")
        import lz4.block
        data = lz4.block.decompress(data, uncompressed_size=e["osize"])
    return data


if __name__ == "__main__":
    cmd, game = sys.argv[1], sys.argv[2]
    entries = index(game)
    if cmd == "list":
        subs = [s.lower() for s in sys.argv[3:]]
        for e in entries:
            if not subs or any(s in e["path"].lower() for s in subs):
                print(f'{e["osize"]:>10} {e["flags"]:#010x} {os.path.relpath(e["paz"], game)} {e["path"]}')
        print(len(entries), "entries total", file=sys.stderr)
    elif cmd == "extract":
        sub, outdir = sys.argv[3].lower(), sys.argv[4]
        os.makedirs(outdir, exist_ok=True)
        for e in entries:
            if sub in e["path"].lower():
                dst = os.path.join(outdir, e["path"].replace("/", "__"))
                with open(dst, "wb") as f:
                    f.write(read_entry(e))
                print("wrote", dst)
