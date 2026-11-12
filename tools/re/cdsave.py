"""Scratch: decode a Crimson Desert save COPY (read-only) and dump its embedded schema.

Format knowledge from public prior art (NattKh save_crypto.py / save_parser.py), reimplemented.
"""
import hashlib, hmac, struct, sys, json
import lz4.block
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms

BASE = bytes.fromhex("C41B8E730DF259A637CC04E9B12F9668DA107A853E61F9224DB80AD75C13EF90")[:31]
PREFIX = {1: b'^Qgbrm/.#@`zsr]\\@rvfal#"', 2: b"^Pearl--#Abyss__@!!"}


def key_for(v):
    m = PREFIX[v] + b"PRIVATE_HMAC_SECRET_CHECK"
    return bytes(a ^ b for a, b in zip(BASE, m)) + b"\x00"


def decode(path):
    with open(path, "rb") as f:
        d = f.read()
    assert d[:4] == b"SAVE"
    ver = struct.unpack_from("<H", d, 4)[0]
    usize, psize = struct.unpack_from("<II", d, 0x12)
    nonce = d[0x1A:0x2A]
    mac = d[0x2A:0x4A]
    k = key_for(ver)
    ct = d[0x80:0x80 + psize]
    comp = Cipher(algorithms.ChaCha20(k, nonce), mode=None).decryptor().update(ct)
    ok = hmac.compare_digest(hmac.new(k, comp, hashlib.sha256).digest(), mac)
    raw = lz4.block.decompress(comp, uncompressed_size=usize)
    return raw, {"version": ver, "hmac_ok": ok, "usize": usize, "psize": psize}


def u16(b, o): return struct.unpack_from("<H", b, o)[0]
def u32(b, o): return struct.unpack_from("<I", b, o)[0]


def schema(raw):
    pos = 0x0E
    pos += 4
    n = u16(raw, pos); pos += 2
    L = u32(raw, pos); pos += 4
    name = raw[pos:pos + L].decode("ascii", "replace"); pos += L
    types = []
    for i in range(n):
        fc = u16(raw, pos); pos += 2
        fields = []
        for _ in range(fc):
            L = u32(raw, pos); pos += 4
            fn = raw[pos:pos + L].decode("ascii", "replace"); pos += L
            L = u32(raw, pos); pos += 4
            tn = raw[pos:pos + L].decode("ascii", "replace"); pos += L
            kind, size, aux = struct.unpack_from("<HHI", raw, pos); pos += 8
            fields.append((fn, tn, kind, size, aux))
        types.append((name, fields))
        if i != n - 1:
            L = u32(raw, pos); pos += 4
            name = raw[pos:pos + L].decode("ascii", "replace"); pos += L
    return types, pos


def toc(raw, end, types):
    cnt = u32(raw, end + 4)
    out = []
    for i in range(cnt):
        ci, s1, s2, off, size = struct.unpack_from("<5I", raw, end + 12 + i * 20)
        out.append((i, ci, types[ci][0] if ci < len(types) else "?", off, size))
    return out


if __name__ == "__main__":
    raw, info = decode(sys.argv[1])
    print(json.dumps(info))
    if len(sys.argv) > 2:
        open(sys.argv[2], "wb").write(raw)
    types, end = schema(raw)
    print("types", len(types), "schema_end", hex(end))
    for i, (n, fs) in enumerate(types):
        print(f"T{i} {n}")
        for f in fs:
            print("    ", *f)
    print("== TOC")
    for e in toc(raw, end, types):
        print(*e)
