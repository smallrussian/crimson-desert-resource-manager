using System.Buffers.Binary;
using System.Text;

namespace CampLedger.Reader;

/// <summary>A camp dispatch mission as defined in the game's static data (not per-save state).</summary>
public sealed record OperationInfo(
    uint Key, uint NodeKey, string? InternalName, string? Category, string? Role,
    int MembersMin, int MembersMax, IReadOnlyList<(uint Item, long Amount)> Cost, IReadOnlyList<uint> DropSets,
    double? DurationHours = null, string? NameKey = null, uint? SkillStatus = null);

/// <summary>A comrade skill that raises mission rewards, e.g. Skill_Farming_II: Farming +30%.</summary>
public sealed record CampSkill(uint Key, string Type, int Tier, uint? Status, int Percent);

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
    readonly List<Table> _nodes, _drops, _items, _characters;
    readonly Dictionary<uint, CampSkill> _campSkills = new();
    readonly Dictionary<uint, string> _sublevelNames = new();
    readonly HashSet<uint> _dropKeys;
    readonly DisplayNames? _names;

    public Catalogue(Archive archive)
        : this(archive.Tables("factionnode"), archive.Tables("dropsetinfo"), archive.Tables("iteminfo"),
               archive.Tables("characterinfo"), DisplayNames.TryLoad(archive, out string? problem),
               archive.Tables("skill"), archive.Tables("sublevelinfo"))
    {
        NamesProblem = problem;
        Conditions = ConditionBook.TryLoad(archive, out string? conditionsProblem);
        ConditionsProblem = conditionsProblem;
    }

    /// <summary>Why English names could not be loaded, or null.</summary>
    public string? NamesProblem { get; }

    /// <summary>Mission start conditions (conditioninfo and the name tables), or null with <see cref="ConditionsProblem"/>.</summary>
    public ConditionBook? Conditions { get; init; }
    public string? ConditionsProblem { get; init; }

    /// <summary>From tables already in memory (base first, DLC after). Used by tests with hand-built tables.</summary>
    public Catalogue(List<Table> factionNode, List<Table> drops, List<Table> items, List<Table> characters,
                     DisplayNames? names = null, List<Table>? skills = null, List<Table>? sublevels = null)
    {
        foreach (var t in skills ?? new())
            foreach (var key in t.Records.Keys)
                if (ParseCampSkill(t, key) is CampSkill cs) _campSkills[key] = cs;
        foreach (var t in sublevels ?? new())
            foreach (var key in t.Records.Keys)
                if (t.Name(key) is string n) _sublevelNames[key] = n;
        _nodes = factionNode;
        _names = names;
        _drops = drops;
        _dropKeys = new HashSet<uint>(_drops.SelectMany(t => t.Records.Keys));
        _items = items;
        _characters = characters;
        foreach (var table in factionNode) ScanOperations(table);   // DLC copies come later and win
    }

    public int OperationCount => _operations.Count;
    public OperationInfo? Operation(uint key) => _operations.GetValueOrDefault(key);

    /// <summary>Dispatch missions defined at a node (records with a members marker; combat-style ones excluded).</summary>
    public IEnumerable<OperationInfo> OperationsAt(uint nodeKey) =>
        _operations.Values.Where(o => o.NodeKey == nodeKey && o.Category is not null).OrderBy(o => o.Key);

    /// <summary>Operation key by internal name (Node_..._Work_...), or null.</summary>
    public uint? OperationKeyOf(string internalName) =>
        _operations.Values.FirstOrDefault(o => o.InternalName == internalName && o.Category is not null)?.Key;

    /// <summary>
    /// The node's discovery knowledge (knowledgeinfo Knowledge_Node_...): u32 15 bytes after the node key, which
    /// follows the length-prefixed node name. Checked on Glenbright Farm and Southern Quarry, 2026-11-12.
    /// </summary>
    public uint? NodeKnowledgeKey(uint nodeKey)
    {
        for (int t = _nodes.Count - 1; t >= 0; t--)
        {
            if (!_nodes[t].Records.TryGetValue(nodeKey, out var r)) continue;
            return NodeKnowledgeKey(_nodes[t].Body.AsSpan(r.Start, r.End - r.Start), nodeKey);
        }
        return null;
    }

    public static uint? NodeKnowledgeKey(ReadOnlySpan<byte> rec, uint nodeKey)
    {
        if (rec.Length < 4) return null;
        int nameLen = (int)U32(rec, 0);
        int at = 4 + nameLen;
        if (nameLen is < 1 or > 200 || at + 19 > rec.Length || U32(rec, at) != nodeKey) return null;
        uint k = U32(rec, at + 15);
        return k == 0 ? null : k;
    }

    /// <summary>
    /// The operation's start conditions: between its key and its internal name, a u32 count n then n entries of
    /// 13 bytes, each starting with a conditioninfo key. The first run whose keys are all conditions is taken.
    /// Null when no such run is found.
    /// </summary>
    public IReadOnlyList<uint>? OperationConditionKeys(uint opKey, Func<uint, bool> isCondition)
    {
        if (Operation(opKey) is not { InternalName: string name } op) return null;
        for (int t = _nodes.Count - 1; t >= 0; t--)
        {
            if (!_nodes[t].Records.TryGetValue(op.NodeKey, out var r)) continue;
            return ConditionKeys(_nodes[t].Body.AsSpan(r.Start, r.End - r.Start), opKey, name, isCondition);
        }
        return null;
    }

    public static IReadOnlyList<uint>? ConditionKeys(ReadOnlySpan<byte> rec, uint opKey, string internalName, Func<uint, bool> isCondition)
    {
        Span<byte> kb = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(kb, opKey);
        int at = rec.IndexOf(kb);
        if (at < 0) return null;
        int end = rec.Slice(at).IndexOf(Encoding.ASCII.GetBytes(internalName));
        if (end < 0) return null;
        end += at;
        for (int i = at + 4; i + 4 < end; i++)
        {
            int n = (int)U32(rec, i);
            if (n is < 1 or > 12 || i + 4 + 13 * n > end) continue;
            var keys = new List<uint>(n);
            for (int j = 0; j < n; j++)
            {
                uint k = U32(rec, i + 4 + 13 * j);
                if (!isCondition(k)) { keys = null; break; }
                keys.Add(k);
            }
            if (keys is not null) return keys;
        }
        return null;
    }

    public static string? RegionOf(string? internalName)
    {
        if (internalName is null || !internalName.StartsWith("Node_")) return null;
        string prefix = internalName.Split('_')[1];
        return Regions.GetValueOrDefault(prefix);
    }

    public bool HasDisplayNames => _names is not null;

    /// <summary>English mission type as the game shows it, e.g. "Hasty Mining". Null if unknown.</summary>
    public string? OperationDisplayName(uint key) =>
        _names is not null && Operation(key)?.NameKey is string k ? _names.Faction(k) : null;

    /// <summary>English location of a mission node as the game shows it, e.g. "Southern Quarry". Null if unknown.</summary>
    public string? NodeLocation(uint nodeKey) =>
        _names is not null && Last(_nodes, nodeKey, (t, k) => t.Name(k)) is string node ? _names.Location(node) : null;

    public CampSkill? Skill(uint key) => _campSkills.GetValueOrDefault(key);

    /// <summary>Skill type a mission's rewards respond to, e.g. "Farming", from its skill status key.</summary>
    public string? SkillTypeOf(uint status) => _campSkills.Values.FirstOrDefault(s => s.Status == status)?.Type;

    public string? SublevelName(uint key) => _sublevelNames.GetValueOrDefault(key);

    /// <summary>English character name, e.g. "Evelyn". Null if unknown.</summary>
    public string? CharacterDisplayName(uint key) => _names?.Character(key);

    /// <summary>
    /// A comrade's skill slots in order (slot 1 first). Each entry is a camp skill key, or 0 for a slot that
    /// holds something else (the game shows cooking skills there). Null when the record does not fit the layout.
    /// </summary>
    public IReadOnlyList<uint>? SkillSlots(uint characterKey)
    {
        for (int t = _characters.Count - 1; t >= 0; t--)
        {
            if (!_characters[t].Records.TryGetValue(characterKey, out var r)) continue;
            return ParseSkillSlots(_characters[t].Body.AsSpan(r.Start, r.End - r.Start), k => _campSkills.ContainsKey(k));
        }
        return null;
    }

    static readonly byte[] SlotMarker = { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0xA0, 0x86, 0x01, 0x00 };

    /// <summary>
    /// Slot layout in characterinfo (2.04.08, docs/re-notes/comrade-bonuses.md): each slot block contains a run of
    /// entries starting with FF FF FF FF, 8 zero bytes, u32 100000. The slot's skill key is 696 bytes before the
    /// first such entry of the run. Runs are about 1,504 bytes apart.
    /// </summary>
    public static IReadOnlyList<uint>? ParseSkillSlots(ReadOnlySpan<byte> rec, Func<uint, bool> isCampSkill)
    {
        var firsts = new List<int>();
        int last = int.MinValue / 2;
        for (int i = rec.IndexOf(SlotMarker); i >= 0;)
        {
            if (i - last > 500) firsts.Add(i);
            last = i;
            int next = rec.Slice(i + 1).IndexOf(SlotMarker);
            i = next < 0 ? -1 : i + 1 + next;
        }
        if (firsts.Count is < 2 or > 8) return null;
        var slots = new List<uint>();
        for (int s = 0; s < firsts.Count; s++)
        {
            if (s > 0 && firsts[s] - firsts[s - 1] is < 1400 or > 1600) return null;
            int at = firsts[s] - 696;
            if (at < 0) return null;
            uint key = U32(rec, at);
            if (key != 0 && !isCampSkill(key)) return null;
            slots.Add(key);
        }
        return slots;
    }

    /// <summary>
    /// Skill_&lt;Type&gt;_I/II/III records. A reward skill holds exactly one (u32 status key, u32 amount) pair, status
    /// in 1,000,000-1,100,000 and amount a multiple of 1,000 (1,000 = 1%); usually 178 bytes after the name, but
    /// not always, so it is searched for. A skill with no such pair is a gate ("allows ... missions"), Percent 0.
    /// Two or more pairs: not understood, left out.
    /// </summary>
    static CampSkill? ParseCampSkill(Table t, uint key)
    {
        string? name = t.Name(key);
        if (name is null || !name.StartsWith("Skill_")) return null;
        var parts = name.Split('_');
        if (parts.Length != 3) return null;
        int tier = parts[2] switch { "I" => 1, "II" => 2, "III" => 3, _ => 0 };
        if (tier == 0 || !parts[1].All(char.IsAsciiLetter)) return null;
        var r = t.Records[key];
        var rec = t.Body.AsSpan(r.Start, r.End - r.Start);
        uint? status = null;
        int percent = 0, found = 0;
        for (int p = 4 + name.Length; p + 8 <= rec.Length; p++)
        {
            uint st = U32(rec, p), amount = U32(rec, p + 4);
            if (st is < 1_000_000 or > 1_100_000 || amount == 0 || amount > 1_000_000 || amount % 1000 != 0) continue;
            status = st;
            percent = (int)(amount / 1000);
            found++;
        }
        return found > 1 ? null : new CampSkill(key, parts[1], tier, status, percent);
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
            return new OperationInfo(key, node, NodeName(rec, t + 20 + catLen), category, Roles[group], min, max, cost, drops,
                                     Duration(rec, keyAt), NameKey(rec, keyAt),
                                     t + 24 + catLen <= rec.Length ? U32(rec, t + 20 + catLen) : null);
        }
        // No members marker: combat-style records. Keep the cost, leave the rest unknown.
        return new OperationInfo(key, node, null, null, Roles[group], 0, 0, cost, Array.Empty<uint>(),
                                 NameKey: NameKey(rec, keyAt));
    }

    /// <summary>The decimal string 13 bytes after the operation key: its key in the faction string table.</summary>
    static string NameKey(ReadOnlySpan<byte> rec, int keyAt) =>
        Encoding.ASCII.GetString(rec.Slice(keyAt + 17, (int)U32(rec, keyAt + 13)));

    /// <summary>
    /// Mission duration: u32 in-game hours x 10 (the save's progress unit), 90 bytes before the operation key, with
    /// a 0x02 byte 5 bytes before it. Matched the owner's screen for South Quarry (12 h), Burnhand Quarry (22 h) and
    /// Oakenshield Manor guard (18 h) on 2026-11-12. Null when the layout does not hold (e.g. a node's first record).
    /// </summary>
    internal static double? Duration(ReadOnlySpan<byte> rec, int keyAt)
    {
        int at = keyAt - 90;
        if (at < 5 || rec[at - 5] != 0x02) return null;
        uint tenths = U32(rec, at);
        return tenths is 0 or > 10_000 ? null : tenths / 10.0;
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
