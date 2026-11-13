using System.Buffers.Binary;
using System.Text;

namespace CampLedger.Reader;

/// <summary>
/// The game's condition expressions (conditioninfo) and the name tables they refer to. Read-only reference data.
/// Layout and evidence: docs/re-notes/available-missions.md.
/// </summary>
public sealed class ConditionBook
{
    readonly Dictionary<uint, string> _expr = new();
    readonly Dictionary<string, string> _macro = new();
    readonly Dictionary<string, Dictionary<string, uint>> _names = new();

    /// <summary>
    /// <paramref name="conditions"/>: conditioninfo copies, base first. <paramref name="nameTables"/>: kind
    /// ("gameplayvariable", "knowledge", "mission", "quest", "stage", "sublevel", "character", "node") to its copies.
    /// </summary>
    public ConditionBook(List<Table> conditions, IReadOnlyDictionary<string, List<Table>> nameTables)
    {
        foreach (var t in conditions)
            foreach (var key in t.Records.Keys)
                if (ParseRecord(t, key) is (string name, string expr))
                {
                    _expr[key] = expr;
                    _macro[name] = expr;
                }
        foreach (var (kind, tables) in nameTables)
        {
            var map = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var t in tables)
                foreach (var key in t.Records.Keys)
                    if (t.Name(key) is string n) map[n] = key;
            _names[kind] = map;
        }
    }

    static readonly string[] NameKinds = { "gameplayvariable", "knowledge", "mission", "quest", "stage", "sublevel", "character", "node" };
    static readonly Dictionary<string, string> TableOf = new()
    {
        ["gameplayvariable"] = "gameplayvariableinfo", ["knowledge"] = "knowledgeinfo", ["mission"] = "missioninfo",
        ["quest"] = "questinfo", ["stage"] = "stageinfo", ["sublevel"] = "sublevelinfo", ["character"] = "characterinfo",
        ["node"] = "factionnode",
    };

    /// <summary>Loads from the install, or returns null with the reason.</summary>
    public static ConditionBook? TryLoad(Archive archive, out string? problem)
    {
        try
        {
            var names = NameKinds.ToDictionary(k => k, k => archive.Tables(TableOf[k]));
            problem = null;
            return new ConditionBook(archive.Tables("conditioninfo"), names);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problem = e.Message;
            return null;
        }
    }

    public int Count => _expr.Count;
    public bool Has(uint key) => _expr.ContainsKey(key);
    public string? Expression(uint key) => _expr.GetValueOrDefault(key);
    public string? Macro(string name) => _macro.GetValueOrDefault(name);
    public uint? KeyOf(string kind, string name) =>
        _names.TryGetValue(kind, out var m) && m.TryGetValue(name, out uint k) ? k : null;

    const string ParserSuffix = ", parserType:";

    /// <summary>
    /// A conditioninfo record starts with a length-prefixed name. Most names are the expression followed by
    /// ", parserType:N"; named macros (e.g. OperationPlayable) carry the expression as the next string after the key.
    /// </summary>
    public static (string Name, string Expr)? ParseRecord(Table t, uint key)
    {
        var (s0, e0) = t.Records[key];
        if (e0 - s0 < 4) return null;
        int nl = (int)BinaryPrimitives.ReadUInt32LittleEndian(t.Body.AsSpan(s0));
        if (nl is < 1 or > 4000 || s0 + 4 + nl > e0) return null;
        string name = Encoding.ASCII.GetString(t.Body, s0 + 4, nl);   // long expressions exceed Table.Name's limit
        int cut = name.LastIndexOf(ParserSuffix, StringComparison.Ordinal);
        if (cut > 0) return (name, name[..cut]);
        var (s, e) = t.Records[key];
        var rec = t.Body.AsSpan(s, e - s);
        int p = 4 + Encoding.ASCII.GetByteCount(name);
        if (p + 4 > rec.Length || BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(p)) != key) return null;
        for (int i = p + 4; i + 4 < rec.Length; i++)
        {
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(i));
            if (len is < 3 or > 1000 || i + 4 + len > rec.Length) continue;
            var str = rec.Slice(i + 4, len);
            bool ok = true;
            foreach (byte c in str) if (c is < 32 or > 126) { ok = false; break; }
            if (ok) return (name, Encoding.ASCII.GetString(str));
        }
        return null;
    }
}

