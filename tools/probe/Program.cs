using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Read-only memory probe for reverse-engineering pointer chains (docs/re-notes/live-memory.md).
// Opens the game with PROCESS_QUERY_INFORMATION | PROCESS_VM_READ only. Never writes, injects or hooks.
//
//   cdprobe find                          reserve objects (pointer 0x5AB9800 signature) with amounts
//   cdprobe dump <addr> [before] [after]  qwords around an address, pointers into the exe marked
//   cdprobe ptr <maxoff> <addr>...        8-aligned qwords v with addr-maxoff <= v <= addr, anywhere readable
//   cdprobe chain <rva> <off>...          follow exe+rva, then [p+off]..., print each step
//   cdprobe regions                       summary of scanned memory

static class P
{
    const uint VM_READ = 0x10, QUERY = 0x400, MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000, MEM_IMAGE = 0x1000000;
    const uint GUARD = 0x100, NOACCESS = 0x01, WRITABLE = 0x04 | 0x08 | 0x40 | 0x80;
    const long PointerRva = 0x5AB9800;

    [StructLayout(LayoutKind.Sequential)]
    struct MBI { public nint Base, AllocBase; public uint AllocProtect; public ushort Part; public nuint Size; public uint State, Protect, Type; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32.dll")] static extern nuint VirtualQueryEx(nint h, nint a, out MBI m, nuint l);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(nint h, nint a, byte[] b, nuint n, out nuint got);

    static nint H;
    static long ModBase, ModEnd;

    static int Main(string[] a)
    {
        var game = Process.GetProcessesByName("CrimsonDesert").FirstOrDefault();
        if (game is null) { Console.WriteLine("game not running"); return 1; }
        ModBase = game.MainModule!.BaseAddress; ModEnd = ModBase + game.MainModule.ModuleMemorySize;
        H = OpenProcess(VM_READ | QUERY, false, game.Id);
        if (H == 0) { Console.WriteLine("open refused"); return 1; }
        Console.WriteLine($"pid {game.Id} exe 0x{ModBase:X}-0x{ModEnd:X}");
        var sw = Stopwatch.StartNew();
        switch (a[0])
        {
            case "find": Find(); break;
            case "dump": Dump(Hex(a[1]), a.Length > 2 ? int.Parse(a[2]) : 64, a.Length > 3 ? int.Parse(a[3]) : 64); break;
            case "ptr": Ptr(int.Parse(a[1]), a.Skip(2).Select(Hex).ToArray()); break;
            case "chain": Chain(Hex(a[1]), a.Skip(2).Select(Hex).ToArray()); break;
            case "regions": Regions(); break;
            case "val": Val(long.Parse(a[1])); break;
            case "items": Items(Hex(a[1])); break;
        }
        Console.WriteLine($"({sw.Elapsed.TotalSeconds:F1} s)");
        return 0;
    }

    static long Hex(string s) => Convert.ToInt64(s.StartsWith("-") ? "-" + s[1..].Replace("0x", "") : s.Replace("0x", ""), 16);
    static string Where(long v) => v >= ModBase && v < ModEnd ? $"exe+0x{v - ModBase:X}" : $"0x{v:X}";

    static bool Read(long addr, byte[] buf, int n) => ReadProcessMemory(H, (nint)addr, buf, (nuint)n, out var got) && (int)got == n;
    static long Q(long addr) { var b = new byte[8]; return Read(addr, b, 8) ? BinaryPrimitives.ReadInt64LittleEndian(b) : 0; }

    /// <summary>Private writable memory plus the exe's own image (where statics live). Mapped files are skipped.</summary>
    static List<(long Start, long Size)> Scannable()
    {
        var list = new List<(long, long)>();
        nint addr = 0;
        while (VirtualQueryEx(H, addr, out var m, (nuint)Marshal.SizeOf<MBI>()) != 0)
        {
            long s = m.Base, n = (long)m.Size;
            bool ok = m.State == MEM_COMMIT && (m.Protect & (GUARD | NOACCESS)) == 0 &&
                      ((m.Type == MEM_PRIVATE && (m.Protect & WRITABLE) != 0) || (m.Type == MEM_IMAGE && s >= ModBase && s < ModEnd));
            if (ok) list.Add((s, n));
            long next = s + n;
            if (next <= addr || next >= 0x7FFFFFFFFFFF) break;
            addr = (nint)next;
        }
        return list;
    }

    static void Regions()
    {
        var r = Scannable();
        Console.WriteLine($"{r.Count} regions, {r.Sum(x => x.Size) / (1 << 20)} MB");
    }

    /// <summary>Runs <paramref name="visit"/> over every scannable byte in 4 MB pieces, in parallel.</summary>
    static void Scan(Action<byte[], int, long> visit)
    {
        const int Piece = 4 << 20;
        var pieces = new List<(long, int)>();
        foreach (var (s, n) in Scannable())
            for (long o = 0; o < n; o += Piece) pieces.Add((s + o, (int)Math.Min(Piece, n - o)));
        Parallel.ForEach(pieces, new ParallelOptions { MaxDegreeOfParallelism = 4 }, () => new byte[Piece], (p, _, buf) =>
        {
            var (at, len) = p;
            if (Read(at, buf, len)) visit(buf, len, at);
            else for (long q = at; q < at + len; q += 4096) if (Read(q, buf, 4096)) visit(buf, 4096, q);
            return buf;
        }, _ => { });
    }

