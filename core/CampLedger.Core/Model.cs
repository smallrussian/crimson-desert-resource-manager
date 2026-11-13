namespace CampLedger.Core;

public enum Reserve { Food, Funds, Stone, Timber, Armaments }

/// <summary>One value per camp reserve. Used for amounts (long), balances (long?) and rates (double?).</summary>
public sealed record ReserveSet<T>(T Food, T Funds, T Stone, T Timber, T Armaments)
{
    public static readonly IReadOnlyList<Reserve> All =
        new[] { Reserve.Food, Reserve.Funds, Reserve.Stone, Reserve.Timber, Reserve.Armaments };

    public T this[Reserve r] => r switch
    {
        Reserve.Food => Food,
        Reserve.Funds => Funds,
        Reserve.Stone => Stone,
        Reserve.Timber => Timber,
        Reserve.Armaments => Armaments,
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    public static ReserveSet<T> From(Func<Reserve, T> f) =>
        new(f(Reserve.Food), f(Reserve.Funds), f(Reserve.Stone), f(Reserve.Timber), f(Reserve.Armaments));

    public ReserveSet<TOut> Map<TOut>(Func<Reserve, T, TOut> f) => ReserveSet<TOut>.From(r => f(r, this[r]));
}

/// <summary>Whole-number reserve amounts, as the game shows them.</summary>
public static class Amounts
{
    public static readonly ReserveSet<long> Zero = new(0, 0, 0, 0, 0);

    public static ReserveSet<long> Add(ReserveSet<long> a, ReserveSet<long> b) => a.Map((r, v) => v + b[r]);
    public static ReserveSet<long> Subtract(ReserveSet<long> a, ReserveSet<long> b) => a.Map((r, v) => v - b[r]);
}

/// <summary>
/// One mission row, like a row of the sheet's Missions tab. Null means unknown: a null cost, reward or
/// duration is never treated as zero.
/// </summary>
public sealed record MissionLine(
    string Name,
    bool Active,
    int Workers,
    double? DurationHours,
    ReserveSet<long>? Cost,
    ReserveSet<long>? Reward);

public enum Trend { Positive, BreakEven, Negative }

public enum WorkerState { Spare, FullyUsed, OverAssigned }

public enum AffordKind { Ready, Hours, NotAtCurrentNet, Unknown }

/// <summary>Upgrade planner answer for one reserve. <see cref="Hours"/> is set only when Kind is Hours.</summary>
public sealed record Afford(AffordKind Kind, double? Hours = null)
{
    public static readonly Afford Ready = new(AffordKind.Ready);
    public static readonly Afford NotAtCurrentNet = new(AffordKind.NotAtCurrentNet);
    public static readonly Afford Unknown = new(AffordKind.Unknown);
}
