using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CampLedger.Reader;

/// <summary>
/// Tier 2: reads the five camp reserves from the running game's memory. External and read-only: the process
/// is opened with query and VM-read rights only; nothing is written or injected.
/// Layout and evidence: docs/re-notes/live-memory.md and offsets/&lt;version&gt;/memory.json.
///
/// A reserve is an item object (class pointer at +0) whose parent (+0x28) is an owner object of a known class whose
/// own parent is the camp root of a known class. Pending costs (negative) and placeholders (0) share that chain and
/// are ignored; display copies have no parent. Fails closed: unknown version, a failed open, or disagreeing
/// candidates all give "unknown".
/// </summary>
public sealed class MemorySource : IDisposable
{
    const uint PROCESS_VM_READ = 0x0010, PROCESS_QUERY_INFORMATION = 0x0400;
    const uint MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000, PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;
    const uint WRITABLE = 0x04 | 0x08 | 0x40 | 0x80;
    const int Piece = 4 << 20, Page = 4096;

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
    readonly Dictionary<long, string> _keys = new();
    readonly Dictionary<string, List<long>> _objects = new();   // reserve name -> object start addresses
    long _class, _ownerClass, _rootClass, _header;
    int _parentOff, _headerOff, _amountOff, _keyOff, _listOff;
    public string? Problem { get; }
    public bool Located => _objects.Count == _keys.Count && _keys.Count > 0;
    /// <summary>How many reserves currently have at least one live object.</summary>
    public int LocatedCount => _objects.Count;

    public MemorySource(Process game, string? version)
        : this(game, version is null ? null : Offsets.FileFor(version, "memory.json"), version ?? "unknown") { }

    /// <summary>Uses a given layout file regardless of the game version (for checking a new layout: cdprobe verify).</summary>
    public static MemorySource FromFile(Process game, string layoutFile) => new(game, layoutFile, layoutFile);

