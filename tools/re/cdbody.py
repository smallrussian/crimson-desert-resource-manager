"""Strict body decoder (absence-marker rule). Raises on any shape it does not understand."""
import struct, sys, json
from cdsave import decode, schema, toc, u16, u32


class DecodeError(Exception):
    pass


def scalar(tn, size, b):
    t = tn.lower()
    if t == "bool" and size == 1:
        return bool(b[0])
    if "float" in t and size == 4 and tn in ("float",):
        return struct.unpack("<f", b)[0]
    if t in ("double",) and size == 8:
        return struct.unpack("<d", b)[0]
    if size in (1, 2, 4, 8):
        fmt = {1: "b", 2: "h", 4: "i", 8: "q"}[size]
        if not t.startswith("int"):
            fmt = fmt.upper()
        return struct.unpack("<" + fmt, b)[0]
    if t == "float3" and size == 12:
        return list(struct.unpack("<3f", b))
    if t == "quaternion" and size == 16:
        return list(struct.unpack("<4f", b))
    return b.hex()


class Body:
    def __init__(self, raw, types):
        self.raw = raw
        self.types = types

    def present(self, mask, i):
        return i // 8 < len(mask) and (mask[i // 8] >> (i % 8)) & 1

    def fields(self, tidx, mask, c, end):
        """Decode fields of type tidx starting at c. Returns (dict, cursor)."""
        raw = self.raw
        name, fdefs = self.types[tidx]
        out = {}
        for i, (fn, tn, kind, size, aux) in enumerate(fdefs):
            if not self.present(mask, i):
                if kind in (3, 6, 7):
                    if raw[c] != 1:
                        raise DecodeError(f"{name}.{fn}: expected absence marker 01 at {c:#x}, got {raw[c]:02x}")
                    c += 1
                continue
            if kind in (0, 2):
                out[fn] = scalar(tn, size, raw[c:c + size]); c += size
            elif kind == 1:
                n = u32(raw, c); c += 4
                data = raw[c:c + n * size]; c += n * size
                out[fn] = data.rstrip(b"\0").decode("utf-8", "replace") if size == 1 else data.hex()
            elif kind == 3:
                if raw[c] != 0:
                    raise DecodeError(f"{name}.{fn}: array tag {raw[c]:02x} at {c:#x}")
                n = u32(raw, c + 1); c += 5
                if n > 1_000_000:
                    raise DecodeError(f"{name}.{fn}: absurd count {n}")
                vals = [scalar(tn, size, raw[c + k * size:c + (k + 1) * size]) for k in range(n)]
                c += n * size
                out[fn] = vals
            elif kind in (4, 5):
                if kind == 5:  # pointer: 1-byte prefix (observed 0x01) before the wrapper
                    if raw[c] != 1:
                        raise DecodeError(f"{name}.{fn}: pointer prefix {raw[c]:02x} at {c:#x}")
                    c += 1
                obj, c = self.locator(c, end, f"{name}.{fn}")
                out[fn] = obj
            elif kind in (6, 7):
                if raw[c] != 0:
                    raise DecodeError(f"{name}.{fn}: list tag {raw[c]:02x} at {c:#x}")
                n = u32(raw, c + 1)
                hdr = raw[c + 5:c + 18]
                c += 18
                if n > 1_000_000:
                    raise DecodeError(f"{name}.{fn}: absurd list count {n}")
                lst = []
                for k in range(n):
                    obj, c = self.locator(c, end, f"{name}.{fn}[{k}]")
                    lst.append(obj)
                out[fn] = lst
            else:
                raise DecodeError(f"{name}.{fn}: unknown kind {kind}")
            if c > end:
                raise DecodeError(f"{name}.{fn}: overran region at {c:#x} > {end:#x}")
        return out, c

    def locator(self, c, end, where):
        raw = self.raw
        mc = u16(raw, c)
        if not (1 <= mc <= 16):
            raise DecodeError(f"{where}: bad child mask count {mc} at {c:#x}: {raw[c:c+24].hex()}")
        mask = raw[c + 2:c + 2 + mc]
        p = c + 2 + mc
        tidx = u16(raw, p)
        if tidx >= len(self.types):
            raise DecodeError(f"{where}: bad child type {tidx} at {c:#x}")
        # u8 reserved, u32 s1, u32 s2, u32 payload_offset
        payload = p + 2 + 1 + 4 + 4 + 4
        start = payload
        c = payload + 4  # reserved u32
        obj, c = self.fields(tidx, mask, c, end)
        size = u32(raw, c)
        if size != c - start:
            raise DecodeError(f"{where} ({self.types[tidx][0]}): trailing size {size} != {c - start} at {c:#x}")
        obj = {"__type": self.types[tidx][0], **obj}
        return obj, c + 4

    def block(self, ci, off, size):
        raw = self.raw
        mc = u16(raw, off)
        mask = raw[off + 2:off + 2 + mc]
        c = off + 2 + mc + 4
        obj, c = self.fields(ci, mask, c, off + size)
        return obj, c - (off + size)


def load(path):
    raw, info = decode(path)
    types, end = schema(raw)
    return raw, info, types, toc(raw, end, types)


if __name__ == "__main__":
    raw, info, types, entries = load(sys.argv[1])
    want = sys.argv[2] if len(sys.argv) > 2 else None
    b = Body(raw, types)
    ok = bad = 0
    for i, ci, cname, off, size in entries:
        if want and cname != want:
            continue
        try:
            obj, slack = b.block(ci, off, size)
            ok += 1
            if slack != 0:
                print(f"#{i} {cname}: slack {slack}")
            if want and len(sys.argv) > 3:
                json.dump(obj, open(sys.argv[3], "w"), indent=1, default=str)
        except DecodeError as e:
            bad += 1
            print(f"#{i} {cname}: {e}")
    print("ok", ok, "bad", bad)