    static void Find()
    {
        long ptr = ModBase + PointerRva;
        var hits = new ConcurrentBag<(long, long, uint)>();
        Scan((buf, len, at) =>
        {
            var s = buf.AsSpan(0, len);
            for (int i = 24; i + 8 <= len; i += 4)
            {
                if (BinaryPrimitives.ReadInt64LittleEndian(s.Slice(i)) != ptr) continue;
                if (BinaryPrimitives.ReadInt64LittleEndian(s.Slice(i - 24)) != 0x0000000100000001L) continue;
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(i - 4));
                if (BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(i - 8)) != 0 || key < 11 || key > 15) continue;
                hits.Add((at + i - 16, BinaryPrimitives.ReadInt64LittleEndian(s.Slice(i - 16)), key));
            }
        });
        foreach (var (addr, amount, key) in hits.OrderBy(h => h.Item3).ThenBy(h => h.Item1))
            Console.WriteLine($"key {key}  amount {amount,10}  at 0x{addr:X}");
    }

    /// <summary>Every 4-aligned u64 equal to n, with the qword before and the two after.</summary>
    static void Val(long n)
    {
        var hits = new ConcurrentBag<(long, long, long, long)>();
        Scan((buf, len, at) =>
        {
            var s = buf.AsSpan(0, len);
            for (int i = 8; i + 24 <= len; i += 4)
                if (BinaryPrimitives.ReadInt64LittleEndian(s.Slice(i)) == n)
                    hits.Add((at + i, BinaryPrimitives.ReadInt64LittleEndian(s.Slice(i - 8)),
                              BinaryPrimitives.ReadInt64LittleEndian(s.Slice(i + 8)), BinaryPrimitives.ReadInt64LittleEndian(s.Slice(i + 16))));
        });
        foreach (var (addr, b, a1, a2) in hits.OrderBy(h => h.Item1).Take(300))
            Console.WriteLine($"0x{addr:X}  before {b:X16}  after {a1:X16} {a2:X16}{(a2 >= ModBase && a2 < ModEnd ? "  -> " + Where(a2) : "")}");
        Console.WriteLine($"{hits.Count} hits");
    }

    /// <summary>Objects starting with vtable exe+rva: amount at +0xD8 (after header 1,1 at +0xD0), item key at +0xE4.</summary>
    static void Items(long rva)
    {
        long vt = ModBase + rva;
        var hits = new ConcurrentBag<(long, long, uint, bool)>();
        Scan((buf, len, at) =>
        {
            var q = MemoryMarshal.Cast<byte, long>(buf.AsSpan(0, len & ~7));
            for (int i = 0; i + 0x1D < q.Length; i++)
            {
                if (q[i] != vt) continue;
                uint key = (uint)((ulong)q[i + 0x1C] >> 32);
                hits.Add((at + i * 8L, q[i + 0x1B], key, q[i + 0x1A] == 0x0000000100000001L));
            }
        });
        Console.WriteLine($"{hits.Count} objects with this vtable");
        foreach (var (addr, amount, key, hdr) in hits.Where(h => h.Item3 is >= 11 and <= 15).OrderBy(h => h.Item3).ThenBy(h => h.Item1))
        {
            long owner = Q(addr + 0x28), ownerVt = owner == 0 ? 0 : Q(owner), root = owner == 0 ? 0 : Q(owner + 0x28);
            long rootVt = root == 0 ? 0 : Q(root);
            Console.WriteLine($"key {key}  amount {amount,10}  header {(hdr ? "ok" : "--")}  object 0x{addr:X}  " +
                              $"owner {(owner == 0 ? "-" : Where(ownerVt))}  root {(root == 0 ? "-" : $"0x{root:X} {Where(rootVt)}")}");
        }
    }

    static void Dump(long addr, int before, int after)
    {
        var b = new byte[before + after];
        if (!Read(addr - before, b, b.Length)) { Console.WriteLine("unreadable"); return; }
        for (int o = 0; o + 8 <= b.Length; o += 8)
        {
            long v = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(o));
            string note = v >= ModBase && v < ModEnd ? "  -> " + Where(v) : "";
            Console.WriteLine($"{o - before,+5:+0;-0}  0x{addr - before + o:X}  {v:X16}{note}");
        }
    }

    static void Ptr(int maxOff, long[] targets)
    {
        Array.Sort(targets);
        long lo = targets[0] - maxOff, hi = targets[^1];
        var hits = new ConcurrentBag<(long Loc, long Target, long Off)>();
        Scan((buf, len, at) =>
        {
            var q = MemoryMarshal.Cast<byte, long>(buf.AsSpan(0, len & ~7));
            for (int i = 0; i < q.Length; i++)
            {
                long v = q[i];
                if ((ulong)(v - lo) > (ulong)(hi - lo)) continue;
                int k = Array.BinarySearch(targets, v);
                if (k < 0) k = ~k;                  // first target >= v
                if (k < targets.Length && targets[k] - v <= maxOff) hits.Add((at + i * 8L, targets[k], targets[k] - v));
            }
        });
        foreach (var h in hits.OrderBy(h => h.Loc >= ModBase && h.Loc < ModEnd ? 0 : 1).ThenBy(h => h.Off).Take(200))
            Console.WriteLine($"{Where(h.Loc),-22} -> 0x{h.Target:X} - 0x{h.Off:X}");
        Console.WriteLine($"{hits.Count} hits ({hits.Count(h => h.Loc >= ModBase && h.Loc < ModEnd)} in exe)");
    }

    static void Chain(long rva, long[] offs)
    {
        long p = ModBase + rva;
        Console.WriteLine($"exe+0x{rva:X} = 0x{p:X}");
        foreach (long o in offs)
        {
            long v = Q(p);
            Console.WriteLine($"[0x{p:X}] = 0x{v:X}  (+0x{o:X})");
            if (v == 0) return;
            p = v + o;
        }
        Console.WriteLine($"end 0x{p:X}: amount {Q(p)}");
    }
}
