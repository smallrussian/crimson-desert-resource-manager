using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CampLedger.Reader;

/// <summary>Finds the game's install folder: from the running game if any, otherwise from Steam's library list.</summary>
public static class GameInstall
{
    public static string? Find(Process? game)
    {
        try
        {
            string? exe = game?.MainModule?.FileName;
            if (exe is not null) return Path.GetDirectoryName(Path.GetDirectoryName(exe));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }

        string? steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam is null) return null;
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var libraries = new List<string> { steam };
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        foreach (string lib in libraries)
        {
            string dir = Path.Combine(lib, "steamapps", "common", "Crimson Desert");
            if (File.Exists(Path.Combine(dir, "bin64", "CrimsonDesert.exe"))) return dir;
        }
        return null;
    }
}

/// <summary>
/// Read-only access to the game's packed data (group 0008 and DLC copies). Index layout as observed on
/// 2.04.08; see docs/re-notes/mission-static-data.md. Nothing extracted here is written to disk.
/// </summary>
public sealed class Archive
{
    sealed record Entry(string Name, string Paz, uint Offset, uint Packed, uint Size, uint Flags);

    readonly List<Entry> _entries = new();

    public Archive(string installDir)
    {
        var roots = new List<string> { installDir };
        string dlcRoot = Path.Combine(installDir, "dlc");
        if (Directory.Exists(dlcRoot)) roots.AddRange(Directory.GetDirectories(dlcRoot));
        foreach (string root in roots)
        {
            string pamt = Path.Combine(root, "0008", "0.pamt");
            if (File.Exists(pamt)) Parse(pamt);
        }
        if (_entries.Count == 0) throw new InvalidDataException($"no readable data index under {installDir}");
        // Language packs (string tables) sit in their own small groups, e.g. 0020 for English on 2.04.08.
        // Their indexes are a few KB; the big asset groups are skipped. A group that does not parse is ignored.
        foreach (string root in roots)
            foreach (string g in Directory.GetDirectories(root))
            {
                string pamt = Path.Combine(g, "0.pamt");
                if (Path.GetFileName(g) == "0008" || !File.Exists(pamt) || new FileInfo(pamt).Length > 16 * 1024) continue;
                try { Parse(pamt); }
                catch (Exception e) when (e is not OutOfMemoryException) { }
            }
    }

    void Parse(string pamt)
    {
        byte[] d = File.ReadAllBytes(pamt);
        int pazCount = (int)U32(d, 4);
        int off = 12 + pazCount * 12;
        int folderSize = (int)U32(d, off); off += 4;
        var folders = Names(d, off, folderSize); off += folderSize;
        int fileSize = (int)U32(d, off); off += 4;
        var files = Names(d, off, fileSize); off += fileSize;
        int folderCount = (int)U32(d, off); off += 4;
        var owner = new Dictionary<int, int>();
        for (int i = 0; i < folderCount; i++, off += 16)
        {
            int name = (int)U32(d, off + 4), first = (int)U32(d, off + 8), count = (int)U32(d, off + 12);
            for (int f = first; f < first + count; f++) owner[f] = name;
        }
        int fileCount = (int)U32(d, off); off += 4;
        string dir = Path.GetDirectoryName(pamt)!;
        for (int i = 0; i < fileCount; i++, off += 20)
        {
            uint flags = U32(d, off + 16);
            string name = files(U32(d, off));
            if (owner.TryGetValue(i, out int folder)) name = folders((uint)folder) + "/" + name;
            _entries.Add(new Entry(name, Path.Combine(dir, $"{flags & 0xFF}.paz"), U32(d, off + 4), U32(d, off + 8), U32(d, off + 12), flags));
        }
    }

