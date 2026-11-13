using System.Buffers.Binary;
using System.Text;

namespace CampLedger.Reader;

/// <summary>
/// English display names for camp missions, from the game's own string tables (read-only, nothing written to
/// disk). Mission type: faction string table, keyed by the decimal string in each operation record. Location:
/// the regioninfo record named "Region_" + the node's internal name (or the node name itself) holds the region
/// string key. Layouts and checks: docs/re-notes/localisation.md (game 2.04.08).
/// </summary>
public sealed class DisplayNames
{
    readonly List<StringTable> _faction, _region, _character;
    readonly Dictionary<string, string> _regionKeyByName = new();
    readonly Dictionary<string, uint> _regionByName = new();
    readonly Dictionary<uint, string> _regionName = new();
    readonly Dictionary<uint, uint> _regionParent = new();

    public DisplayNames(List<StringTable> faction, List<StringTable> region, List<Table> regionInfo,
                        List<StringTable>? character = null, int? regionParentAfterName = null)
    {
        _character = character ?? new();
        _faction = faction;
        _region = region;
        foreach (var t in regionInfo)                              // base first, DLC later and wins
            foreach (var (key, (start, end)) in t.Records)
            {
                if (t.Name(key) is not string name) continue;
                var rec = t.Body.AsSpan(start, end - start);
                if (RegionStringKey(rec, key) is string sk) _regionKeyByName[name] = sk;
                _regionByName[name] = key;
                _regionName[key] = name;
                if (regionParentAfterName is int after && rec.Length >= 4)
                {
                    long at = 4L + BinaryPrimitives.ReadUInt32LittleEndian(rec) + after;
                    if (at + 2 <= rec.Length) _regionParent[key] = BinaryPrimitives.ReadUInt16LittleEndian(rec[(int)at..]);
                }
            }
    }

    /// <summary>
    /// The territory record (Region_&lt;People&gt;_Territory) a faction node belongs to, by following regioninfo parent
    /// links from "Region_" + node name (or the node name). Null if the node or a link is missing, or no layout.
    /// Evidence: docs/re-notes/mission-regions.md.
    /// </summary>
    public string? Territory(string nodeName)
    {
        if (!_regionByName.TryGetValue("Region_" + nodeName, out uint k) && !_regionByName.TryGetValue(nodeName, out k)) return null;
        for (int depth = 0; depth < 12; depth++)
        {
            if (_regionName.TryGetValue(k, out var n) && n.EndsWith("_Territory", StringComparison.Ordinal)) return n;
            if (!_regionParent.TryGetValue(k, out uint parent) || parent == k || !_regionName.ContainsKey(parent)) return null;
            k = parent;
        }
        return null;
    }

    public static DisplayNames? TryLoad(Archive archive, out string? problem, int? regionParentAfterName = null)
    {
        try
        {
            problem = null;
            return new DisplayNames(archive.StringTables("faction"), archive.StringTables("region"),
                                    archive.Tables("regioninfo"), archive.StringTables("character"), regionParentAfterName);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
        {
            problem = e.Message;
            return null;
        }
    }

    public string? Faction(string key) => Last(_faction, key);

    /// <summary>Character display name: key (character key &lt;&lt; 32) | 0x30 in the character table.</summary>
    public string? Character(uint key) => Last(_character, (((ulong)key << 32) | 0x30).ToString());

    /// <summary>Location name for a faction node's internal name, e.g. Node_Her_SouthQuarry → Southern Quarry.</summary>
    public string? Location(string nodeName) =>
        (_regionKeyByName.GetValueOrDefault("Region_" + nodeName) ?? _regionKeyByName.GetValueOrDefault(nodeName))
            is string k ? Last(_region, k) : null;

    /// <summary>
    /// The record's string key: u32 length + decimal digits whose value is (region key &lt;&lt; 32) | 0x110.
    /// The key check stops a stray digit run from being taken as the name.
    /// </summary>
    public static string? RegionStringKey(ReadOnlySpan<byte> rec, uint regionKey)
    {
        for (int i = 0; i + 4 < rec.Length; i++)
        {
            int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(i));
            if (n is < 6 or > 20 || i + 4 + n > rec.Length) continue;
            var s = rec.Slice(i + 4, n);
            bool digits = true;
            foreach (byte b in s) if (b is < (byte)'0' or > (byte)'9') { digits = false; break; }
            if (!digits || !ulong.TryParse(Encoding.ASCII.GetString(s), out ulong v)) continue;
            if (v >> 32 == regionKey && (v & 0xFFFFFFFF) == 0x110) return Encoding.ASCII.GetString(s);
        }
        return null;
    }

    static string? Last(List<StringTable> tables, string key)
    {
        for (int t = tables.Count - 1; t >= 0; t--)
            if (tables[t][key] is string v) return v;
        return null;
    }
}
