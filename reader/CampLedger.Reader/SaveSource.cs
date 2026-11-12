namespace CampLedger.Reader;

/// <summary>
/// Tier 1: reads camp state from the newest save. The save is copied to a temp file first and only the copy
/// is opened. Field locations: docs/re-notes/save-contents.md.
/// </summary>
public sealed class SaveSource : ISource
{
    readonly string _saveRoot;
    readonly string? _gameVersion;
    public string Name => "save";

    public SaveSource(string? saveRoot = null, string? gameVersion = null)
    {
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
            "mission names, costs, rewards and durations: need the game's static tables (not read yet)",
            "workers.total: needs character names from the game's static tables (not read yet)",
        };

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
            missions.Add(new Mission
            {
                Active = true,
                Workers = workers,
                NodeKey = Long(node, "_ownerFactionKey"),
                OperationKey = Long(node, "_operationKey"),
                // _operationCurrentProgress is elapsed in-game hours x 10 (docs/re-notes/mission-static-data.md).
                ElapsedHours = progress is null ? null : Math.Round(progress.Value / 10.0, 2),
                Repeat = node.TryGetValue("_operationShouldRepeat", out var r) && r is bool b ? b : null,
            });
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
            Workers = new Workers { Total = null, InUse = faction is null ? null : inUse },
            Missions = missions,
            InGameClock = clock,
            Unknowns = unknowns,
        };
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
