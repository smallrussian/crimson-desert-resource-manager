namespace CampLedger.Core;

/// <summary>One unlockable skill slot's reward skill. Percent 0 for a gate skill ("allows ... missions").</summary>
public sealed record SlotSkill(string Type, int Percent);

/// <summary>A comrade assigned to a mission, with skill slots in order (slot 1 first; null = no reward skill).</summary>
public sealed record ComradeSlots(string? Name, IReadOnlyList<SlotSkill?> Slots);

/// <summary>What the game adds to a mission's base reward, as read by the reader. Null means unknown.</summary>
public sealed record BonusInputs(
    int? ConversionPercent,
    string? SkillType,
    IReadOnlyList<ComradeSlots>? Comrades,
    int Workers,
    int? WorkersMin);

public sealed record BonusBreakdown(int? Conversion, int? Skill, int? Comrade, int? Total);

/// <summary>
/// Mission reward bonuses (docs/re-notes/comrade-bonuses.md). The three parts add, they do not multiply
/// (owner's screenshot: 1,100 base, +40% and +40% shown as 1.9K, i.e. 1,980):
/// conversion (church level x 2 in the mission's region) + skill (the best assigned comrade's total of
/// unlocked slots matching the mission's skill type; a comrade's first camp-level slots are unlocked) +
/// comrade (20% per comrade above the minimum, at most 40%).
/// </summary>
public static class Bonus
{
    public const int ComradePercentEach = 20;
    public const int ComradePercentMax = 40;

    /// <summary>Best single comrade's matching skill total over unlocked slots. Null if any input is unknown.</summary>
    public static int? SkillPercent(string? skillType, IReadOnlyList<ComradeSlots>? comrades, int? campLevel)
    {
        if (skillType is null || comrades is null || campLevel is not int level || level < 0) return null;
        int best = 0;
        foreach (var c in comrades)
        {
            int total = c.Slots.Take(level).Where(s => s is not null && s.Type == skillType).Sum(s => s!.Percent);
            best = Math.Max(best, total);
        }
        return best;
    }

    /// <summary>20% per comrade above the minimum, capped at 40%. Null if the minimum is unknown or not met.</summary>
    public static int? ComradePercent(int workers, int? workersMin) =>
        workersMin is int min && workers >= min
            ? Math.Min(ComradePercentMax, ComradePercentEach * (workers - min))
            : null;

    public static BonusBreakdown Of(BonusInputs b, int? campLevel)
    {
        int? skill = SkillPercent(b.SkillType, b.Comrades, campLevel);
        int? comrade = ComradePercent(b.Workers, b.WorkersMin);
        int? total = b.ConversionPercent is int conv && skill is int s && comrade is int c ? conv + s + c : null;
        return new BonusBreakdown(b.ConversionPercent, skill, comrade, total);
    }

    /// <summary>
    /// Base reward raised by the total bonus. Rounded down to a whole number; the game's own rounding is not
    /// confirmed (every case seen so far divides exactly).
    /// </summary>
    public static ReserveSet<long>? Apply(ReserveSet<long>? baseReward, int? totalPercent) =>
        baseReward is null || totalPercent is not int p || p < 0
            ? null
            : baseReward.Map((_, v) => v * (100 + p) / 100);
}
