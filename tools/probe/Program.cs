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
        if (a[0] == "static") return Static(a.Skip(1).ToArray());
        if (a[0] == "node") return NodeBytes(uint.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]));
        if (a[0] == "names") return Names(a.Skip(1).Select(uint.Parse).ToArray());
        if (a[0] == "durations")
        {
            var cat = new CampLedger.Reader.Catalogue(new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!));
            var ops = Enumerable.Range(0, 1).SelectMany(_ => typeof(CampLedger.Reader.Catalogue)
                .GetField("_operations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(cat) is Dictionary<uint, CampLedger.Reader.OperationInfo> d ? d.Values : Enumerable.Empty<CampLedger.Reader.OperationInfo>())
                .Where(o => o.Category is not null).ToList();
            Console.WriteLine($"{ops.Count} missions, {ops.Count(o => o.DurationHours is not null)} with a duration");
            foreach (var g in ops.GroupBy(o => o.Category).OrderBy(g => g.Key))
                Console.WriteLine($"  {g.Key,-12} {g.Count(o => o.DurationHours is not null),3}/{g.Count(),3}  e.g. " +
                    string.Join(", ", g.Where(o => o.DurationHours is not null).Take(3).Select(o => $"{o.InternalName?.Replace("Node_", "")} {o.DurationHours}h")));
            Console.WriteLine("missing:");
            foreach (var o in ops.Where(o => o.DurationHours is null).Take(25)) Console.WriteLine($"  {o.Category,-12} {o.InternalName}");
            Console.WriteLine("range: " + string.Join(", ", ops.Where(o => o.DurationHours is not null).GroupBy(o => o.DurationHours).OrderBy(g => g.Key).Select(g => $"{g.Key}h x{g.Count()}")));
            return 0;
        }
        if (a[0] == "nodefind")
        {
            var t = new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!).Tables("factionnode")
                .Last(t => t.Records.ContainsKey(uint.Parse(a[1])));
            var (s, en) = t.Records[uint.Parse(a[1])];
            var r = t.Body.AsSpan(s, en - s);
            var pat = BitConverter.GetBytes(uint.Parse(a[2]));
            for (int i = 0; i + 4 <= r.Length; i++) if (r.Slice(i, 4).SequenceEqual(pat)) Console.WriteLine($"at {i}");
            return 0;
        }
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
            case "pair": Pair(uint.Parse(a[1]), uint.Parse(a[2]), int.Parse(a[3])); break;
            case "track": Track(int.Parse(a[1])); break;
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

    /// <summary>4-aligned u32 a with u32 b within +-window bytes. Prints a's address and b's offset from it.</summary>
    static void Pair(uint x, uint y, int window)
    {
        var hits = new ConcurrentBag<(long, int)>();
        Scan((buf, len, at) =>
        {
            var u = MemoryMarshal.Cast<byte, uint>(buf.AsSpan(0, len & ~3));
            int w = window / 4;
            for (int i = 0; i < u.Length; i++)
            {
                if (u[i] != x) continue;
                for (int j = Math.Max(0, i - w); j < Math.Min(u.Length, i + w + 1); j++)
                    if (u[j] == y) hits.Add((at + i * 4L, (j - i) * 4));
            }
        });
        foreach (var (addr, off) in hits.OrderBy(h => h.Item1))
            Console.WriteLine($"0x{addr:X}  other at {off:+0;-0} ");
        Console.WriteLine($"{hits.Count} hits");
    }

    /// <summary>
    /// Records every 8-aligned double in (0.01, 5000) with a fractional part, waits, records again, and groups the
    /// addresses that increased by the same amount. Timers that run on one clock land in one group.
    /// </summary>
    static void Track(int seconds)
    {
        Dictionary<long, double> Snap()
        {
            var bag = new ConcurrentBag<(long, double)>();
            Scan((buf, len, at) =>
            {
                var q = MemoryMarshal.Cast<byte, double>(buf.AsSpan(0, len & ~7));
                for (int i = 0; i < q.Length; i++)
                {
                    double d = q[i];
                    if (d > 0.01 && d < 5000 && d != Math.Floor(d)) bag.Add((at + i * 8L, d));
                }
            });
            var dict = new Dictionary<long, double>(bag.Count);
            foreach (var (k, v) in bag) dict[k] = v;
            return dict;
        }
        var first = Snap();
        Console.WriteLine($"first pass: {first.Count} candidates; waiting {seconds} s");
        Thread.Sleep(seconds * 1000);
        var second = Snap();
        var groups = new Dictionary<double, List<(long, double, double)>>();
        foreach (var (addr, v2) in second)
        {
            if (!first.TryGetValue(addr, out double v1) || v2 <= v1) continue;
            double delta = Math.Round(v2 - v1, 6);
            if (!groups.TryGetValue(delta, out var g)) groups[delta] = g = new();
            g.Add((addr, v1, v2));
        }
        // Mission timers: a handful of distinct values (each mission is at a different point), all in a plausible range.
        foreach (var (delta, g) in groups.Where(g => g.Value.Count is >= 3 and <= 12
                     && g.Value.Select(x => Math.Round(x.Item2, 3)).Distinct().Count() >= 3
                     && g.Value.All(x => x.Item3 < 2000)).OrderByDescending(g => g.Value.Count).Take(25))
        {
            Console.WriteLine($"delta {delta}: {g.Count} addresses");
            foreach (var (addr, v1, v2) in g.OrderBy(x => x.Item1).Take(12))
                Console.WriteLine($"   0x{addr:X}  {v1:G8} -> {v2:G8}");
        }
    }

    /// <summary>
    /// Offline: for operations whose internal name contains each given text, lists where 'hours' appears in the
    /// operation's factionnode record under common encodings. Usage: static Quarry=12 Burnhand=22 ...
    /// </summary>
    static int Static(string[] specs)
    {
        string install = CampLedger.Reader.GameInstall.Find(null) ?? throw new InvalidOperationException("no install");
        var archive = new CampLedger.Reader.Archive(install);
        var tables = archive.Tables("factionnode");
        var cat = new CampLedger.Reader.Catalogue(archive);
        foreach (var spec in specs)
        {
            var (text, hours) = (spec.Split('=')[0], double.Parse(spec.Split('=')[1]));
            var enc = new (string, byte[])[]
            {
                ("u32 h", BitConverter.GetBytes((uint)hours)), ("u32 h*10", BitConverter.GetBytes((uint)(hours * 10))),
                ("u32 min", BitConverter.GetBytes((uint)(hours * 60))), ("u32 s", BitConverter.GetBytes((uint)(hours * 3600))),
                ("u32 ms", BitConverter.GetBytes((uint)(hours * 3_600_000))), ("u64 ms", BitConverter.GetBytes((ulong)(hours * 3_600_000))),
                ("f32 h", BitConverter.GetBytes((float)hours)), ("f32 h*10", BitConverter.GetBytes((float)(hours * 10))),
                ("f64 h", BitConverter.GetBytes(hours)), ("f64 h*10", BitConverter.GetBytes(hours * 10)),
                ("f32 s", BitConverter.GetBytes((float)(hours * 3600))),
            };
            for (uint key = 0; key < uint.MaxValue; key++) { break; }
            foreach (var table in tables)
                foreach (var (node, (start, end)) in table.Records)
                {
                    var rec = table.Body.AsSpan(start, end - start);
                    string ascii = System.Text.Encoding.ASCII.GetString(rec);
                    int at = ascii.IndexOf(text, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) continue;
                    Console.WriteLine($"== node {node} ({rec.Length} bytes), '{text}' at {at}, looking for {hours} h");
                    foreach (var (name, pat) in enc)
                    {
                        var hitsAt = new List<int>();
                        for (int i = rec.IndexOf(pat); i >= 0 && hitsAt.Count < 20; )
                        {
                            hitsAt.Add(i);
                            int n = rec.Slice(i + 1).IndexOf(pat);
                            i = n < 0 ? -1 : i + 1 + n;
                        }
                        if (hitsAt.Count > 0 && name != "u32 h") Console.WriteLine($"   {name,-9} at {string.Join(", ", hitsAt)}");
                    }
                }
        }
        return 0;
    }

    /// <summary>Offline: every length-prefixed Node_ name in a record, with the u32 40 bytes after it ends.</summary>
    static int Names(uint[] nodes)
    {
        var archive = new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!);
        var tables = archive.Tables("factionnode");
        foreach (uint node in nodes)
        {
            var table = tables.Last(t => t.Records.ContainsKey(node));
            var (start, end) = table.Records[node];
            var rec = table.Body.AsSpan(start, end - start);
            Console.WriteLine($"== node {node} ({rec.Length} bytes)");
            for (int i = 0; i + 9 < rec.Length; i++)
            {
                int n = (int)BitConverter.ToUInt32(rec.Slice(i, 4));
                if (n is < 8 or > 120 || i + 4 + n > rec.Length || !rec.Slice(i + 4).StartsWith("Node_"u8)) continue;
                string name = System.Text.Encoding.ASCII.GetString(rec.Slice(i + 4, n));
                int e = i + 4 + n;
                string tail = e + 44 <= rec.Length
                    ? $"marker {Convert.ToHexString(rec.Slice(e, 8))}  +32 {rec[e + 32]:X2}  +40 u32 {BitConverter.ToUInt32(rec.Slice(e + 40, 4))}"
                    : "(end)";
                Console.WriteLine($"   {i,5} {name,-50} {tail}");
            }
        }
        return 0;
    }

    /// <summary>Offline: bytes of a factionnode record around an offset, 4 per line, with ASCII.</summary>
    static int NodeBytes(uint node, int at, int before, int after)
    {
        var archive = new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!);
        var table = archive.Tables("factionnode").Last(t => t.Records.ContainsKey(node));
        var (start, end) = table.Records[node];
        var rec = table.Body.AsSpan(start, end - start);
        for (int o = Math.Max(0, at - before); o < Math.Min(rec.Length - 3, at + after); o += 4)
        {
            var b = rec.Slice(o, 4);
            string asc = new string(b.ToArray().Select(c => c is >= 32 and < 127 ? (char)c : '.').ToArray());
            Console.WriteLine($"{o,5} {(o == at ? ">" : " ")} {Convert.ToHexString(b)}  {BitConverter.ToUInt32(b),10}  {asc}");
        }
        return 0;
    }

    static void Dump(long addr, int before, int after)
    {
        var b = new byte[before + after];
        if (!Read(addr - before, b, b.Length)) { Console.WriteLine("unreadable"); return; }
        for (int o = 0; o + 8 <= b.Length; o += 8)
        {
            long v = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(o));
            string note = v >= ModBase && v < ModEnd ? "  -> " + Where(v) : "";
            uint lo = (uint)v, hi = (uint)((ulong)v >> 32);
            double d = BitConverter.Int64BitsToDouble(v);
            float flo = BitConverter.Int32BitsToSingle((int)lo), fhi = BitConverter.Int32BitsToSingle((int)hi);
            string F(float f) => Math.Abs(f) is > 1e-3f and < 1e7f ? f.ToString("G6") : "-";
            string dd = Math.Abs(d) is > 1e-3 and < 1e12 ? d.ToString("G8") : "-";
            if (note == "") note = $"  u32 {lo,10} {hi,10}  f32 {F(flo),9} {F(fhi),9}  f64 {dd}";
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
