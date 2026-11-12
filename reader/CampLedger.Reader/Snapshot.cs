using System.Text.Json.Serialization;

namespace CampLedger.Reader;

/// <summary>One reading of camp state. A field a source could not read is null, never a guessed default.</summary>
public sealed class Snapshot
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? GameVersion { get; init; }
    public string Source { get; init; } = "save";
    public Reserves Reserves { get; init; } = new();
    public Workers Workers { get; init; } = new();
    public List<Mission> Missions { get; init; } = new();
    /// <summary>In-game clock in milliseconds (save field _fieldTimeRaw), or null.</summary>
    public ulong? InGameClock { get; init; }
    /// <summary>Things this snapshot could not establish, in plain words.</summary>
    public List<string> Unknowns { get; init; } = new();
}

public sealed class Reserves
{
    public long? Food { get; set; }
    public long? Funds { get; set; }
    public long? Stone { get; set; }
    public long? Timber { get; set; }
    public long? Armaments { get; set; }

    [JsonIgnore]
    public bool Complete => Food.HasValue && Funds.HasValue && Stone.HasValue && Timber.HasValue && Armaments.HasValue;

    public bool SameAs(Reserves o) =>
        Food == o.Food && Funds == o.Funds && Stone == o.Stone && Timber == o.Timber && Armaments == o.Armaments;
}

public sealed class Workers
{
    public int? Total { get; set; }
    public int? InUse { get; set; }
}

public sealed class Mission
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Region { get; set; }
    public bool Active { get; set; }
    public int? Workers { get; set; }
    /// <summary>"Members Required" range from the game's mission data.</summary>
    public int? WorkersMin { get; set; }
    public int? WorkersMax { get; set; }
    /// <summary>Which reserve the mission mainly produces (game's operation group).</summary>
    public string? Role { get; set; }
    public double? DurationHours { get; set; }
    public Reserves? Cost { get; set; }
    /// <summary>Base reward per cycle, before conversion, skill and comrade bonuses.</summary>
    public Reserves? Reward { get; set; }
    public string? PersonalStorageReward { get; set; }

    // Identifiers and state read from the save. Names, costs and rewards need the game's static tables.
    public long? NodeKey { get; set; }
    public long? OperationKey { get; set; }
    public double? ElapsedHours { get; set; }
    public bool? Repeat { get; set; }
}

/// <summary>A source either returns a snapshot or says why it cannot.</summary>
public abstract record SourceResult
{
    public sealed record Ok(Snapshot Snapshot) : SourceResult;
    public sealed record Unavailable(string Reason) : SourceResult;
}

public interface ISource
{
    string Name { get; }
    SourceResult Read();
}
