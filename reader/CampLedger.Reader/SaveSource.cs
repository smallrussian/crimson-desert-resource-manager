namespace CampLedger.Reader;

/// <summary>
/// Tier 1: reads camp state from the newest save. The save is copied to a temp file first and only the copy
/// is opened. Field locations: docs/re-notes/save-contents.md.
/// </summary>
public sealed class SaveSource : ISource
{
    /// <summary>Recruitment manager; never dispatchable (owner decision 2026-11-11).</summary>
    const string NeverDispatchable = "NHM_Unique_Marius_468";

    readonly string _saveRoot;
    readonly string? _gameVersion;
    readonly Catalogue? _catalogue;
    readonly string? _catalogueProblem;
    public string Name => "save";

    public SaveSource(string? saveRoot = null, string? gameVersion = null, Catalogue? catalogue = null,
                      string? catalogueProblem = null)
    {
        _catalogue = catalogue;
        _catalogueProblem = catalogueProblem;
        _saveRoot = saveRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pearl Abyss", "CD", "save");
        _gameVersion = gameVersion;
    }

    /// <summary>Newest save.save under the save root, by last write time.</summary>
    public FileInfo? NewestSave()
    {
        if (!Directory.Exists(_saveRoot)) return null;
        return new DirectoryInfo(_saveRoot).EnumerateFiles("save.save", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
    }

    public SourceResult Read()
    {
        var newest = NewestSave();
        return newest is null
            ? new SourceResult.Unavailable($"no save.save under {_saveRoot}")
            : ReadFile(newest.FullName);
    }

    public SourceResult ReadFile(string path)
    {
        string copy = Path.Combine(Path.GetTempPath(), $"campledger-{Guid.NewGuid():N}.save");
        try
        {
            File.Copy(path, copy);
            File.SetAttributes(copy, FileAttributes.Normal); // the copy is ours; a read-only source must not block cleanup
            byte[] file;
            using (var fs = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                file = new byte[fs.Length];
                fs.ReadExactly(file);
            }
            return new SourceResult.Ok(Build(new SaveBody(SaveCrypto.Decode(file))));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                      or IndexOutOfRangeException or ArgumentException)
        {
            return new SourceResult.Unavailable($"{Path.GetFileName(Path.GetDirectoryName(path))}: {e.Message}");
        }
        finally
        {
            try { File.Delete(copy); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    Snapshot Build(SaveBody body)
    {
        var unknowns = new List<string>
        {
            "mission rewards are base amounts; bonus inputs are in missions[].bonus and the camp level in campLevel",
        };
        if (_catalogue is { HasDisplayNames: false })
            unknowns.Add("English names not loaded: " + _catalogue.NamesProblem);
        if (_catalogue is null)
            unknowns.Add("game data not loaded, so mission details and workers.total are unknown: "
                         + (_catalogueProblem ?? "no install found"));

        var reserves = new Reserves();
        foreach (var e in List(body.Decode("InventorySaveData"), "_inventorylist"))
        {
            if (Long(e, "_inventoryKey") != 1) continue;
            foreach (var item in List(e, "_itemList"))
            {
                long? amount = Long(item, "_stackCount");
                switch (Long(item, "_itemKey"))
                {
                    case 11: reserves.Funds = amount; break;
                    case 12: reserves.Food = amount; break;
                    case 13: reserves.Timber = amount; break;
                    case 14: reserves.Stone = amount; break;
                    case 15: reserves.Armaments = amount; break;
                }
            }
        }
        if (!reserves.Complete) unknowns.Add("one or more reserves were not present in the save");

        var missions = new List<Mission>();
        int inUse = 0;
        var characterOf = new Dictionary<long, long>();
        var assignedTo = new Dictionary<long, Mission>();   // mercenary number -> running mission
        foreach (var merc in List(body.Decode("MercenaryClanSaveData"), "_mercenaryDataList"))
            if (Long(merc, "_mercenaryNo") is long no && Long(merc, "_characterKey") is long ck) characterOf[no] = ck;
        var sublevels = new Dictionary<string, long>();
        var subBlock = body.Decode("SubLevelSaveData");
        foreach (var e in List(subBlock, "_list"))
            if (Long(e, "_key") is long k && Long(e, "_level") is long lv && _catalogue?.SublevelName((uint)k) is string sn)
                sublevels[sn] = lv;
        var faction = body.Decode("FactionSaveData");
        if (faction is null) unknowns.Add("FactionSaveData block missing: no mission state");
        foreach (var node in List(faction, "_factionNodeElementSaveDataList"))
        {
            if (!node.ContainsKey("_operationKey")) continue;
            int workers = List(node, "_operatorMercenaryList").Count();
            inUse += workers;
            double? progress = node.TryGetValue("_operationCurrentProgress", out var p) && p is double d ? d : null;
            var mission = new Mission
            {
                Active = true,
                Workers = workers,
                NodeKey = Long(node, "_ownerFactionKey"),
                OperationKey = Long(node, "_operationKey"),
                // _operationCurrentProgress is elapsed in-game hours x 10 (docs/re-notes/mission-static-data.md).
                ElapsedHours = progress is null ? null : Math.Round(progress.Value / 10.0, 2),
                Repeat = node.TryGetValue("_operationShouldRepeat", out var r) && r is bool b ? b : null,
            };
            Describe(mission, unknowns);
            foreach (var m in List(node, "_operatorMercenaryList"))
                if (Long(m, "_mercenaryNo") is long mno) assignedTo[mno] = mission;
            mission.Bonus = BonusOf(mission, List(node, "_operatorMercenaryList").Select(m => Long(m, "_mercenaryNo")).ToList(),
                                    characterOf, subBlock is null ? null : sublevels, unknowns);
            missions.Add(mission);
        }

        var roster = Roster(body, assignedTo, unknowns);
        var facts = Facts(body, faction, subBlock is null ? null : sublevels);
        int? campLevel = CampLevel(facts);
        if (campLevel is null) unknowns.Add("camp level unknown: no BaseCamp_Lv game-play variable read");

        ulong? clock = null;
        foreach (var f in List(body.Decode("TransformSaveData"), "_fieldSaveDataList"))
            if (f.TryGetValue("_fieldTimeRaw", out var t) && t is ulong u) clock = u;
        if (clock is null) unknowns.Add("in-game clock not present in the save");

        return new Snapshot
        {
            GameVersion = _gameVersion,
            Source = "save",
            Reserves = reserves,
            Workers = new Workers { Total = roster.Total, InUse = faction is null ? null : inUse },
            Comrades = roster.List,
            Missions = missions,
            CampLevel = campLevel,
            AvailableMissions = Available(faction, missions, sublevels, subBlock is not null, facts, unknowns),
            InGameClock = clock,
            Unknowns = unknowns,
        };
    }

    /// <summary>Random tables list dozens of items (51 for Lift Repair Support); the text keeps the first few.</summary>
    const int MaxExtras = 12;

    /// <summary>Adds the game's reference data for one running mission. Leaves fields null when anything fails.</summary>
    void Describe(Mission m, List<string> unknowns)
    {
        if (_catalogue is null || m.OperationKey is not long key) return;
        var op = _catalogue.Operation((uint)key);
        if (op is null || op.Category is null)
        {
            unknowns.Add($"operation {key}: not found in game data");
            return;
        }
        m.InternalName = op.InternalName?.StartsWith("Node_") == true ? op.InternalName[5..] : op.InternalName;
        m.Category = op.Category;
        m.Type = _catalogue.OperationDisplayName((uint)key);
        m.Location = _catalogue.NodeLocation(op.NodeKey);
        m.Name = m.Location ?? m.Type ?? m.InternalName;
        if (_catalogue.HasDisplayNames && m.Type is null) unknowns.Add($"{m.InternalName}: English mission name not found");
        m.Role = op.Role;
        m.Region = _catalogue.NodeRegion(op.NodeKey) ?? Catalogue.RegionOf(op.InternalName);
        m.WorkersMin = op.MembersMin;
        m.WorkersMax = op.MembersMax;
        m.DurationHours = op.DurationHours;
        if (op.DurationHours is null) unknowns.Add($"{m.Name}: duration not found in game data");

        // Items that are not reserves (ore for Shipbuilding) do not touch the reserves; list them on their own.
        Reserves cost = Zero();
        var itemCost = new List<string>();
        foreach (var (item, amount) in op.Cost)
            if (!Add(cost, item, amount)) itemCost.Add($"{_catalogue.ItemName(item) ?? item.ToString()} x{amount}");
        m.Cost = cost;
        m.ItemCost = itemCost.Count == 0 ? null : string.Join("; ", itemCost);

        Reserves? reward = Zero();
        var extras = new List<string>();
        foreach (uint set in op.DropSets)
        {
            var items = _catalogue.DropSet(set);
            if (items is null)
            {
                unknowns.Add($"{m.Name}: reward table {set} did not validate");
                reward = null;
                break;
            }
            foreach (var it in items)
            {
                if (it.Max == 0) continue;                     // x0 entries give nothing (placeholders on build rewards)
                bool certain = it.Chance >= 1.0 && it.Min == it.Max;
                if (certain && Catalogue.Reserve.ContainsKey(it.Item)) Add(reward, it.Item, it.Min);
                else
                {
                    // Contribution rewards point at a sub-level (600 = Contribution_Graymane), not an item.
                    string name = NonEmpty(_catalogue.ItemName(it.Item)) ?? NonEmpty(_catalogue.SublevelName(it.Item)) ?? it.Item.ToString();
                    string qty = it.Min == it.Max ? $"x{it.Min}" : $"x{it.Min}-{it.Max}";
                    extras.Add(it.Chance >= 1.0 ? $"{name} {qty}" : $"{name} {qty} ({it.Chance * 100:0}% chance)");
                }
            }
        }
        if (op.DropSets.Count == 0)
        {
            unknowns.Add($"{m.Name}: no reward table found");
            reward = null;
        }
        m.Reward = reward;
        m.PersonalStorageReward = reward is null || extras.Count == 0 ? null
            : extras.Count <= MaxExtras ? string.Join("; ", extras)
            : string.Join("; ", extras.Take(MaxExtras)) + $"; and {extras.Count - MaxExtras} more";
    }

    /// <summary>
    /// Per-save facts for the game's start conditions. A block that is missing leaves its set null (unknown).
    /// Quest, mission and stage state 5 is "completed" (entries with a completion time; 2.04.08).
    /// </summary>
    GameFacts Facts(SaveBody body, Dictionary<string, object?>? faction, Dictionary<string, long>? sublevels)
    {
        static HashSet<uint> Keys(IEnumerable<Dictionary<string, object?>> list, string field, Func<Dictionary<string, object?>, bool> keep)
            => list.Where(keep).Select(e => Long(e, field)).OfType<long>().Select(k => (uint)k).ToHashSet();
        static bool Done(Dictionary<string, object?> e) => Long(e, "_state") == 5;

        var gpv = body.Decode("GamePlayVariableSaveData");
        var know = body.Decode("KnowledgeSaveData");
        var quest = body.Decode("QuestSaveData");
        var clan = body.Decode("MercenaryClanSaveData");
        Dictionary<uint, int>? hired = null;
        if (clan is not null)
        {
            hired = new();
            foreach (var m in List(clan, "_mercenaryDataList"))
                if (Long(m, "_characterKey") is long ck) hired[(uint)ck] = hired.GetValueOrDefault((uint)ck) + 1;
        }
        Dictionary<uint, (long, bool)>? nodes = null;
        Dictionary<uint, uint>? running = null;
        if (faction is not null)
        {
            nodes = new();
            running = new();
            foreach (var n in List(faction, "_factionNodeElementSaveDataList"))
            {
                if (Long(n, "_ownerFactionKey") is not long nk) continue;
                nodes[(uint)nk] = (Long(n, "_factionState") ?? 0, n.ContainsKey("_isBlock"));
                if (Long(n, "_operationKey") is long op) running[(uint)nk] = (uint)op;
            }
        }
        return new GameFacts
        {
            GamePlayVariables = gpv is null ? null
                : Keys(List(gpv, "_gamePlayVariableElementSaveData"), "_gamePlayVariableKey", e => e.GetValueOrDefault("_currentVariable") is true),
            Knowledge = know is null ? null : Keys(List(know, "_list"), "_key", _ => true),
            CompletedMissions = quest is null ? null : Keys(List(quest, "_missionStateList"), "_key", Done),
            KnownMissions = quest is null ? null : Keys(List(quest, "_missionStateList"), "_key", _ => true),
            CompletedQuests = quest is null ? null : Keys(List(quest, "_questStateList"), "_questKey", Done),
            CompletedStages = quest is null ? null : Keys(List(quest, "_stageStateData"), "_key", Done),
            Sublevels = sublevels,
            Hired = hired,
            Nodes = nodes,
            Running = running,
            OperationKeyOf = _catalogue is null ? null : _catalogue.OperationKeyOf,
            NoManagementRents = faction is null ? null
                : List(faction, "_factionElementSaveDataList").FirstOrDefault(f => Long(f, "_ownerFactionKey") == PlayerFaction) is { } pf
                  ? !List(pf, "_factionManagementSaveDataList").Any() && !(pf.GetValueOrDefault("_factionManagementSaveDataList") is List<object?> { Count: > 0 })
                  : null,
        };
    }

    /// <summary>Highest N with game-play variable BaseCamp_LvN set (Lv0..Lv5 seen). Owner's save: 4, matching the game.</summary>
    int? CampLevel(GameFacts facts)
    {
        if (_catalogue?.Conditions is not ConditionBook book || facts.GamePlayVariables is null) return null;
        int? level = null;
        for (int n = 0; n <= 9; n++)
            if (book.KeyOf("gameplayvariable", $"BaseCamp_Lv{n}") is uint k && facts.GamePlayVariables.Contains(k)) level = n;
        return level;
    }

    /// <summary>
    /// Missions that can be started (docs/re-notes/available-missions.md): not running; node not blockaded
    /// (`_factionState` 2, no `_isBlock`); node discovered (its Knowledge_Node_ knowledge is in the save); and every
    /// start condition in the mission's record holds. A condition the save cannot settle leaves the mission listed
    /// with <see cref="Mission.Unconfirmed"/> filled in; anything assumed is in <see cref="Mission.Assumptions"/>.
    /// A node held by a faction hostile to the Greymanes is also marked unconfirmed.
    /// </summary>
    List<Mission> Available(Dictionary<string, object?>? faction, List<Mission> running, Dictionary<string, long> sublevels,
                            bool haveSublevels, GameFacts facts, List<string> unknowns)
    {
        var result = new List<Mission>();
        if (_catalogue is null || faction is null) return result;
        if (_catalogue.Conditions is not ConditionBook book)
        {
            unknowns.Add("availableMissions not computed: start conditions not loaded (" + _catalogue.ConditionsProblem + ")");
            return result;
        }
        var eval = new ConditionEvaluator(book, facts);
        var hostile = new HashSet<long>();
        foreach (var f in List(faction, "_factionElementSaveDataList"))
            if (Long(f, "_ownerFactionKey") == PlayerFaction)
                foreach (var r in List(f, "_factionRelationDataList"))
                    if ((Long(r, "_relationTypeRaw") ?? 0) == 0 && Long(r, "_targetFactionKey") is long t) hostile.Add(t);

        var runningOps = running.Select(m => m.OperationKey).ToHashSet();
        var ignored = new List<string>();
        int unconfirmed = 0;
        foreach (var node in List(faction, "_factionNodeElementSaveDataList"))
        {
            if (Long(node, "_ownerFactionKey") is not long nodeKey || Long(node, "_factionState") != 2
                || node.ContainsKey("_isBlock")) continue;
            var nodeNotes = new List<string>();
            if (_catalogue.NodeKnowledgeKey((uint)nodeKey) is uint nk)
            {
                if (facts.Knowledge is null) nodeNotes.Add("node discovery unknown (knowledge not read)");
                else if (!facts.Knowledge.Contains(nk)) continue;   // not discovered
            }
            else nodeNotes.Add("node discovery unknown (no knowledge key in the node record)");
            long? holder = Long(node, "_conquerorFactionKey") ?? Long(node, "_blockadingFactionKey");
            if (holder is long h && hostile.Contains(h)) nodeNotes.Add($"node held by faction {h}, hostile to the Greymanes");

            foreach (var op in _catalogue.OperationsAt((uint)nodeKey))
            {
                if (runningOps.Contains(op.Key)) continue;
                var notes = new List<string>(nodeNotes);
                var assumptions = new List<string>();
                var keys = _catalogue.OperationConditionKeys(op.Key, book.Has);
                bool blocked = false;
                if (keys is null) notes.Add("start conditions not found in the mission record");
                else
                    foreach (uint k in keys)
                    {
                        var v = eval.Evaluate(book.Expression(k)!);
                        if (v.Value == Tri.False) { blocked = true; break; }
                        notes.AddRange(v.Unknown);
                        assumptions.AddRange(v.Assumptions);
                    }
                if (blocked) continue;
                var m = new Mission { Active = false, NodeKey = nodeKey, OperationKey = op.Key };
                Describe(m, ignored);
                m.Bonus = BonusOf(m, new List<long?>(), new Dictionary<long, long>(), haveSublevels ? sublevels : null, ignored);
                if (m.Bonus is not null) m.Bonus.Comrades = null;   // nobody assigned yet
                m.Assumptions = assumptions.Count == 0 ? null : assumptions.Distinct().ToList();
                m.Unconfirmed = notes.Count == 0 ? null : notes.Distinct().ToList();
                if (m.Unconfirmed is not null) unconfirmed++;
                result.Add(m);
            }
        }
        if (unconfirmed > 0)
            unknowns.Add($"availableMissions: {unconfirmed} of {result.Count} depend on conditions the save does not settle (see each mission's unconfirmed)");
        return result;
    }

    /// <summary>The player's faction (Greymanes): its faction element has _leaderCharacterKey 1.</summary>
    const long PlayerFaction = 1000000;

    /// <summary>Church sub-level per region (sublevelinfo names). Regions not listed have no known church.</summary>
    static readonly Dictionary<string, string> Religion = new()
    {
        ["Hernand"] = "Religion_Hernand", ["Demeniss"] = "Religion_Demenissian", ["Delesyia"] = "Religion_Delesyian",
    };

    /// <summary>
    /// Regions with no church sub-level in the game data, checked in game to give +0% conversion
    /// (Pailune: Beighen "Advanced Logging" shows +0%, owner 2026-11-13). Crimson Desert is not checked: unknown.
    /// </summary>
    static readonly HashSet<string> NoChurch = new() { "Pailune" };

    MissionBonus? BonusOf(Mission m, List<long?> mercs, Dictionary<long, long> characterOf,
                          Dictionary<string, long>? sublevels, List<string> unknowns)
    {
        if (_catalogue is null || m.OperationKey is not long opKey) return null;
        var op = _catalogue.Operation((uint)opKey);
        var bonus = new MissionBonus
        {
            SkillType = op?.SkillStatus is uint st ? _catalogue.SkillTypeOf(st) : null,
        };
        // No save entry for a region's church means level 0 (Burnhand, Demeniss: none in the save, game shows +0%).
        if (m.Region is string region && Religion.TryGetValue(region, out var sub) && sublevels is not null)
            bonus.ConversionPercent = (int)(2 * sublevels.GetValueOrDefault(sub));
        else if (m.Region is string r && NoChurch.Contains(r))
            bonus.ConversionPercent = 0;
        else unknowns.Add($"{m.Name}: conversion bonus unknown (no church level for region {m.Region ?? "unknown"})");

        var comrades = new List<AssignedComrade>();
        foreach (long? no in mercs)
        {
            string? problem = null;
            long ck = 0;
            IReadOnlyList<uint>? slots = null;
            if (no is not long n) problem = "assigned comrade has no number";
            else if (!characterOf.TryGetValue(n, out ck)) problem = $"comrade {n} not in the comrade list";
            else if ((slots = _catalogue.SkillSlots((uint)ck)) is null) problem = $"character {ck}: skill slots did not match the known layout";
            if (problem is not null || slots is null)
            {
                unknowns.Add($"{m.Name}: comrade skills unknown ({problem})");
                comrades = null;
                break;
            }
            comrades.Add(new AssignedComrade
            {
                Name = _catalogue.CharacterDisplayName((uint)ck),
                CharacterKey = ck,
                Slots = slots.Select(k => _catalogue.Skill(k) is CampSkill cs ? new SlotSkill(cs.Type, cs.Percent) : null).ToList(),
            });
        }
        bonus.Comrades = comrades;
        if (bonus.SkillType is null) unknowns.Add($"{m.Name}: skill type unknown, so the skill bonus is unknown");
        return bonus;
    }

    /// <summary>
    /// Dispatchable comrades: human comrades (character name NHM_/NHW_/NDM_/NOM_) who have a camp spawn position,
    /// except Marius. Matched the game's "Comrades Found: 37" name for name on 2026-11-11 (one save).
    /// </summary>
    (int? Total, List<Comrade>? List) Roster(SaveBody body, Dictionary<long, Mission> assignedTo, List<string> unknowns)
    {
        if (_catalogue is null) return (null, null);
        var clan = body.Decode("MercenaryClanSaveData");
        if (clan is null)
        {
            unknowns.Add("MercenaryClanSaveData missing: workers.total unknown");
            return (null, null);
        }
        var list = new List<Comrade>();
        foreach (var merc in List(clan, "_mercenaryDataList"))
        {
            if (Long(merc, "_characterKey") is not long ck) continue;
            string? name = _catalogue.CharacterName((uint)ck);
            if (name is null)
            {
                unknowns.Add($"character {ck} not in game data: workers.total unknown");
                return (null, null);
            }
            bool human = name.StartsWith("NHM_") || name.StartsWith("NHW_") || name.StartsWith("NDM_") || name.StartsWith("NOM_");
            if (!human || !merc.ContainsKey("_spawnPosition") || name == NeverDispatchable) continue;
            long? no = Long(merc, "_mercenaryNo");
            var slots = _catalogue.SkillSlots((uint)ck);
            Mission? on = no is long n && assignedTo.TryGetValue(n, out var m) ? m : null;
            list.Add(new Comrade
            {
                Name = _catalogue.CharacterDisplayName((uint)ck) ?? name,
                CharacterKey = ck,
                MercenaryNo = no,
                Slots = slots?.Select(k => _catalogue.Skill(k) is CampSkill cs ? new ComradeSkill(cs.Type, cs.Tier, cs.Percent) : null).ToList(),
                MissionName = on is null ? null : on.Location is null ? on.Type ?? on.Name : $"{on.Location}: {on.Type}",
                OperationKey = on?.OperationKey,
            });
        }
        return (list.Count, list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    static Reserves Zero() => new() { Food = 0, Funds = 0, Stone = 0, Timber = 0, Armaments = 0 };

    static bool Add(Reserves? r, uint item, long amount)
    {
        if (r is null || !Catalogue.Reserve.TryGetValue(item, out var name)) return false;
        switch (name)
        {
            case "funds": r.Funds += amount; break;
            case "food": r.Food += amount; break;
            case "timber": r.Timber += amount; break;
            case "stone": r.Stone += amount; break;
            case "armaments": r.Armaments += amount; break;
        }
        return true;
    }

    static IEnumerable<Dictionary<string, object?>> List(Dictionary<string, object?>? o, string field) =>
        o is not null && o.TryGetValue(field, out var v) && v is List<object?> l
            ? l.OfType<Dictionary<string, object?>>()
            : Enumerable.Empty<Dictionary<string, object?>>();

    static long? Long(Dictionary<string, object?> o, string field) =>
        o.TryGetValue(field, out var v)
            ? v switch
            {
                long l => l,
                int i => i,
                short s => s,
                sbyte b => b,
                ulong u when u <= long.MaxValue => (long)u,
                _ => null,
            }
            : null;
}
