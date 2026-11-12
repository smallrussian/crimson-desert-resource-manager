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
            "mission durations: not found in game data yet",
            "mission rewards are base amounts; conversion, skill and comrade bonuses are not applied",
        };
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
            missions.Add(mission);
        }

        ulong? clock = null;
        foreach (var f in List(body.Decode("TransformSaveData"), "_fieldSaveDataList"))
            if (f.TryGetValue("_fieldTimeRaw", out var t) && t is ulong u) clock = u;
        if (clock is null) unknowns.Add("in-game clock not present in the save");

        return new Snapshot
        {
            GameVersion = _gameVersion,
            Source = "save",
            Reserves = reserves,
            Workers = new Workers { Total = TotalWorkers(body, unknowns), InUse = faction is null ? null : inUse },
            Missions = missions,
            InGameClock = clock,
            Unknowns = unknowns,
        };
    }

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
        m.Name = op.InternalName?.StartsWith("Node_") == true ? op.InternalName[5..] : op.InternalName;
        m.Type = op.Category;
        m.Role = op.Role;
        m.Region = Catalogue.RegionOf(op.InternalName);
        m.WorkersMin = op.MembersMin;
        m.WorkersMax = op.MembersMax;

        Reserves? cost = Zero();
        foreach (var (item, amount) in op.Cost)
            if (!Add(cost, item, amount))
            {
                unknowns.Add($"{m.Name}: cost includes non-reserve item {item}");
                cost = null;
                break;
            }
        m.Cost = cost;

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
                bool certain = it.Chance >= 1.0 && it.Min == it.Max;
                if (certain && Catalogue.Reserve.ContainsKey(it.Item)) Add(reward, it.Item, it.Min);
                else
                {
                    string name = _catalogue.ItemName(it.Item) ?? it.Item.ToString();
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
        m.PersonalStorageReward = reward is null || extras.Count == 0 ? null : string.Join("; ", extras);
    }

    /// <summary>
    /// Dispatchable comrades: human comrades (character name NHM_/NHW_/NDM_/NOM_) who have a camp spawn position,
    /// except Marius. Matched the game's "Comrades Found: 37" name for name on 2026-11-11 (one save).
    /// </summary>
    int? TotalWorkers(SaveBody body, List<string> unknowns)
    {
        if (_catalogue is null) return null;
        var clan = body.Decode("MercenaryClanSaveData");
        if (clan is null)
        {
            unknowns.Add("MercenaryClanSaveData missing: workers.total unknown");
            return null;
        }
        int total = 0;
        foreach (var merc in List(clan, "_mercenaryDataList"))
        {
            if (Long(merc, "_characterKey") is not long ck) continue;
            string? name = _catalogue.CharacterName((uint)ck);
            if (name is null)
            {
                unknowns.Add($"character {ck} not in game data: workers.total unknown");
                return null;
            }
            bool human = name.StartsWith("NHM_") || name.StartsWith("NHW_") || name.StartsWith("NDM_") || name.StartsWith("NOM_");
            if (human && merc.ContainsKey("_spawnPosition") && name != NeverDispatchable) total++;
        }
        return total;
    }

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
