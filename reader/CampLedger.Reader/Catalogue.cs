using System.Buffers.Binary;
using System.Text;

namespace CampLedger.Reader;

/// <summary>A camp dispatch mission as defined in the game's static data (not per-save state).</summary>
public sealed record OperationInfo(
    uint Key, uint NodeKey, string? InternalName, string? Category, string? Role,
    int MembersMin, int MembersMax, IReadOnlyList<(uint Item, long Amount)> Cost, IReadOnlyList<uint> DropSets);

public sealed record DropItem(uint Item, long Min, long Max, double Chance);

/// <summary>
/// Mission, reward and character reference data read from the install (read-only). Layouts and how they were
/// checked: docs/re-notes/mission-static-data.md. Anything that does not validate is left out, never guessed.
/// </summary>
public sealed class Catalogue
{
    public static readonly IReadOnlyDictionary<uint, string> Reserve = new Dictionary<uint, string>
    { [11] = "funds", [12] = "food", [13] = "timber", [14] = "stone", [15] = "armaments" };

    /// <summary>factionoperationgroupinfo keys: which reserve a mission mainly produces.</summary>
    static readonly Dictionary<ushort, string> Roles = new()
    {
        [0x4240] = "Funds", [0x4241] = "Food", [0x4242] = "Timber", [0x4243] = "Stone", [0x4244] = "Armaments",
        [0x4245] = "Crafting", [0x4246] = "Explore", [0x4247] = "Building", [0x4248] = "Combat",
    };

    /// <summary>Region from the node's internal-name prefix. Only prefixes with evidence are listed.</summary>
    static readonly Dictionary<string, string> Regions = new()
    {
        ["Her"] = "Hernand", ["Dem"] = "Demeniss", ["Del"] = "Delesyia", ["Crim"] = "Crimson Desert", ["Kwe"] = "Pailune",
    };

    readonly Dictionary<uint, OperationInfo> _operations = new();
    readonly List<Table> _drops, _items, _characters;
    readonly HashSet<uint> _dropKeys;

    public Catalogue(Archive archive)
        : this(archive.Tables("factionnode"), archive.Tables("dropsetinfo"), archive.Tables("iteminfo"),
               archive.Tables("characterinfo")) { }

    /// <summary>From tables already in memory (base first, DLC after). Used by tests with hand-built tables.</summary>
    public Catalogue(List<Table> factionNode, List<Table> drops, List<Table> items, List<Table> characters)
    {
        _drops = drops;
        _dropKeys = new HashSet<uint>(_drops.SelectMany(t => t.Records.Keys));
        _items = items;
        _characters = characters;
        foreach (var table in factionNode) ScanOperations(table);   // DLC copies come later and win
    }

    public int OperationCount => _operations.Count;
    public OperationInfo? Operation(uint key) => _operations.GetValueOrDefault(key);

    public static string? RegionOf(string? internalName)
    {
        if (internalName is null || !internalName.StartsWith("Node_")) return null;
        string prefix = internalName.Split('_')[1];
        return Regions.GetValueOrDefault(prefix);
    }

    public string? ItemName(uint key) => Last(_items, key, (t, k) => t.Name(k));
    public string? CharacterName(uint key) => Last(_characters, key, (t, k) => t.Name(k));

