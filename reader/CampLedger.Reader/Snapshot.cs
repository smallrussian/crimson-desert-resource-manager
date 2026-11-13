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
    /// <summary>
    /// Missions that can be started now: node discovered and not blockaded, every start condition in the game data
    /// met, not running. See docs/re-notes/available-missions.md. Entries with <c>unconfirmed</c> depend on
    /// something the source could not settle.
    /// </summary>
    public List<Mission> AvailableMissions { get; init; } = new();
    /// <summary>Dispatchable comrades (same rule as workers.total), with skill slots and current mission. Null if unread.</summary>
    public List<Comrade>? Comrades { get; init; }
    /// <summary>Camp level from the save (highest BaseCamp_LvN game-play variable set), or null.</summary>
    public int? CampLevel { get; init; }
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

    [JsonIgnore]
    public bool Any => Food.HasValue || Funds.HasValue || Stone.HasValue || Timber.HasValue || Armaments.HasValue;

    /// <summary>True when a value known here differs from <paramref name="o"/>. Unknown (null) values here are ignored.</summary>
    public bool KnownDiffers(Reserves o) =>
        (Food.HasValue && Food != o.Food) || (Funds.HasValue && Funds != o.Funds) || (Stone.HasValue && Stone != o.Stone)
        || (Timber.HasValue && Timber != o.Timber) || (Armaments.HasValue && Armaments != o.Armaments);
}

public sealed class Workers
{
    public int? Total { get; set; }
    public int? InUse { get; set; }
}

public sealed class Mission
{
    /// <summary>What the sheet calls "Mission / Location": the English location, else the mission type.</summary>
    public string? Name { get; set; }
    /// <summary>English mission type as the game shows it, e.g. "Hasty Mining".</summary>
    public string? Type { get; set; }
    /// <summary>English location as the game shows it, e.g. "Southern Quarry".</summary>
    public string? Location { get; set; }
    public string? Region { get; set; }
    /// <summary>Game's internal node name without the Node_ prefix, e.g. Her_SouthQuarry_Work_Stone_Tier2.</summary>
    public string? InternalName { get; set; }
    /// <summary>Game's internal category, e.g. Mining.</summary>
    public string? Category { get; set; }
    public bool Active { get; set; }
    public int? Workers { get; set; }
    /// <summary>"Members Required" range from the game's mission data.</summary>
    public int? WorkersMin { get; set; }
    public int? WorkersMax { get; set; }
    /// <summary>Which reserve the mission mainly produces (game's operation group).</summary>
    public string? Role { get; set; }
    public double? DurationHours { get; set; }
    /// <summary>Reserve cost per cycle. Items that are not reserves are in <see cref="ItemCost"/>.</summary>
    public Reserves? Cost { get; set; }
    /// <summary>Non-reserve items the mission also costs, e.g. "QuartzOre x30; Malachite x20". Null when none.</summary>
    public string? ItemCost { get; set; }
    /// <summary>Base reward per cycle, before conversion, skill and comrade bonuses.</summary>
    public Reserves? Reward { get; set; }
    /// <summary>Reward bonus inputs. The calc core turns these into a total with the user's camp level.</summary>
    public MissionBonus? Bonus { get; set; }
    public string? PersonalStorageReward { get; set; }
    /// <summary>Available missions only: what was assumed to list it, e.g. wanted level below 4.</summary>
    public List<string>? Assumptions { get; set; }
    /// <summary>Available missions only: start conditions the source could not settle. Null when all were checked.</summary>
    public List<string>? Unconfirmed { get; set; }

    // Identifiers and state read from the save. Names, costs and rewards need the game's static tables.
    public long? NodeKey { get; set; }
    public long? OperationKey { get; set; }
    public double? ElapsedHours { get; set; }
    public bool? Repeat { get; set; }
}

/// <summary>
/// What the game adds to a mission's base reward (docs/re-notes/comrade-bonuses.md). The three parts add:
/// conversion + best comrade's unlocked skill total + 20% per comrade above the minimum (max 40%).
/// </summary>
public sealed class MissionBonus
{
    /// <summary>Church blessing level of the mission's region x 2. Null when the region has no known church.</summary>
    public int? ConversionPercent { get; set; }
    /// <summary>Skill type the mission's rewards respond to, e.g. "Farming". Null if unknown.</summary>
    public string? SkillType { get; set; }
    /// <summary>Assigned comrades and their skill slots. Null when any could not be read.</summary>
    public List<AssignedComrade>? Comrades { get; set; }
}

public sealed class AssignedComrade
{
    public string? Name { get; set; }
    public long? CharacterKey { get; set; }
    /// <summary>Skill per slot, slot 1 first; null for a slot without a reward skill. Unlocked slots = camp level.</summary>
    public List<SlotSkill?> Slots { get; set; } = new();
}

public sealed record SlotSkill(string Type, int Percent);

/// <summary>A camp skill in a comrade's slot, e.g. Farming tier 2 = +30%. Percent 0 for gate skills (e.g. Building).</summary>
public sealed record ComradeSkill(string Type, int Tier, int Percent);

public sealed class Comrade
{
    public string? Name { get; set; }
    public long? CharacterKey { get; set; }
    public long? MercenaryNo { get; set; }
    /// <summary>Skill per slot, slot 1 first; null entry = no camp skill there. Null list = slots not read.</summary>
    public List<ComradeSkill?>? Slots { get; set; }
    /// <summary>The running mission this comrade is on, or null when free.</summary>
    public string? MissionName { get; set; }
    public long? OperationKey { get; set; }
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
