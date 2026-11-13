namespace CampLedger.Core;

/// <summary>Portfolio tab: totals over the active missions, per reserve.</summary>
public sealed record Portfolio(
    ReserveSet<long?> NetPerCycle,
    ReserveSet<Trend?> Status,
    ReserveSet<double?> NetPerHour,
    ReserveSet<double?> NetPerDay,
    ReserveSet<double?> HoursUntilEmpty,
    bool? OperatingBudgetOk,
    double HoursPerDay,
    IReadOnlyList<string> Unknowns);

public sealed record WorkerSummary(int? Total, int InUse, int? Free, WorkerState? State);

/// <summary>Upgrade Planner tab, per reserve.</summary>
public sealed record UpgradePlan(
    ReserveSet<long> UpgradeCost,
    ReserveSet<long?> OperatingCost,
    ReserveSet<long?> TotalNeeded,
    ReserveSet<long?> Shortfall,
    ReserveSet<Afford> HoursToAfford);

/// <summary>
/// The net-gain calculations from the owner's spreadsheet, as pure functions (no I/O). Formulas are the
/// sheet's, read 2026-11-12; see docs/architecture.md for the one deliberate difference (unknown duration).
/// Anything that depends on an unknown input is null, never a guess.
/// </summary>
public static class Calc
{
    public const double DefaultHoursPerDay = 24;

    /// <summary>Missions!P:T. Reward minus cost; null if either is unknown.</summary>
    public static ReserveSet<long>? Net(MissionLine m) =>
        m.Cost is null || m.Reward is null ? null : Amounts.Subtract(m.Reward, m.Cost);

    /// <summary>
    /// Missions!Z:AD. Net divided by duration. A missing or non-positive duration is unknown (the sheet shows 0
    /// there; AGENTS.md says unknown).
    /// </summary>
    public static ReserveSet<double>? NetPerHour(MissionLine m)
    {
        var net = Net(m);
        if (net is null || m.DurationHours is not double h || h <= 0 || double.IsNaN(h)) return null;
        return net.Map((_, v) => v / h);
    }

    /// <summary>Portfolio!C. Sum of net over active missions. Unknown for every reserve if any active net is.</summary>
    public static ReserveSet<long?> NetPerCycle(IEnumerable<MissionLine> missions)
    {
        var total = Amounts.Zero;
        foreach (var m in missions.Where(m => m.Active))
        {
            if (Net(m) is not { } net) return ReserveSet<long?>.From(_ => null);
            total = Amounts.Add(total, net);
        }
        return total.Map((_, v) => (long?)v);
    }

    /// <summary>
    /// Portfolio!F. Sum of per-hour net over active missions. A reserve is unknown when an active mission with
    /// no usable duration changes it (or its net is unknown); reserves that mission does not touch stay known.
    /// </summary>
    public static ReserveSet<double?> TotalNetPerHour(IEnumerable<MissionLine> missions)
    {
        var active = missions.Where(m => m.Active).ToList();
        return ReserveSet<double?>.From(r =>
        {
            double sum = 0;
            foreach (var m in active)
            {
                if (NetPerHour(m) is { } rate) sum += rate[r];
                else if (Net(m) is not { } net || net[r] != 0) return null;
            }
            return sum;
        });
    }

    /// <summary>Portfolio!G. Per-hour net times hours per day (a user setting, labelled as an assumption).</summary>
    public static ReserveSet<double?> NetPerDay(ReserveSet<double?> perHour, double hoursPerDay = DefaultHoursPerDay)
    {
        if (hoursPerDay <= 0 || double.IsNaN(hoursPerDay))
            throw new ArgumentOutOfRangeException(nameof(hoursPerDay), "hours per day must be positive");
        return perHour.Map((_, v) => v * hoursPerDay);
    }

    /// <summary>Portfolio!D.</summary>
    public static Trend? StatusOf(long? netPerCycle) => netPerCycle switch
    {
        null => null,
        > 0 => Trend.Positive,
        0 => Trend.BreakEven,
        _ => Trend.Negative,
    };