    /// <summary>Drop-set entries, or null if the record is missing or fails its self-checks.</summary>
    public IReadOnlyList<DropItem>? DropSet(uint key)
    {
        for (int t = _drops.Count - 1; t >= 0; t--)
        {
            if (!_drops[t].Records.TryGetValue(key, out var r)) continue;
            var b = _drops[t].Body.AsSpan(r.Start, r.End - r.Start);
            int skip = 4 + (int)U32(b, 0);                      // length-prefixed comment, e.g. Item(Money_Camp_Money,1900)
            if (skip + 0x15 > b.Length || U32(b, skip) != key) return null;
            b = b.Slice(skip);
            int n = (int)U32(b, 0x11);
            if (n is < 0 or > 32 || 0x15 + n * 0x40 > b.Length) return null;
            var items = new List<DropItem>(n);
            for (int i = 0; i < n; i++)
            {
                var e = b.Slice(0x15 + i * 0x40, 0x40);
                uint item = U32(e, 1);
                if (e[0x3A] != 0xFF || e[0x3B] != 0xFF || U32(e, 0x3C) != item) return null;
                long min = (long)BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(0x2A));
                long max = (long)BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(0x32));
                items.Add(new DropItem(item, min, max, U32(e, 0x1A) / 1_000_000.0));
            }
            return items;
        }
        return null;
    }

    void ScanOperations(Table table)
    {
        foreach (var (node, (start, end)) in table.Records)
        {
            var rec = table.Body.AsSpan(start, end - start);
            for (int i = 13; i + 4 < rec.Length; i++)
            {
                // Each operation: u32 key, 9 bytes, u32 length + decimal string (localisation key), u16 group, ...
                int len = rec[i];
                if (len is < 16 or > 22 || rec[i + 1] != 0 || rec[i + 2] != 0 || rec[i + 3] != 0) continue;
                if (i + 4 + len + 6 > rec.Length || !AllDigits(rec.Slice(i + 4, len))) continue;
                int p = i + 4 + len;
                ushort group = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(p));
                if (!Roles.ContainsKey(group)) continue;
                var op = ParseOperation(rec, i - 13, p, node, group);
                if (op is null) continue;
                // Never let an incomplete record replace a complete one with the same key.
                if (op.Category is null && _operations.TryGetValue(op.Key, out var had) && had.Category is not null) continue;
                _operations[op.Key] = op;
            }
        }
    }

    OperationInfo? ParseOperation(ReadOnlySpan<byte> rec, int keyAt, int p, uint node, ushort group)
    {
        uint key = U32(rec, keyAt);
        int count = (int)U32(rec, p + 2);
        if (count is < 0 or > 6) return null;
        int c = p + 6;
        var cost = new List<(uint, long)>();
        for (int k = 0; k < count; k++, c += 30)                      // cost entry: u32 item, 8 bytes, u64 amount, 10 bytes
        {
            if (c + 30 > rec.Length) return null;
            cost.Add((U32(rec, c), (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.Slice(c + 12))));
        }
        // Members marker: u32 1, u32 min, u32 max, u32 0, then the length-prefixed category.
        for (int t = c; t + 24 < rec.Length && t < c + 200; t++)
        {
            int min = (int)U32(rec, t + 4), max = (int)U32(rec, t + 8), catLen = (int)U32(rec, t + 16);
            if (U32(rec, t) != 1 || min is < 1 or > 20 || max < min || max > 30 || U32(rec, t + 12) != 0
                || catLen is < 3 or > 24 || t + 20 + catLen > rec.Length || !AllLetters(rec.Slice(t + 20, catLen))) continue;
            string category = Encoding.ASCII.GetString(rec.Slice(t + 20, catLen));
            var drops = new List<uint>();
            for (int q = c; q + 4 <= t; q++)
            {
                uint v = U32(rec, q);
                if (v > 0 && _dropKeys.Contains(v)) drops.Add(v);
            }
            return new OperationInfo(key, node, NodeName(rec, t + 20 + catLen), category, Roles[group], min, max, cost, drops);
        }
        // No members marker: combat-style records. Keep the cost, leave the rest unknown.
        return new OperationInfo(key, node, null, null, Roles[group], 0, 0, cost, Array.Empty<uint>());
    }

    static string? NodeName(ReadOnlySpan<byte> rec, int from)
    {
        for (int q = from; q + 9 < rec.Length && q < from + 200; q++)
        {
            int n = (int)U32(rec, q);
            if (n is < 8 or > 120 || q + 4 + n > rec.Length) continue;
            var s = rec.Slice(q + 4, n);
            if (s.StartsWith("Node_"u8) && AllName(s)) return Encoding.ASCII.GetString(s);
        }
        return null;
    }

    static string? Last(List<Table> tables, uint key, Func<Table, uint, string?> get)
    {
        for (int t = tables.Count - 1; t >= 0; t--)
            if (tables[t].Records.ContainsKey(key)) return get(tables[t], key);
        return null;
    }

    static bool AllDigits(ReadOnlySpan<byte> s) { foreach (byte b in s) if (b is < (byte)'0' or > (byte)'9') return false; return true; }
    static bool AllLetters(ReadOnlySpan<byte> s) { foreach (byte b in s) if (!char.IsAsciiLetter((char)b)) return false; return true; }
    static bool AllName(ReadOnlySpan<byte> s) { foreach (byte b in s) if (!char.IsAsciiLetterOrDigit((char)b) && b != '_') return false; return true; }
    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(o));
}