    /// <summary>Name trie: LEB128 distance back to the parent entry (0 = root), u8 length, bytes.</summary>
    static Func<uint, string> Names(byte[] d, int start, int size)
    {
        var nodes = new Dictionary<int, (int Parent, string Name)>();
        int p = start;
        while (p < start + size)
        {
            int rel = p - start, dist = 0, shift = 0;
            byte b;
            do { b = d[p++]; dist |= (b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
            int n = d[p++];
            nodes[rel] = (dist == 0 ? -1 : rel - dist, Encoding.UTF8.GetString(d, p, n));
            p += n;
        }
        return reference =>
        {
            var parts = new List<string>();
            int r = (int)reference;
            while (r >= 0 && parts.Count < 128 && nodes.TryGetValue(r, out var node)) { parts.Add(node.Name); r = node.Parent; }
            parts.Reverse();
            return string.Concat(parts);
        };
    }

    /// <summary>All copies of a static table (base first, then DLC), as (body, key → record span).</summary>
    public List<Table> Tables(string table)
    {
        var result = new List<Table>();
        foreach (var group in _entries.Where(e => e.Name == $"gamedata/binarystaticinfo__/bin/{table}"
                                                  || e.Name.StartsWith($"gamedata/binarystaticinfo__/bin/{table}.dlc"))
                                      .GroupBy(e => (e.Name, Path.GetDirectoryName(e.Paz))))
        {
            var parts = group.Select(Read).ToList();
            // Header: count, then count x (key, u32 offset). The body is the other part.
            // The count is u16 on most tables and u32 on some (questinfo, stageinfo); keys are u32 on most
            // tables and u16 on a few (regioninfo), so header entries are 8 or 6 bytes.
            byte[]? head = parts.FirstOrDefault(p => HeadFormat(p).KeySize != 0);
            byte[]? body = parts.FirstOrDefault(p => !ReferenceEquals(p, head));
            if (head is null || body is null) throw new InvalidDataException($"{table}: header and body not found");
            var (countSize, keySize) = HeadFormat(head);
            result.Add(new Table(body, head, keySize, countSize));
        }
        if (result.Count == 0) throw new InvalidDataException($"table {table} not found");
        return result;
    }

    public static (int CountSize, int KeySize) HeadFormat(byte[] p)
    {
        if (p.Length < 2) return (0, 0);
        long n16 = BinaryPrimitives.ReadUInt16LittleEndian(p);
        if (p.Length == 2 + 8 * n16) return (2, 4);
        if (p.Length == 2 + 6 * n16) return (2, 2);
        if (p.Length < 4) return (0, 0);
        long n32 = BinaryPrimitives.ReadUInt32LittleEndian(p);
        if (p.Length == 4 + 8 * n32) return (4, 4);
        if (p.Length == 4 + 6 * n32) return (4, 2);
        return (0, 0);
    }

    /// <summary>A string table in one language, base first then DLC copies. Throws if none is found.</summary>
    public List<StringTable> StringTables(string table, string language = "eng")
    {
        string path = $"gamedata/stringtable/binary__/{language}/{table}";
        var result = _entries.Where(e => e.Name == path || e.Name.StartsWith(path + ".dlc"))
            .OrderBy(e => e.Name.Length)
            .Select(e => StringTable.Parse(EntryCrypto.Decrypt(Read(e), e.Name[(e.Name.LastIndexOf('/') + 1)..] + ".paloc")))
            .ToList();
        if (result.Count == 0) throw new InvalidDataException($"string table {language}/{table} not found");
        return result;
    }

    byte[] Read(Entry e)
    {
        var packed = new byte[e.Packed];
        using (var fs = new FileStream(e.Paz, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(e.Offset, SeekOrigin.Begin);
            fs.ReadExactly(packed);
        }
        if (e.Packed == e.Size) return packed;
        if (((e.Flags >> 16) & 0xF) != 2) throw new InvalidDataException($"{e.Name}: unsupported compression");
        return Lz4.DecompressBlock(packed, (int)e.Size);
    }

    static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
}

public sealed class Table
{
    public byte[] Body { get; }
    public Dictionary<uint, (int Start, int End)> Records { get; } = new();

    public Table(byte[] body, byte[] head, int keySize = 4, int countSize = 2)
    {
        Body = body;
        int n = countSize == 4 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(head) : BinaryPrimitives.ReadUInt16LittleEndian(head);
        int step = keySize + 4, c = countSize;
        var entries = new (uint Key, int Offset)[n];
        for (int i = 0; i < n; i++)
            entries[i] = (keySize == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(c + step * i))
                                       : BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(c + step * i)),
                          (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(c + keySize + step * i)));
        var starts = entries.Select(e => e.Offset).Distinct().Order().ToList();
        starts.Add(body.Length);
        var next = new Dictionary<int, int>();
        for (int i = 0; i < starts.Count - 1; i++) next[starts[i]] = starts[i + 1];
        foreach (var (key, offset) in entries)
            if (next.TryGetValue(offset, out int end)) Records[key] = (offset, end);
    }

    /// <summary>Record name: most records start with a length-prefixed ASCII name.</summary>
    public string? Name(uint key)
    {
        if (!Records.TryGetValue(key, out var r) || r.End - r.Start < 4) return null;
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(Body.AsSpan(r.Start));
        if (n <= 0 || n > 200 || r.Start + 4 + n > r.End) return null;
        return Encoding.ASCII.GetString(Body, r.Start + 4, n);
    }
}