/// <summary>Per-save facts the conditions are checked against. Built by the source; null sets mean "not read".</summary>
public sealed class GameFacts
{
    public HashSet<uint>? GamePlayVariables { get; init; }
    public HashSet<uint>? Knowledge { get; init; }
    public HashSet<uint>? CompletedMissions { get; init; }
    /// <summary>Every mission with any state in the save (started, in progress or done).</summary>
    public HashSet<uint>? KnownMissions { get; init; }
    public HashSet<uint>? CompletedQuests { get; init; }
    public HashSet<uint>? CompletedStages { get; init; }
    /// <summary>Sub-level by sublevelinfo name; a missing name is level 0.</summary>
    public Dictionary<string, long>? Sublevels { get; init; }
    /// <summary>Hired comrades: character key to count.</summary>
    public Dictionary<uint, int>? Hired { get; init; }
    /// <summary>Node key to (save _factionState, _isBlock present).</summary>
    public Dictionary<uint, (long State, bool Blocked)>? Nodes { get; init; }
    /// <summary>Node key to the operation running there.</summary>
    public Dictionary<uint, uint>? Running { get; init; }
    /// <summary>True when the player's faction has no management (rent) entries; null when not read or not empty.</summary>
    public bool? NoManagementRents { get; init; }
    /// <summary>Operation internal name (Node_...) to operation key, from the game data.</summary>
    public Func<string, uint?>? OperationKeyOf { get; init; }
}

public enum Tri { False, True, Unknown }

/// <summary>Result of checking one expression: the value, plus what it relied on or could not establish.</summary>
public sealed class Verdict
{
    public Tri Value { get; set; }
    public List<string> Assumptions { get; } = new();
    public List<string> Unknown { get; } = new();
}

/// <summary>
/// Evaluates condition expressions such as
/// <c>CheckGamePlayVariable(BaseCamp_Lv3) &amp;&amp; !CompleteQuest(Quest_X)</c> with three-valued logic.
/// Any function not listed here, or a fact the save does not hold, gives Unknown (never a guess).
/// </summary>
public sealed class ConditionEvaluator
{
    readonly ConditionBook _book;
    readonly GameFacts _facts;

    public ConditionEvaluator(ConditionBook book, GameFacts facts) { _book = book; _facts = facts; }

    public Verdict Evaluate(string expression)
    {
        var v = new Verdict();
        try
        {
            var p = new Parser(Tokenize(expression));
            var node = p.Or();
            if (!p.AtEnd) throw new FormatException("trailing tokens");
            v.Value = Eval(node, v, 0);
        }
        catch (FormatException e)
        {
            v.Value = Tri.Unknown;
            v.Unknown.Add($"could not parse \"{expression}\": {e.Message}");
        }
        return v;
    }

    // ---- syntax ----

    abstract record Node;
    sealed record Not(Node Inner) : Node;
    sealed record And(Node L, Node R) : Node;
    sealed record Or(Node L, Node R) : Node;
    sealed record Call(string Name, List<string> Args, string? Op, string? Rhs, string Text) : Node;