    /// <summary>
    /// Portfolio!H. Hours until the balance runs out at the current per-hour net. Null means sustainable
    /// (net per hour is zero or positive) or unknown; use <see cref="Sustainable"/> to tell them apart.
    /// </summary>
    public static double? HoursUntilEmpty(long? balance, double? netPerHour) =>
        balance is long b && netPerHour is double n && n < 0 ? b / Math.Abs(n) : null;

    /// <summary>True when net per hour is zero or positive, false when negative, null when unknown.</summary>
    public static bool? Sustainable(double? netPerHour) => netPerHour is double n ? n >= 0 : null;

    /// <summary>Portfolio!B8. Food and funds are the operating budget: both per-cycle nets must be at least 0.</summary>
    public static bool? OperatingBudgetOk(ReserveSet<long?> netPerCycle) =>
        netPerCycle.Food is long f && netPerCycle.Funds is long u ? f >= 0 && u >= 0 : null;

    /// <summary>Portfolio!B10:C12.</summary>
    public static WorkerSummary Workers(int? total, IEnumerable<MissionLine> missions)
    {
        int inUse = missions.Where(m => m.Active).Sum(m => m.Workers);
        int? free = total - inUse;
        WorkerState? state = free switch
        {
            null => null,
            < 0 => WorkerState.OverAssigned,
            0 => WorkerState.FullyUsed,
            _ => WorkerState.Spare,
        };
        return new WorkerSummary(total, inUse, free, state);
    }

    public static Portfolio PortfolioOf(IReadOnlyCollection<MissionLine> missions, ReserveSet<long?> balance,
                                        double hoursPerDay = DefaultHoursPerDay)
    {
        var perCycle = NetPerCycle(missions);
        var perHour = TotalNetPerHour(missions);
        var unknowns = new List<string>();
        foreach (var m in missions.Where(m => m.Active))
        {
            if (Net(m) is null) unknowns.Add($"{m.Name}: cost or reward unknown");
            else if (NetPerHour(m) is null) unknowns.Add($"{m.Name}: duration unknown");
        }
        return new Portfolio(
            perCycle,
            perCycle.Map((_, v) => StatusOf(v)),
            perHour,
            NetPerDay(perHour, hoursPerDay),
            perHour.Map((r, v) => HoursUntilEmpty(balance[r], v)),
            OperatingBudgetOk(perCycle),
            hoursPerDay,
            unknowns);
    }

    /// <summary>Upgrade Planner!C. One cycle of the active missions' costs. Unknown if any active cost is.</summary>
    public static ReserveSet<long?> OperatingCost(IEnumerable<MissionLine> missions)
    {
        var total = Amounts.Zero;
        foreach (var m in missions.Where(m => m.Active))
        {
            if (m.Cost is null) return ReserveSet<long?>.From(_ => null);
            total = Amounts.Add(total, m.Cost);
        }
        return total.Map((_, v) => (long?)v);
    }

    /// <summary>
    /// Upgrade Planner tab. Total needed = upgrade cost + one operating cycle; shortfall = max(0, total - balance);
    /// hours to afford = shortfall / net per hour, Ready when there is no shortfall, NotAtCurrentNet when net per
    /// hour is zero or negative.
    /// </summary>
    public static UpgradePlan PlanUpgrade(ReserveSet<long> upgradeCost, IReadOnlyCollection<MissionLine> missions,
                                          ReserveSet<long?> balance)
    {
        var operating = OperatingCost(missions);
        var perHour = TotalNetPerHour(missions);
        var total = operating.Map((r, v) => v + upgradeCost[r]);
        var shortfall = total.Map((r, v) => v is long t && balance[r] is long b ? Math.Max(0, t - b) : (long?)null);
        var afford = shortfall.Map((r, s) => HoursToAfford(s, perHour[r]));
        return new UpgradePlan(upgradeCost, operating, total, shortfall, afford);
    }

    public static Afford HoursToAfford(long? shortfall, double? netPerHour) => (shortfall, netPerHour) switch
    {
        (null, _) => Afford.Unknown,
        (0, _) => Afford.Ready,
        (_, null) => Afford.Unknown,
        (_, <= 0) => Afford.NotAtCurrentNet,
        (long s, double n) => new Afford(AffordKind.Hours, s / n),
    };
}
