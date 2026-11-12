using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CampLedger.Reader;

/// <summary>
/// Tier 2: reads the five camp reserves from the running game's memory. External and read-only: the process
/// is opened with query and VM-read rights only; nothing is written or injected.
/// Layout and evidence: docs/re-notes/live-memory.md and offsets/&lt;version&gt;/memory.json.
/// Fails closed: unknown game version, a failed open, or an ambiguous match all give "unavailable".
/// </summary>
public sealed class MemorySource : IDisposable
{
    const uint PROCESS_VM_READ = 0x0010, PROCESS_QUERY_INFORMATION = 0x0400;
    const uint MEM_COMMIT = 0x1000, PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;
    const uint WRITABLE = 0x04 | 0x08 | 0x40 | 0x80;
    const int Chunk = 1 << 20, Page = 4096, Lead = 32;
    static readonly byte[] Header = { 1, 0, 0, 0, 1, 0, 0, 0 };

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect; public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")]
    static extern nuint VirtualQueryEx(nint process, nint address, out MemoryBasicInformation info, nuint length);
    [DllImport("kernel32.dll")]
    static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint read);

    readonly nint _handle;
    readonly byte[] _pointer = new byte[8];
    readonly Dictionary<long, string> _keys = new();
    readonly Dictionary<string, List<long>> _addresses = new();   // reserve name -> addresses of the amount
    public string? Problem { get; }
    public bool Located => _addresses.Count == _keys.Count && _keys.Count > 0;

    public MemorySource(Process game, string? version)
    {
        string file = Path.Combine(AppContext.BaseDirectory, "offsets", version ?? "unknown", "memory.json");
        if (version is null || !File.Exists(file))
        {
            Problem = $"no memory layout for game version {version ?? "unknown"}";
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var obj = doc.RootElement.GetProperty("reserveObject");
            long rva = Convert.ToInt64(obj.GetProperty("pointerRva").GetString()!, 16);
            foreach (var k in obj.GetProperty("itemKeys").EnumerateObject()) _keys[k.Value.GetInt64()] = k.Name;
            long moduleBase = game.MainModule!.BaseAddress;
            BinaryPrimitives.WriteInt64LittleEndian(_pointer, moduleBase + rva);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException
                                      or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Problem = "could not load memory layout: " + e.Message;
            return;
        }
        _handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, game.Id);
        if (_handle == 0) Problem = $"read-only open refused (error {Marshal.GetLastWin32Error()})";
    }

    /// <summary>Full scan for the reserve objects. Returns how many of the reserves were located.</summary>
    public int Locate()
    {
        _addresses.Clear();
        if (Problem is not null) return 0;
        var found = new Dictionary<string, List<long>>();
        var buf = new byte[Chunk + Lead];
        nint address = 0;
        while (VirtualQueryEx(_handle, address, out var mbi, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
        {
            long start = mbi.BaseAddress, size = (long)mbi.RegionSize;
            if (mbi.State == MEM_COMMIT && (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0 && (mbi.Protect & WRITABLE) != 0)
            {
                for (long off = 0; off < size; off += Chunk)
                {
                    // Start Lead bytes early so an object straddling two chunks is still seen whole.
                    long from = Math.Max(start, start + off - Lead);
                    int want = (int)Math.Min(start + size - from, Chunk + Lead);
                    if (Read(from, buf, want)) Search(buf, want, from, found);
                    else
                        for (long p = from & ~(long)(Page - 1); p < from + want; p += Page)   // a page failed: go page by page
                            if (Read(p, buf, Page)) Search(buf, Page, p, found);
                }
            }
            long next = start + size;
            if (next <= address || next >= 0x7FFFFFFFFFFF) break;
            address = (nint)next;
        }
        foreach (var (name, list) in found) _addresses[name] = list.Distinct().ToList();
        return _addresses.Count;
    }

    void Search(byte[] buf, int length, long baseAddress, Dictionary<string, List<long>> found)
    {
        var span = buf.AsSpan(0, length);
        int i = 0;
        while (true)
        {
            int hit = span.Slice(i).IndexOf(_pointer);
            if (hit < 0) break;
            int at = i + hit;
            i = at + 1;
            if (at < 24) continue;
            if (!span.Slice(at - 24, 8).SequenceEqual(Header)) continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(at - 8)) != 0) continue;
            long key = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(at - 4));
            if (!_keys.TryGetValue(key, out var name)) continue;
            if (!found.TryGetValue(name, out var list)) found[name] = list = new List<long>();
            list.Add(baseAddress + at - 16);
        }
    }

    /// <summary>
    /// Re-reads the located objects. Null if anything no longer validates (the caller should Locate again).
    /// A reserve is reported only when exactly one object holds a non-zero amount; otherwise it is unknown.
    /// </summary>
    public Reserves? ReadReserves()
    {
        if (!Located) return null;
        var r = new Reserves();
        var buf = new byte[32];
        foreach (var (name, list) in _addresses)
        {
            long? value = null; int nonZero = 0;
            foreach (long a in list)
            {
                if (!Read(a - 8, buf, 32)) return null;
                var s = buf.AsSpan();
                long key = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20));
                if (!s.Slice(0, 8).SequenceEqual(Header) || !s.Slice(24, 8).SequenceEqual(_pointer)
                    || !_keys.TryGetValue(key, out var n) || n != name) return null;
                ulong amount = BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(8));
                if (amount == 0) continue;
                if (amount > 1_000_000_000_000) return null;
                nonZero++; value = (long)amount;
            }
            if (nonZero != 1) value = null;   // none or several candidates: unknown, never a guess
            switch (name)
            {
                case "funds": r.Funds = value; break;
                case "food": r.Food = value; break;
                case "timber": r.Timber = value; break;
                case "stone": r.Stone = value; break;
                case "armaments": r.Armaments = value; break;
            }
        }
        return r;
    }

    bool Read(long address, byte[] buffer, int count) =>
        ReadProcessMemory(_handle, (nint)address, buffer, (nuint)count, out var got) && (int)got == count;

    public void Dispose()
    {
        if (_handle != 0) CloseHandle(_handle);
    }
}