    static List<string> Tokenize(string s)
    {
        var t = new List<string>();
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (i + 1 < s.Length && (s.Substring(i, 2) is "&&" or "||" or "<=" or ">=" or "==" or "!="))
            { t.Add(s.Substring(i, 2)); i += 2; continue; }
            if ("()!,<>".IndexOf(c) >= 0) { t.Add(c.ToString()); i++; continue; }
            int j = i;
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] is '_' or '.' or '-')) j++;
            if (j == i) throw new FormatException($"unexpected '{c}'");
            t.Add(s[i..j]);
            i = j;
        }
        return t;
    }

    sealed class Parser
    {
        readonly List<string> _t;
        int _i;
        public Parser(List<string> t) { _t = t; }
        public bool AtEnd => _i >= _t.Count;
        string? Peek => _i < _t.Count ? _t[_i] : null;
        string Next() => _i < _t.Count ? _t[_i++] : throw new FormatException("unexpected end");
        void Expect(string s) { if (Next() != s) throw new FormatException($"expected '{s}'"); }

        public Node Or()
        {
            var l = And();
            while (Peek == "||") { _i++; l = new Or(l, And()); }
            return l;
        }

        Node And()
        {
            var l = Unary();
            while (Peek == "&&") { _i++; l = new And(l, Unary()); }
            return l;
        }

        Node Unary()
        {
            if (Peek == "!") { _i++; return new Not(Unary()); }
            if (Peek == "(") { _i++; var n = Or(); Expect(")"); return Compare(n); }
            int start = _i;
            string name = Next();
            if (!char.IsLetter(name[0])) throw new FormatException($"unexpected '{name}'");
            var args = new List<string>();
            if (Peek == "(")
            {
                _i++;
                while (Peek != ")")
                {
                    args.Add(Next());
                    if (Peek == ",") _i++;
                }
                Expect(")");
            }
            string? op = null, rhs = null;
            if (Peek is "<" or ">" or "<=" or ">=" or "==" or "!=") { op = Next(); rhs = Next(); }
            string text = name + (args.Count > 0 || _t.ElementAtOrDefault(start + 1) == "(" ? $"({string.Join(", ", args)})" : "")
                          + (op is null ? "" : $"{op}{rhs}");
            return new Call(name, args, op, rhs, text);
        }

        // "(A<4)" style: a parenthesised call can still carry a comparison after it.
        Node Compare(Node n) => n;
    }

    // ---- evaluation ----

    Tri Eval(Node n, Verdict v, int depth)
    {
        if (depth > 16) { v.Unknown.Add("macro nesting too deep"); return Tri.Unknown; }
        switch (n)
        {
            case Not x:
                return Eval(x.Inner, v, depth) switch { Tri.True => Tri.False, Tri.False => Tri.True, _ => Tri.Unknown };
            case And x:
            {
                var l = Eval(x.L, v, depth);
                if (l == Tri.False) return Tri.False;
                var r = Eval(x.R, v, depth);
                return r == Tri.False ? Tri.False : l == Tri.True && r == Tri.True ? Tri.True : Tri.Unknown;
            }
            case Or x:
            {
                var l = Eval(x.L, v, depth);
                if (l == Tri.True) return Tri.True;
                var r = Eval(x.R, v, depth);
                return r == Tri.True ? Tri.True : l == Tri.False && r == Tri.False ? Tri.False : Tri.Unknown;
            }
            case Call c:
                return Function(c, v, depth);
        }
        return Tri.Unknown;
    }

    Tri Unknown(Verdict v, string why) { v.Unknown.Add(why); return Tri.Unknown; }

    static Tri Of(bool b) => b ? Tri.True : Tri.False;

    Tri Has(Verdict v, Call c, string kind, HashSet<uint>? set, string what)
    {
        if (c.Args.Count != 1 || c.Op is not null) return Unknown(v, $"{c.Text}: unexpected form");
        if (set is null) return Unknown(v, $"{c.Text}: {what} not read from the save");
        if (_book.KeyOf(kind, c.Args[0]) is not uint key) return Unknown(v, $"{c.Text}: {c.Args[0]} not in game data");
        return Of(set.Contains(key));
    }

    static Tri CompareNumbers(long left, string? op, string? rhs)
    {
        if (op is null || !long.TryParse(rhs, out long r)) return Tri.Unknown;
        return Of(op switch
        {
            "<" => left < r, ">" => left > r, "<=" => left <= r, ">=" => left >= r, "==" => left == r, "!=" => left != r,
            _ => false,
        });
    }

    Tri Function(Call c, Verdict v, int depth)
    {
        switch (c.Name)
        {
            case "Macro":
            {
                if (c.Args.Count != 1 || _book.Macro(c.Args[0]) is not string body) return Unknown(v, $"{c.Text}: macro not found");
                try
                {
                    var p = new Parser(Tokenize(body));
                    var node = p.Or();
                    return Eval(node, v, depth + 1);
                }
                catch (FormatException e) { return Unknown(v, $"{c.Text}: {e.Message}"); }
            }
            case "CheckGamePlayVariable":
                return Has(v, c, "gameplayvariable", _facts.GamePlayVariables, "game-play variables");
            case "CheckKnowledge":
            case "KnowledgeKey":
                return Has(v, c, "knowledge", _facts.Knowledge, "knowledge");
            case "CompleteMission":
            case "MissionKey":
                return Has(v, c, "mission", _facts.CompletedMissions, "mission state");
            case "CompleteQuest":
                return Has(v, c, "quest", _facts.CompletedQuests, "quest state");
            case "CompleteStage":
                return Has(v, c, "stage", _facts.CompletedStages, "stage state");
            case "CompleteSubMission":
            {
                // Sub-mission progress is not decoded. A finished mission has finished its sub-missions; a mission
                // the save has never recorded has finished none. Anything in between is unknown.
                if (c.Args.Count != 2) return Unknown(v, $"{c.Text}: unexpected form");
                if (_facts.CompletedMissions is null || _facts.KnownMissions is null) return Unknown(v, $"{c.Text}: mission state not read");
                if (_book.KeyOf("mission", c.Args[0]) is not uint mk) return Unknown(v, $"{c.Text}: {c.Args[0]} not in game data");
                if (_facts.CompletedMissions.Contains(mk)) return Tri.True;
                if (!_facts.KnownMissions.Contains(mk)) return Tri.False;
                return Unknown(v, $"{c.Text}: mission in progress, sub-mission progress not decoded");
            }
            case "GetCurrentSubLevel":
            {
                if (c.Args.Count != 1 || c.Op is null) return Unknown(v, $"{c.Text}: unexpected form");
                if (_facts.Sublevels is null) return Unknown(v, $"{c.Text}: sub-levels not read");
                var r = CompareNumbers(_facts.Sublevels.GetValueOrDefault(c.Args[0]), c.Op, c.Rhs);
                return r == Tri.Unknown ? Unknown(v, $"{c.Text}: unexpected comparison") : r;
            }
            case "IsHiredMercenary":
            case "GetMercenaryHiredCount":
            {
                if (c.Args.Count != 1) return Unknown(v, $"{c.Text}: unexpected form");
                if (_facts.Hired is null) return Unknown(v, $"{c.Text}: comrades not read");
                if (_book.KeyOf("character", c.Args[0]) is not uint ck) return Unknown(v, $"{c.Text}: {c.Args[0]} not in game data");
                int count = _facts.Hired.GetValueOrDefault(ck);
                if (c.Name == "IsHiredMercenary") return c.Op is null ? Of(count > 0) : Unknown(v, $"{c.Text}: unexpected form");
                var r = CompareNumbers(count, c.Op, c.Rhs);
                return r == Tri.Unknown ? Unknown(v, $"{c.Text}: unexpected comparison") : r;
            }
            case "GetFactionNodeState":
            {
                // Save _factionState 2 without _isBlock reads as Normal; state 0 with _isBlock as Block (2.04.08).
                if (c.Args.Count != 1 || c.Op is not ("==" or "!=") || c.Rhs is not ("Normal" or "Block"))
                    return Unknown(v, $"{c.Text}: unexpected form");
                if (_facts.Nodes is null) return Unknown(v, $"{c.Text}: node states not read");
                if (_book.KeyOf("node", c.Args[0]) is not uint nk || !_facts.Nodes.TryGetValue(nk, out var st))
                    return Unknown(v, $"{c.Text}: node not in the save");
                string? state = st is { State: 2, Blocked: false } ? "Normal" : st is { State: 0, Blocked: true } ? "Block" : null;
                if (state is null) return Unknown(v, $"{c.Text}: node state {st.State} not mapped");
                return Of((state == c.Rhs) == (c.Op == "=="));
            }
            case "CheckOperationing":
            {
                if (c.Args.Count != 2 || c.Op is not null) return Unknown(v, $"{c.Text}: unexpected form");
                if (_facts.Running is null || _facts.OperationKeyOf is null) return Unknown(v, $"{c.Text}: running missions not read");
                if (_book.KeyOf("node", c.Args[0]) is not uint nk || _facts.OperationKeyOf(c.Args[1]) is not uint ok)
                    return Unknown(v, $"{c.Text}: not in game data");
                return Of(_facts.Running.TryGetValue(nk, out uint running) && running == ok);
            }
            case "CheckFactionManagementRent":
                // The save omits empty lists; with no management entries on the player's faction nothing is rented.
                if (_facts.NoManagementRents == true) return Tri.False;
                return Unknown(v, $"{c.Text}: rented properties not decoded");
            case "CheckFactionWantedLevel":
            {
                // Not stored in the save. A wanted level of 4 or more is rare and temporary; assume below.
                if (c.Op is "<" or "<=" && long.TryParse(c.Rhs, out long n) && n >= 1)
                {
                    v.Assumptions.Add($"wanted level assumed below {(c.Op == "<" ? n : n + 1)} (not in the save)");
                    return Tri.True;
                }
                return Unknown(v, $"{c.Text}: wanted level not in the save");
            }
        }
        if (c.Args.Count == 0 && c.Op is null && _book.Macro(c.Name) is not null)
            return Function(new Call("Macro", new List<string> { c.Name }, null, null, c.Text), v, depth);
        return Unknown(v, $"{c.Text}: not decoded");
    }
}