    MemorySource(Process game, string? file, string label)
    {
        if (file is null || !File.Exists(file))
        {
            Problem = $"no memory layout for game version {label}";
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var o = doc.RootElement.GetProperty("reserveObject");
            long H(string name) => Convert.ToInt64(o.GetProperty(name).GetString()!, 16);
            long moduleBase = game.MainModule!.BaseAddress;
            _class = moduleBase + H("classRva");
            _ownerClass = moduleBase + H("ownerClassRva");
            _rootClass = moduleBase + H("rootClassRva");
            _parentOff = (int)H("parentOffset");
            _headerOff = (int)H("headerOffset");
            _amountOff = (int)H("amountOffset");
            _keyOff = (int)H("keyOffset");
            _listOff = (int)H("balanceListOffset");
            _header = BinaryPrimitives.ReadInt64LittleEndian(Convert.FromHexString(o.GetProperty("headerBytes").GetString()!));
            foreach (var k in o.GetProperty("itemKeys").EnumerateObject()) _keys[k.Value.GetInt64()] = k.Name;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or IOException)
        {
            Problem = "could not load memory layout: " + e.Message;
            return;
        }
        _handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, game.Id);
        if (_handle == 0) Problem = $"read-only open refused (error {Marshal.GetLastWin32Error()})";
    }

    /// <summary>Scans the game's private writable memory for reserve objects. Returns how many reserves were located.</summary>
    public int Locate()
    {
        _objects.Clear();
        if (Problem is not null) return 0;
        var pieces = new List<(long, int)>();
        nint address = 0;
        while (VirtualQueryEx(_handle, address, out var m, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
        {
            long start = m.BaseAddress, size = (long)m.RegionSize;
            if (m.State == MEM_COMMIT && m.Type == MEM_PRIVATE && (m.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0
                && (m.Protect & WRITABLE) != 0)
                for (long off = 0; off < size; off += Piece) pieces.Add((start + off, (int)Math.Min(Piece, size - off)));
            long next = start + size;
            if (next <= address || next >= 0x7FFFFFFFFFFF) break;
            address = (nint)next;
        }

        var candidates = new ConcurrentBag<long>();
        Parallel.ForEach(pieces, new ParallelOptions { MaxDegreeOfParallelism = 4 }, () => new byte[Piece], (p, _, buf) =>
        {
            var (at, len) = p;
            if (Read(at, buf, len)) Search(buf, len, at, candidates);
            else
                for (long q = at; q < at + len; q += Page)          // a page failed: go page by page
                    if (Read(q, buf, Page)) Search(buf, Page, q, candidates);
            return buf;
        }, _ => { });

        foreach (long obj in candidates.Distinct())
            if (Check(obj, out string? name, out _, out _) && name is not null)
            {
                if (!_objects.TryGetValue(name, out var list)) _objects[name] = list = new List<long>();
                list.Add(obj);
            }
        return _objects.Count;
    }

    void Search(byte[] buf, int length, long baseAddress, ConcurrentBag<long> found)
    {
        var q = MemoryMarshal.Cast<byte, long>(buf.AsSpan(0, length & ~7));
        for (int i = 0; i < q.Length; i++)
            if (q[i] == _class) found.Add(baseAddress + i * 8L);
    }

    /// <summary>
    /// Validates one object fully (class, header, key, owner chain), reads its amount, and says whether it is the
    /// balance (non-empty list at balanceListOffset) rather than a pending change or placeholder.
    /// </summary>
    bool Check(long obj, out string? name, out long amount, out bool balance)
    {
        name = null; amount = 0; balance = false;
        var b = new byte[_keyOff + 4];
        if (!Read(obj, b, b.Length)) return false;
        var s = b.AsSpan();
        if (BinaryPrimitives.ReadInt64LittleEndian(s) != _class
            || BinaryPrimitives.ReadInt64LittleEndian(s.Slice(_headerOff)) != _header
            || !_keys.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(_keyOff)), out name)) return false;
        long owner = BinaryPrimitives.ReadInt64LittleEndian(s.Slice(_parentOff));
        if (owner == 0 || Q(owner) != _ownerClass) return false;
        long root = Q(owner + _parentOff);
        if (root == 0 || Q(root) != _rootClass) return false;
        amount = BinaryPrimitives.ReadInt64LittleEndian(s.Slice(_amountOff));
        balance = BinaryPrimitives.ReadInt64LittleEndian(s.Slice(_listOff)) != 0
                  && BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(_listOff + 8)) != 0
                  && BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(_listOff + 12)) != 0;
        return true;
    }

    /// <summary>
    /// Re-reads the located objects. Objects that no longer validate are dropped; a reserve with none left becomes
    /// unlocated. Returns null when nothing is located (the caller should Locate again).
    /// </summary>
    public Reserves? ReadReserves()
    {
        if (_objects.Count == 0) return null;
        var r = new Reserves();
        foreach (var name in _objects.Keys.ToList())
        {
            var list = _objects[name];
            var amounts = new List<(long, bool)>();
            list.RemoveAll(obj =>
            {
                if (!Check(obj, out var n, out long amount, out bool balance) || n != name) return true;
                amounts.Add((amount, balance));
                return false;
            });
            if (list.Count == 0)
            {
                _objects.Remove(name);
                continue;
            }
            long? value = Resolve(amounts);
            switch (name)
            {
                case "funds": r.Funds = value; break;
                case "food": r.Food = value; break;
                case "timber": r.Timber = value; break;
                case "stone": r.Stone = value; break;
                case "armaments": r.Armaments = value; break;
            }
        }
        return _objects.Count == 0 ? null : r;
    }

    /// <summary>
    /// One reserve's value from its owned objects. Only balance objects count; pending changes (either sign) and
    /// placeholders are ignored. Every balance must hold the same value, and a negative or implausible one makes the
    /// reserve unknown. No balance, or disagreeing ones: unknown, never a guess. A balance of 0 is a real 0.
    /// </summary>
    public static long? Resolve(IReadOnlyCollection<(long Amount, bool Balance)> objects)
    {
        long? value = null;
        foreach (var (a, balance) in objects)
        {
            if (!balance) continue;
            if (a is < 0 or > 1_000_000_000_000) return null;
            if (value is not null && value != a) return null;
            value = a;
        }
        return value;
    }

    long Q(long address)
    {
        var b = new byte[8];
        return Read(address, b, 8) ? BinaryPrimitives.ReadInt64LittleEndian(b) : 0;
    }

    bool Read(long address, byte[] buffer, int count) =>
        ReadProcessMemory(_handle, (nint)address, buffer, (nuint)count, out var got) && (int)got == count;

    public void Dispose()
    {
        if (_handle != 0) CloseHandle(_handle);
    }
}
