"""Tier 2 research: external, READ-ONLY process memory scanner.

Opens the target with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only. No write, no injection,
no thread or handle operations. If the open or a read is refused, it reports and stops: do not
work around it (AGENTS.md hard rule 2).

  memscan.py probe
  memscan.py near u32:11 u64:31126 [--within 64]      anchor first, companions after
  memscan.py pat funds=<hex> food=<hex>               exact byte patterns, one pass
  memscan.py dump 0x1234abcd 256
"""
import argparse, ctypes, struct, sys
from ctypes import wintypes as wt

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
MEM_COMMIT = 0x1000
PAGE_GUARD = 0x100
PAGE_NOACCESS = 0x01
WRITABLE = 0x04 | 0x08 | 0x40 | 0x80  # RW, WC, XRW, XWC
TH32CS_SNAPPROCESS = 0x2

k32 = ctypes.WinDLL("kernel32", use_last_error=True)


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", wt.DWORD), ("PartitionId", wt.WORD),
                ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD)]


class PE32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.c_void_p), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", wt.LONG), ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_char * 260)]


k32.OpenProcess.restype = wt.HANDLE
k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
k32.VirtualQueryEx.restype = ctypes.c_size_t
k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(MBI), ctypes.c_size_t]
k32.ReadProcessMemory.restype = wt.BOOL
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                  ctypes.POINTER(ctypes.c_size_t)]
k32.CreateToolhelp32Snapshot.restype = wt.HANDLE
k32.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]
k32.Process32First.argtypes = [wt.HANDLE, ctypes.POINTER(PE32)]
k32.Process32Next.argtypes = [wt.HANDLE, ctypes.POINTER(PE32)]
k32.CloseHandle.argtypes = [wt.HANDLE]


def find_pid(name):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    pe = PE32(); pe.dwSize = ctypes.sizeof(PE32)
    pids = []
    ok = k32.Process32First(snap, ctypes.byref(pe))
    while ok:
        if pe.szExeFile.decode("mbcs", "replace").lower() == name.lower():
            pids.append(pe.th32ProcessID)
        ok = k32.Process32Next(snap, ctypes.byref(pe))
    k32.CloseHandle(snap)
    return pids


class Proc:
    def __init__(self, pid):
        self.h = k32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, False, pid)
        if not self.h:
            raise OSError(f"OpenProcess(read-only) refused, error {ctypes.get_last_error()}. Stopping.")

    def regions(self, writable_only=True):
        addr = 0
        mbi = MBI()
        while k32.VirtualQueryEx(self.h, addr, ctypes.byref(mbi), ctypes.sizeof(mbi)):
            base, size = mbi.BaseAddress or 0, mbi.RegionSize
            if (mbi.State == MEM_COMMIT and not mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)
                    and (not writable_only or mbi.Protect & WRITABLE)):
                yield base, size, mbi.Type
            addr = base + size
            if addr >= 0x7FFFFFFFFFFF:
                break

    def read(self, addr, size):
        buf = ctypes.create_string_buffer(size)
        got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(self.h, addr, buf, size, ctypes.byref(got)):
            return None
        return buf.raw[:got.value]

    def chunks(self, step=32 << 20, overlap=4096):
        for base, size, _ in self.regions():
            off = 0
            while off < size:
                n = min(step + overlap, size - off)
                data = self.read(base + off, n)
                if data:
                    yield base + off, data
                off += step


FMT = {"u8": "<B", "u16": "<H", "u32": "<I", "u64": "<Q", "i32": "<i", "i64": "<q", "f32": "<f", "f64": "<d"}


def parse_val(s):
    t, v = s.split(":")
    v = float(v) if t.startswith("f") else int(v, 0)
    return s, struct.pack(FMT[t], v)


def cmd_probe(p, a):
    tot = n = 0
    for base, size, _ in p.regions():
        tot += size; n += 1
    print(f"opened read-only. writable committed regions: {n}, {tot / 2**30:.2f} GiB")


def cmd_near(p, a):
    (aname, apat), comps = parse_val(a.values[0]), [parse_val(v) for v in a.values[1:]]
    w = a.within
    hits = 0
    for base, data in p.chunks():
        i = data.find(apat)
        while i != -1:
            if (base + i) % a.align == 0:
                lo, hi = max(0, i - w), min(len(data), i + w + len(apat))
                win = data[lo:hi]
                offs = []
                for cname, cpat in comps:
                    j = win.find(cpat)
                    found = []
                    while j != -1:
                        found.append(lo + j - i)
                        j = win.find(cpat, j + 1)
                    if not found:
                        break
                    offs.append((cname, found))
                else:
                    hits += 1
                    print(f"{base + i:#014x} " + " ".join(f"{c}@{','.join(f'{o:+d}' for o in f)}" for c, f in offs))
                    if hits >= a.limit:
                        print("limit reached"); return
            i = data.find(apat, i + 1)
    print("hits", hits)


def cmd_pat(p, a):
    """Exact byte patterns, all searched in one pass. name=hex ..."""
    pats = [(n, bytes.fromhex(h)) for n, h in (x.split("=") for x in a.patterns)]
    for base, data in p.chunks():
        for n, pat in pats:
            i = data.find(pat)
            while i != -1:
                print(f"{n} {base + i:#014x}")
                i = data.find(pat, i + 1)


def cmd_dump(p, a):
    addr = int(a.addr, 0)
    data = p.read(addr, a.size)
    if data is None:
        print("unreadable"); return
    for o in range(0, len(data), 16):
        row = data[o:o + 16]
        print(f"{addr + o:#014x}  {row.hex(' '):<47}  {''.join(chr(c) if 32 <= c < 127 else '.' for c in row)}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default="CrimsonDesert.exe")
    ap.add_argument("--pid", type=int)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("probe")
    s = sub.add_parser("near"); s.add_argument("values", nargs="+"); s.add_argument("--within", type=int, default=64)
    s.add_argument("--align", type=int, default=4); s.add_argument("--limit", type=int, default=200)
    s = sub.add_parser("pat"); s.add_argument("patterns", nargs="+")
    s = sub.add_parser("dump"); s.add_argument("addr"); s.add_argument("size", type=int)
    a = ap.parse_args()
    pid = a.pid
    if pid is None:
        pids = find_pid(a.exe)
        if not pids:
            sys.exit(f"{a.exe} is not running")
        pid = pids[0]
    print(f"pid {pid}")
    try:
        p = Proc(pid)
    except OSError as e:
        sys.exit(str(e))
    {"probe": cmd_probe, "near": cmd_near, "dump": cmd_dump, "pat": cmd_pat}[a.cmd](p, a)
