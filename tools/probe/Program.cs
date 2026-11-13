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
//   cdprobe relocate <memory.json> [funds=N ...]  after a patch: find reserve objects by shape, print new class RVAs
//   cdprobe verify <memory.json>          locate and read reserves with the reader's own code and this layout file
//   cdprobe multidrop [n]                 camp drop sets with 2+ items (for re-deriving the dropsetinfo layout)
//   cdprobe recbin <table> <key> <out>    raw game-data record bytes to a file (kept outside the repo)
//   cdprobe campskills                    camp skill keys, types and percents the catalogue recognises
// Per-patch procedure: docs/patch-update.md. Game-data commands use offsets/<installed version>/gamedata.json,
// or the file in CAMPLEDGER_GAMEDATA.

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
            var cat = Cat(new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!));
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
        if (a[0] == "savenodes")
        {
            // Read-only: copies the newest save to a temp file and decodes the copy.
            var src = new CampLedger.Reader.SaveSource().NewestSave()!;
            string copy = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.save");
            File.Copy(src.FullName, copy);
            File.SetAttributes(copy, FileAttributes.Normal);
            byte[] raw = File.ReadAllBytes(copy);
            File.Delete(copy);
            var body = new CampLedger.Reader.SaveBody(CampLedger.Reader.SaveCrypto.Decode(raw));
            if (a.Length > 2 && a[1] == "fields")
            {
                // savenodes fields <regex> : every type/field name in the save schema matching the regex.
                var rx = new System.Text.RegularExpressions.Regex(a[2], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var t in body.Types)
                    foreach (var f in t.Fields.Where(f => rx.IsMatch(f.Name) || rx.IsMatch(t.Name)))
                        Console.WriteLine($"{t.Name}.{f.Name} : {f.TypeName}");
                return 0;
            }
            if (a.Length > 1 && a[1] == "blocks")
            {
                // savenodes blocks              -> list block types with sizes and field names
                // savenodes blocks <Type> [filter] -> dump a decoded block (lists truncated)
                static string Dump(object? v, int depth, int maxList) => v switch
                {
                    List<object?> l => depth > 4 ? $"list[{l.Count}]" : "[" + string.Join("; ", l.Take(maxList).Select(x => Dump(x, depth + 1, maxList))) + (l.Count > maxList ? $"; …{l.Count}" : "") + "]",
                    Dictionary<string, object?> d => "{" + string.Join(", ", d.Where(kv => kv.Key != "__type").Select(kv => $"{kv.Key}={Dump(kv.Value, depth + 1, maxList)}")) + "}",
                    _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
                };
                if (a.Length == 2)
                {
                    foreach (var b in body.Blocks.OrderBy(b => b.TypeName))
                        Console.WriteLine($"{b.TypeName,-45} {b.Size,9}  {string.Join(",", body.Types[b.TypeIndex].Fields.Select(f => f.Name)).PadRight(0)[..Math.Min(160, string.Join(",", body.Types[b.TypeIndex].Fields.Select(f => f.Name)).Length)]}");
                    return 0;
                }
                var blk = body.Decode(a[2]);
                if (blk is null) { Console.WriteLine("no such block"); return 1; }
                int max = a.Length > 4 ? int.Parse(a[4]) : 40;
                foreach (var (k, v) in blk)
                {
                    if (k == "__type") continue;
                    if (v is List<object?> l)
                    {
                        Console.WriteLine($"{k}: list[{l.Count}]");
                        foreach (var x in l.Where(x => a.Length < 4 || a[3] == "-" || Dump(x, 0, 99).Contains(a[3])).Take(max)) Console.WriteLine("  " + Dump(x, 0, 12));
                    }
                    else Console.WriteLine($"{k} = {Dump(v, 0, 12)}");
                }
                return 0;
            }
            var faction = body.Decode("FactionSaveData")!;
            var nodes = ((List<object?>)faction["_factionNodeElementSaveDataList"]!).OfType<Dictionary<string, object?>>().ToList();
            Console.WriteLine($"{src.FullName}: {nodes.Count} nodes; top-level fields: {string.Join(", ", faction.Keys)}");
            var counts = new Dictionary<string, int>();
            foreach (var n in nodes) foreach (var k in n.Keys) counts[k] = counts.GetValueOrDefault(k) + 1;
            foreach (var (k, c) in counts.OrderBy(x => x.Key))
            {
                var vals = nodes.Where(n => n.ContainsKey(k)).Select(n => n[k] is List<object?> l ? $"list[{l.Count}]" : Convert.ToString(n[k], System.Globalization.CultureInfo.InvariantCulture))
                    .GroupBy(v => v).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key} x{g.Count()}");
                Console.WriteLine($"  {k,-40} {c,5}  {string.Join(" | ", vals)}");
            }
            if (a.Length > 1 && a[1] == "factions")
            {
                static string Show(object? v, int depth) => v switch
                {
                    List<object?> l => depth > 2 ? $"list[{l.Count}]" : "[" + string.Join("; ", l.Take(12).Select(x => Show(x, depth + 1))) + (l.Count > 12 ? $"; …{l.Count}" : "") + "]",
                    Dictionary<string, object?> d => "{" + string.Join(", ", d.Where(kv => kv.Key != "__type").Select(kv => $"{kv.Key}={Show(kv.Value, depth + 1)}")) + "}",
                    _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
                };
                foreach (var f in ((List<object?>)faction["_factionElementSaveDataList"]!).OfType<Dictionary<string, object?>>())
                    if (a.Length < 3 || a.Skip(2).Any(w => Show(f, 0).Contains(w)))
                        Console.WriteLine(Show(f, 0));
                return 0;
            }
            if (a.Length > 1 && a[1] == "camp")
            {
                var cat = Cat(new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!));
                var ops = ((Dictionary<uint, CampLedger.Reader.OperationInfo>)typeof(CampLedger.Reader.Catalogue)
                    .GetField("_operations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(cat)!).Values.Where(o => o.Category is not null).ToList();
                var byNode = nodes.ToDictionary(n => Convert.ToUInt32(n["_ownerFactionKey"]));
                string State(uint node) => byNode.TryGetValue(node, out var n)
                    ? $"state={n.GetValueOrDefault("_factionState")} block={n.GetValueOrDefault("_isBlock") ?? "-"} by={n.GetValueOrDefault("_blockadingFactionKey") ?? "-"} conq={n.GetValueOrDefault("_conquerorFactionKey") ?? "-"} sub={n.GetValueOrDefault("_blockSubType") ?? "-"} skills={(n.GetValueOrDefault("_factionNodeApplySkillList") as List<object?>)?.Count.ToString() ?? "-"}"
                    : "not in save";
                if (a.Length > 2 && a[2] == "unlock")
                {
                    // Per mission node: knowledge key from the factionnode record (u32 15 bytes after the node key),
                    // whether the save has it learned, and the node's controlling factions vs the player's relations.
                    var know = ((List<object?>)body.Decode("KnowledgeSaveData")!["_list"]!).OfType<Dictionary<string, object?>>()
                        .ToDictionary(k => Convert.ToUInt32(k["_key"]), k => Convert.ToInt32(k.GetValueOrDefault("_level") ?? 0));
                    var player = ((List<object?>)faction["_factionElementSaveDataList"]!).OfType<Dictionary<string, object?>>()
                        .First(f => Convert.ToString(f["_ownerFactionKey"]) == "1000000");
                    var rel = ((List<object?>)player["_factionRelationDataList"]!).OfType<Dictionary<string, object?>>()
                        .ToDictionary(r => Convert.ToUInt32(r["_targetFactionKey"]), r => Convert.ToInt32(r.GetValueOrDefault("_relationTypeRaw") ?? 0));
                    var tables = new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!).Tables("factionnode");
                    uint? Knowledge(uint node)
                    {
                        var t = tables.LastOrDefault(t => t.Records.ContainsKey(node));
                        if (t is null) return null;
                        var (s, e) = t.Records[node];
                        var r = t.Body.AsSpan(s, e - s);
                        int nl = (int)BitConverter.ToUInt32(r);
                        if (4 + nl + 4 + 19 > r.Length || BitConverter.ToUInt32(r.Slice(4 + nl)) != node) return null;
                        return BitConverter.ToUInt32(r.Slice(4 + nl + 15));
                    }
                    string Rel(object? f) => f is null ? "-" : $"{f}:{rel.GetValueOrDefault(Convert.ToUInt32(f), -1)}";
                    if (a.Length > 3)
                    {
                        // camp unlock <namefilter>: u32 keys (1,000,000-1,100,000) inside each op's record segment and their save state.
                        var q = body.Decode("QuestSaveData")!;
                        var st = new Dictionary<uint, string>();
                        void Add(string list, string keyField, string tag)
                        {
                            foreach (var e in ((List<object?>)q[list]!).OfType<Dictionary<string, object?>>())
                                st[Convert.ToUInt32(e[keyField])] = (st.TryGetValue(Convert.ToUInt32(e[keyField]), out var p) ? p + "," : "") + $"{tag}{e.GetValueOrDefault("_state")}";
                        }
                        Add("_stageStateData", "_key", "stage"); Add("_missionStateList", "_key", "mission"); Add("_questStateList", "_questKey", "quest");
                        foreach (var (kk, lv) in know) st[kk] = (st.TryGetValue(kk, out var p2) ? p2 + "," : "") + $"know{lv}";
                        foreach (var o in ops.Where(o => (o.InternalName ?? "").Contains(a[3], StringComparison.OrdinalIgnoreCase)))
                        {
                            var t = tables.Last(t => t.Records.ContainsKey(o.NodeKey));
                            var (s, e) = t.Records[o.NodeKey];
                            var r = t.Body.AsSpan(s, e - s);
                            var kb = BitConverter.GetBytes(o.Key);
                            int at = r.IndexOf(kb);
                            int nameAt = r.Slice(at).IndexOf(System.Text.Encoding.ASCII.GetBytes(o.InternalName!)) + at;
                            var refs = new List<string>();
                            for (int i = at + 4; i + 4 <= nameAt; i++)
                            {
                                uint v = BitConverter.ToUInt32(r.Slice(i));
                                if (v is >= 1_000_000 and <= 1_100_000) refs.Add($"{v}@{i - at}[{st.GetValueOrDefault(v, "none")}]");
                            }
                            Console.WriteLine($"{o.InternalName} op {o.Key}: {string.Join(" ", refs)}");
                        }
                        return 0;
                    }
                    var rows = ops.Select(o =>
                    {
                        byNode.TryGetValue(o.NodeKey, out var n);
                        uint? k = Knowledge(o.NodeKey);
                        return (o, n, k, learned: k is uint kk && know.ContainsKey(kk) ? know[kk] : 0);
                    }).ToList();
                    foreach (var g in rows.GroupBy(r => $"state={r.n?.GetValueOrDefault("_factionState")} block={(r.n?.ContainsKey("_isBlock") == true ? "Y" : "-")} know={(r.k is null ? "?" : r.learned.ToString())} by={Rel(r.n?.GetValueOrDefault("_blockadingFactionKey"))} conq={Rel(r.n?.GetValueOrDefault("_conquerorFactionKey"))}")
                                             .OrderBy(g => g.Key))
                        Console.WriteLine($"{g.Count(),4} {g.Select(r => r.o.NodeKey).Distinct().Count(),3}n  {g.Key}  e.g. {string.Join("; ", g.Take(3).Select(r => $"{cat.NodeLocation(r.o.NodeKey) ?? r.o.InternalName}/{cat.OperationDisplayName(r.o.Key)}"))}");
                    return 0;
                }
                if (a.Length > 2 && a[2] == "held")
                {
                    bool Held(uint node) => byNode.TryGetValue(node, out var n) && Convert.ToString(n.GetValueOrDefault("_factionState")) == "2"
                        && !n.ContainsKey("_isBlock")
                        && (a.Length < 4 || a[3] != "strict" || Convert.ToString(n.GetValueOrDefault("_blockadingFactionKey")) == "1000000" || Convert.ToString(n.GetValueOrDefault("_conquerorFactionKey")) == "1000000");
                    var held = ops.Where(o => Held(o.NodeKey)).OrderBy(o => cat.NodeLocation(o.NodeKey) ?? o.InternalName).ToList();
                    Console.WriteLine($"{held.Count} missions at held nodes ({held.Select(o => o.NodeKey).Distinct().Count()} nodes)");
                    foreach (var o in held)
                        Console.WriteLine($"  {CampLedger.Reader.Catalogue.RegionOf(o.InternalName),-9} {cat.NodeLocation(o.NodeKey) ?? "-",-34} {cat.OperationDisplayName(o.Key),-42} {o.Category,-12} {o.MembersMin}-{o.MembersMax}  {o.DurationHours}h");
                    return 0;
                }
                if (a.Length > 2)
                {
                    foreach (var o in ops.Where(o => (o.InternalName ?? "").Contains(a[2], StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"{o.NodeKey} {o.InternalName} {cat.OperationDisplayName(o.Key)}: " + (byNode.TryGetValue(o.NodeKey, out var n)
                            ? string.Join(", ", n.Where(kv => kv.Key != "__type").Select(kv => $"{kv.Key}={(kv.Value is List<object?> l ? $"list[{l.Count}]" : kv.Value)}"))
                            : "not in save"));
                    return 0;
                }
                foreach (var g in ops.GroupBy(o => State(o.NodeKey)).OrderByDescending(g => g.Count()))
                    Console.WriteLine($"{g.Count(),4}  {g.Key}  e.g. {string.Join("; ", g.Take(4).Select(o => $"{cat.NodeLocation(o.NodeKey) ?? o.InternalName}/{cat.OperationDisplayName(o.Key)}"))}");
                return 0;
            }
            foreach (string want in a.Skip(1))
                foreach (var n in nodes.Where(n => Convert.ToString(n.GetValueOrDefault("_ownerFactionKey")) == want))
                    Console.WriteLine($"node {want}: " + string.Join(", ", n.Select(kv => $"{kv.Key}={(kv.Value is List<object?> l ? $"list[{l.Count}]" : kv.Value)}")));
            return 0;
        }
        if (a[0] == "conds")
        {
            // Survey: every condition expression referenced by dispatch ops (count + 13-byte entries), macros expanded.
            var ar = new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!);
            var cond = new Dictionary<uint, (string Name, string Expr)>();
            foreach (var t in ar.Tables("conditioninfo"))
                foreach (var (k, (s, en)) in t.Records)
                {
                    var r = t.Body.AsSpan(s, en - s);
                    var strs = new List<string>();
                    for (int i = 0; i + 4 < r.Length && strs.Count < 2; i++)
                    {
                        int L = (int)BitConverter.ToUInt32(r.Slice(i));
                        if (L is < 1 or > 400 || i + 4 + L > r.Length) continue;
                        var str = r.Slice(i + 4, L); bool ok = true;
                        foreach (var c in str) if (c is < 32 or > 126) { ok = false; break; }
                        if (ok) { strs.Add(System.Text.Encoding.ASCII.GetString(str)); i += 3 + L; }
                    }
                    if (strs.Count == 2) cond[k] = (strs[0], strs[1]); if (strs.Count == 2 && strs[1] == "Dock") Console.WriteLine($"Dock: {k} name={strs[0]} len={en - s}");
                }
            var byName = cond.Values.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.Last().Expr);
            Console.WriteLine($"{cond.Count} conditions");
            var cat = Cat(ar);
            var ops = ((Dictionary<uint, CampLedger.Reader.OperationInfo>)typeof(CampLedger.Reader.Catalogue)
                .GetField("_operations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(cat)!).Values.Where(o => o.Category is not null).ToList();
            var fn = ar.Tables("factionnode");
            var used = new Dictionary<string, int>(); int noList = 0;
            foreach (var o in ops)
            {
                var t = fn.Last(t => t.Records.ContainsKey(o.NodeKey));
                var (s, e) = t.Records[o.NodeKey];
                var r = t.Body.AsSpan(s, e - s);
                int at = r.IndexOf(BitConverter.GetBytes(o.Key));
                int end = r.Slice(at).IndexOf(System.Text.Encoding.ASCII.GetBytes(o.InternalName!)) + at;
                bool found = false;
                for (int i = at + 4; i + 4 < end && !found; i++)
                {
                    int n = (int)BitConverter.ToUInt32(r.Slice(i));
                    if (n is < 1 or > 12 || i + 4 + 13 * n > end) continue;
                    var keys = new List<uint>(); for (int j = 0; j < n; j++) keys.Add(BitConverter.ToUInt32(r.Slice(i + 4 + 13 * j)));
                    if (!keys.All(cond.ContainsKey)) continue;
                    found = true;
                    foreach (var k in keys) used[cond[k].Expr] = used.GetValueOrDefault(cond[k].Expr) + 1;
                    if (a.Length > 1 && o.InternalName!.Contains(a[1])) Console.WriteLine($"{o.InternalName} @{i - at}: " + string.Join(" | ", keys.Select(k => cond[k].Expr)));
                }
                if (!found) noList++;
            }
            Console.WriteLine($"ops without a list: {noList}");
            foreach (var (x, c) in used.OrderByDescending(u => u.Value)) Console.WriteLine($"{c,5}  {x}");
            Console.WriteLine("macros:");
            foreach (var m in used.Keys.SelectMany(x => System.Text.RegularExpressions.Regex.Matches(x, @"Macro\((\w+)\)").Select(mm => mm.Groups[1].Value)).Distinct())
                Console.WriteLine($"  {m} = {byName.GetValueOrDefault(m, "?")}");
            return 0;
        }
        if (a[0] == "findkey")
        {
            // findkey <namesFile> <key...> : which tables have a record with this key (first string shown).
            var ar = new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!);
            foreach (var name in File.ReadAllLines(a[1]).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct())
            {
                List<CampLedger.Reader.Table> ts;
                try { ts = ar.Tables(name); } catch { continue; }
                foreach (var ks in a.Skip(2))
                {
                    uint key = uint.Parse(ks);
                    var t = ts.LastOrDefault(t => t.Records.ContainsKey(key));
                    if (t is null) continue;
                    var (s, en) = t.Records[key];
                    int L = (int)BitConverter.ToUInt32(t.Body, s);
                    string first = L is > 0 and < 100 ? System.Text.Encoding.ASCII.GetString(t.Body, s + 4, L) : "?";
                    Console.WriteLine($"{key} {name}: {first} ({en - s} bytes)");
                }
            }
            return 0;
        }
        if (a[0] == "multidrop")
        {
            // multidrop [count] : drop-set records whose comment names two or more items, for checking the entry layout.
            var ts = new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!).Tables("dropsetinfo");
            int want = a.Length > 1 ? int.Parse(a[1]) : 5, shown = 0;
            foreach (var t in ts)
                foreach (var (key, (s, en)) in t.Records)
                {
                    int L = (int)BitConverter.ToUInt32(t.Body, s);
                    if (L is < 1 or > 400) continue;
                    string c = System.Text.Encoding.ASCII.GetString(t.Body, s + 4, L);
                    if (c.Split("Item(").Length < 3 || !c.Contains("Money_Camp")) continue;
                    Console.WriteLine($"{key} {c} ({en - s} bytes, comment {L})");
                    if (++shown >= want) return 0;
                }
            return 0;
        }
        if (a[0] == "campskills")
        {
            // campskills : key, type, tier, percent of every camp skill the catalogue recognises.
            var cat = Cat(new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!));
            var d = (System.Collections.IDictionary)typeof(CampLedger.Reader.Catalogue)
                .GetField("_campSkills", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cat)!;
            foreach (System.Collections.DictionaryEntry e in d) Console.WriteLine($"{e.Key} {e.Value}");
            return 0;
        }
        if (a[0] == "recbin")
        {
            // recbin <table> <key> <outFile> : raw record bytes (the copy DLC wins), for offline layout comparison.
            var ts = new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!).Tables(a[1]);
            uint key = uint.Parse(a[2]);
            var t = ts.LastOrDefault(t => t.Records.ContainsKey(key));
            if (t is null) { Console.WriteLine("not found"); return 1; }
            var (s, en) = t.Records[key];
            File.WriteAllBytes(a[3], t.Body.AsSpan(s, en - s).ToArray());
            Console.WriteLine($"{en - s} bytes from table {ts.IndexOf(t)} of {ts.Count}");
            return 0;
        }
        if (a[0] == "rec")
        {
            // rec <table> <key> [hexFrom hexTo] : printable length-prefixed strings in a record, optional hex window.
            var ts = new CampLedger.Reader.Archive(CampLedger.Reader.GameInstall.Find(null)!).Tables(a[1]);
            uint key = uint.Parse(a[2]);
            var t = ts.LastOrDefault(t => t.Records.ContainsKey(key));
            if (t is null) { Console.WriteLine($"not found ({ts.Count} tables, {ts.Sum(x => x.Records.Count)} records)"); return 1; }
            var (s, en) = t.Records[key];
            var r = t.Body.AsSpan(s, en - s);
            Console.WriteLine($"{r.Length} bytes");
            for (int i = 0; i + 4 < r.Length; i++)
            {
                int L = (int)BitConverter.ToUInt32(r.Slice(i));
                if (L is < 3 or > 100 || i + 4 + L > r.Length) continue;
                var str = r.Slice(i + 4, L);
                bool ok = true; foreach (var c in str) if (c is < 32 or > 126) { ok = false; break; }
                if (ok) Console.WriteLine($"  @{i} \"{System.Text.Encoding.ASCII.GetString(str)}\"");
            }
            if (a.Length > 4)
                for (int i = int.Parse(a[3]); i < Math.Min(r.Length, int.Parse(a[4])); i += 16)
                {
                    var row = r.Slice(i, Math.Min(16, r.Length - i)).ToArray();
                    Console.WriteLine($"  {i,5} {Convert.ToHexString(row)}  " + string.Join(" ", Enumerable.Range(0, row.Length / 4).Select(j => BitConverter.ToUInt32(row, j * 4))));
                }
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
            case "relocate": Relocate(a[1], a.Skip(2).ToArray()); break;
            case "verify": Verify(a[1]); break;
        }
        Console.WriteLine($"({sw.Elapsed.TotalSeconds:F1} s)");
        return 0;
    }

    /// <summary>Catalogue with the layout for the installed game version, or the file in CAMPLEDGER_GAMEDATA if set.</summary>
    static CampLedger.Reader.Catalogue Cat(CampLedger.Reader.Archive archive)
    {
        string? file = Environment.GetEnvironmentVariable("CAMPLEDGER_GAMEDATA");
        if (file is not null) return new(archive, CampLedger.Reader.GameDataLayout.Load(file));
        string? v = CampLedger.Reader.GameVersion.FromInstall(CampLedger.Reader.GameInstall.Find(null));
        return new(archive, CampLedger.Reader.GameDataLayout.TryLoad(v, out string? problem)
                            ?? throw new InvalidOperationException(problem + "; set CAMPLEDGER_GAMEDATA to a gamedata.json to try one"));
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

    /// <summary>
    /// After a patch: finds reserve item objects by their shape alone (header, amount, zero, key 11-15 at the
    /// offsets in an older memory.json), ignoring that file's RVAs. For every hit it follows the owner route
    /// (object +parentOffset -> owner, owner +parentOffset -> root) and groups hits by the three class pointers.
    /// The group that covers all five keys with an owner and root in the exe gives the new RVAs.
    /// Optional "funds=50456" style arguments mark hits whose amount equals a known value (e.g. from the save).
    /// docs/patch-update.md describes how to use the result.
    /// </summary>
    static void Relocate(string layoutFile, string[] expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(layoutFile));
        var o = doc.RootElement.GetProperty("reserveObject");
        int Off(string n) => (int)Hex(o.GetProperty(n).GetString()!);
        int parentOff = Off("parentOffset"), headerOff = Off("headerOffset"), amountOff = Off("amountOffset"),
            keyOff = Off("keyOffset"), listOff = Off("balanceListOffset");
        long header = BinaryPrimitives.ReadInt64LittleEndian(Convert.FromHexString(o.GetProperty("headerBytes").GetString()!));
        var names = o.GetProperty("itemKeys").EnumerateObject().ToDictionary(p => (uint)p.Value.GetInt32(), p => p.Name);
        var want = expected.Select(e => e.Split('=')).ToDictionary(p => p[0], p => long.Parse(p[1]));
        int zeroOff = amountOff + 8;
        if (zeroOff + 4 != keyOff) Console.WriteLine("note: key is not right after amount+zero in this layout; checking key separately");

        var hits = new ConcurrentBag<long>();   // object starts
        Scan((buf, len, at) =>
        {
            var s = buf.AsSpan(0, len);
            for (int i = 0; i + (keyOff - headerOff) + 4 <= len; i += 8)
            {
                if (BinaryPrimitives.ReadInt64LittleEndian(s.Slice(i)) != header) continue;
                if (BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(i + zeroOff - headerOff)) != 0) continue;
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(i + keyOff - headerOff));
                if (names.ContainsKey(key)) hits.Add(at + i - headerOff);
            }
        });
        Console.WriteLine($"{hits.Count} objects with the reserve shape");

        var groups = new Dictionary<(long, long, long), List<(long Obj, uint Key, long Amount, bool Balance)>>();
        var b = new byte[8];
        foreach (long obj in hits)
        {
            long cls = Q(obj), parent = Q(obj + parentOff);
            long pcls = parent == 0 ? 0 : Q(parent), root = parent == 0 ? 0 : Q(parent + parentOff), rcls = root == 0 ? 0 : Q(root);
            uint key = Read(obj + keyOff, b, 4) ? BinaryPrimitives.ReadUInt32LittleEndian(b) : 0;
            long amount = Q(obj + amountOff);
            bool balance = Q(obj + listOff) != 0 && Q(obj + listOff + 8) != 0;
            var g = (cls, pcls, rcls);
            if (!groups.TryGetValue(g, out var list)) groups[g] = list = new();
            list.Add((obj, key, amount, balance));
        }
        bool InExe(long v) => v >= ModBase && v < ModEnd;
        foreach (var ((cls, pcls, rcls), list) in groups.OrderByDescending(g => g.Value.Select(x => x.Key).Distinct().Count()).ThenByDescending(g => g.Value.Count).Take(12))
        {
            bool route = InExe(cls) && InExe(pcls) && InExe(rcls);
            Console.WriteLine($"\nclass {Where(cls)}  owner {(pcls == 0 ? "-" : Where(pcls))}  root {(rcls == 0 ? "-" : Where(rcls))}  " +
                              $"{list.Count} objects, keys {string.Join(",", list.Select(x => x.Key).Distinct().OrderBy(k => k))}{(route ? "  <- full route" : "")}");
            foreach (var x in list.OrderBy(x => x.Key).ThenByDescending(x => x.Balance).Take(20))
            {
                string name = names[x.Key];
                string mark = want.TryGetValue(name, out long w) ? (w == x.Amount ? "  = expected" : $"  (expected {w})") : "";
                Console.WriteLine($"  {name,-10} {x.Amount,10}  {(x.Balance ? "balance" : "pending/placeholder")}  0x{x.Obj:X}{mark}");
            }
        }
        var best = groups.Where(g => InExe(g.Key.Item1) && InExe(g.Key.Item2) && InExe(g.Key.Item3))
                         .Where(g => g.Value.Where(x => x.Balance).Select(x => x.Key).Distinct().Count() == names.Count).ToList();
        Console.WriteLine();
        if (best.Count == 1)
        {
            var (c, p, r) = best[0].Key;
            Console.WriteLine($"candidate: classRva 0x{c - ModBase:X}  ownerClassRva 0x{p - ModBase:X}  rootClassRva 0x{r - ModBase:X}");
            Console.WriteLine("Copy these into offsets/<new version>/memory.json, then run: cdprobe verify offsets/<new version>/memory.json");
        }
        else Console.WriteLine($"no unique candidate ({best.Count} groups cover all five reserves with a balance object). " +
                               "If none: the object layout changed; find a reserve by value (cdprobe val <amount>) and re-derive the offsets.");
    }

    /// <summary>
    /// Checks a memory.json against the running game exactly as the reader does (same code path:
    /// CampLedger.Reader.MemorySource), and prints each reserve. Pass the file you want to test.
    /// </summary>
    static void Verify(string layoutFile)
    {
        var game = Process.GetProcessesByName("CrimsonDesert").First();
        using var m = CampLedger.Reader.MemorySource.FromFile(game, layoutFile);
        if (m.Problem is not null) { Console.WriteLine("unavailable: " + m.Problem); return; }
        int n = m.Locate();
        Console.WriteLine($"located {n} of 5");
        var r = m.ReadReserves();
        Console.WriteLine(r is null ? "reserves: unknown" : $"funds {r.Funds}  food {r.Food}  timber {r.Timber}  stone {r.Stone}  armaments {r.Armaments}");
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
        var cat = Cat(archive);
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
