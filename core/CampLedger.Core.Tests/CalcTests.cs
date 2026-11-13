using CampLedger.Core;
using Xunit;

namespace CampLedger.Core.Tests;

/// <summary>
/// Expected values are the owner's Google Sheet as read on 2026-11-12 (Missions rows 2-7, Portfolio, Upgrade
/// Planner). The sheet shows one decimal for rates and whole numbers per day, so those are compared at that
/// precision. The mission figures are the sheet's own entries (row 2 is the sheet's example row).
/// </summary>
public class CalcTests
{
    static ReserveSet<long> R(long food = 0, long funds = 0, long stone = 0, long timber = 0, long armaments = 0) =>
        new(food, funds, stone, timber, armaments);

    static readonly ReserveSet<long?> NoBalance = new(0, 0, 0, 0, 0);   // the sheet's balance cells are blank

    static MissionLine Nas => new("Nas river Fishing Dock", true, 2, 16, R(funds: 600), R(food: 1300));
    static MissionLine Hillside => new("Hillside Manor", true, 2, 17, R(food: 100), R(funds: 3200));
    static MissionLine Oakenshield => new("Oakenshield Manor", true, 2, 18, R(food: 120), R(funds: 3400));
    static MissionLine SouthernQuarry => new("Southern Quarry", true, 5, 12, R(food: 140, funds: 1300), R(stone: 2500));
    static MissionLine Glenbright => new("Glenbright Farm", true, 2, 16, R(funds: 900), R(food: 1900));
    /// <summary>Duration is blank in the sheet.</summary>
    static MissionLine Calphade => new("Calphade Logging Camp", true, 5, null, R(food: 200, funds: 2000), R(timber: 2000));

    static List<MissionLine> Sheet() => new() { Nas, Hillside, Oakenshield, SouthernQuarry, Glenbright, Calphade };

    [Fact]
    public void Mission_net_and_per_hour_match_the_sheet()
    {
        Assert.Equal(R(food: 1300, funds: -600), Calc.Net(Nas));
        Assert.Equal(R(food: -140, funds: -1300, stone: 2500), Calc.Net(SouthernQuarry));

        var nas = Calc.NetPerHour(Nas)!;
        Assert.Equal(81.25, nas.Food);      // sheet shows 81.3
        Assert.Equal(-37.5, nas.Funds, 1);
        var hill = Calc.NetPerHour(Hillside)!;
        Assert.Equal(-5.9, hill.Food, 1);
        Assert.Equal(188.2, hill.Funds, 1);
        Assert.Equal(188.9, Calc.NetPerHour(Oakenshield)!.Funds, 1);
        Assert.Equal(208.3, Calc.NetPerHour(SouthernQuarry)!.Stone, 1);
        Assert.Equal(-56.25, Calc.NetPerHour(Glenbright)!.Funds);   // sheet shows -56.3
    }

    [Fact]
    public void Missing_duration_is_unknown_not_zero()
    {
        Assert.Equal(R(food: -200, funds: -2000, timber: 2000), Calc.Net(Calphade));
        Assert.Null(Calc.NetPerHour(Calphade));
        Assert.Null(Calc.NetPerHour(Nas with { DurationHours = 0 }));
        Assert.Null(Calc.NetPerHour(Nas with { DurationHours = -3 }));
    }

    [Fact]
    public void Net_per_cycle_matches_the_sheet()
    {
        Assert.Equal(new ReserveSet<long?>(2640, 1800, 2500, 2000, 0), Calc.NetPerCycle(Sheet()));
    }

    [Fact]
    public void Inactive_missions_are_not_counted()
    {
        var missions = Sheet().Select(m => m with { Active = false }).ToList();
        Assert.Equal(new ReserveSet<long?>(0, 0, 0, 0, 0), Calc.NetPerCycle(missions));
        Assert.Equal(new ReserveSet<double?>(0, 0, 0, 0, 0), Calc.TotalNetPerHour(missions));
    }

    [Fact]
    public void Net_per_hour_and_per_day_match_the_sheet_when_every_counted_mission_has_a_duration()
    {
        // The sheet counts Calphade as 0 per hour because its duration is blank, so the sheet's totals equal the
        // totals without Calphade.
        var missions = Sheet().Select(m => m.Name == Calphade.Name ? m with { Active = false } : m).ToList();
        var perHour = Calc.TotalNetPerHour(missions);
        Assert.Equal(175.8, perHour.Food!.Value, 1);
        Assert.Equal(175.0, perHour.Funds!.Value, 1);
        Assert.Equal(208.3, perHour.Stone!.Value, 1);
        Assert.Equal(0, perHour.Timber);
        Assert.Equal(0, perHour.Armaments);

        var perDay = Calc.NetPerDay(perHour);
        Assert.Equal(4219, Math.Round(perDay.Food!.Value));
        Assert.Equal(4201, Math.Round(perDay.Funds!.Value));
        Assert.Equal(5000, Math.Round(perDay.Stone!.Value));
    }

    [Fact]
    public void Active_mission_without_duration_makes_only_the_reserves_it_changes_unknown()
    {
        var perHour = Calc.TotalNetPerHour(Sheet());
        Assert.Null(perHour.Food);
        Assert.Null(perHour.Funds);
        Assert.Null(perHour.Timber);
        Assert.Equal(208.3, perHour.Stone!.Value, 1);   // Calphade does not touch stone
        Assert.Equal(0, perHour.Armaments);

        var p = Calc.PortfolioOf(Sheet(), NoBalance);
        Assert.Contains("Calphade Logging Camp: duration unknown", p.Unknowns);
        Assert.Null(p.NetPerDay.Food);
    }

    [Fact]
    public void Net_per_day_uses_the_hours_per_day_setting()
    {
        var perHour = new ReserveSet<double?>(10, -2, null, 0, 1);
        Assert.Equal(new ReserveSet<double?>(200, -40, null, 0, 20), Calc.NetPerDay(perHour, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => Calc.NetPerDay(perHour, 0));
    }

    [Fact]
    public void Status_and_operating_budget_match_the_sheet()
    {
        var p = Calc.PortfolioOf(Sheet(), NoBalance);
        Assert.Equal(new ReserveSet<Trend?>(Trend.Positive, Trend.Positive, Trend.Positive, Trend.Positive, Trend.BreakEven),
                     p.Status);
        Assert.True(p.OperatingBudgetOk);
        Assert.Equal(24, p.HoursPerDay);

        Assert.Equal(Trend.Negative, Calc.StatusOf(-1));
        Assert.False(Calc.OperatingBudgetOk(new ReserveSet<long?>(5, -1, 0, 0, 0)));
        Assert.Null(Calc.OperatingBudgetOk(new ReserveSet<long?>(5, null, 0, 0, 0)));
    }

    [Fact]
    public void Hours_until_empty_only_when_losing()
    {
        // Sheet: every reserve is "Sustainable" because none has a negative per-hour net.
        Assert.Null(Calc.HoursUntilEmpty(0, 175.8));
        Assert.True(Calc.Sustainable(0));
        Assert.Equal(50, Calc.HoursUntilEmpty(1000, -20));
        Assert.False(Calc.Sustainable(-20));
        Assert.Null(Calc.HoursUntilEmpty(null, -20));
        Assert.Null(Calc.Sustainable(null));
    }

    [Fact]
    public void Workers_match_the_sheet()
    {
        Assert.Equal(new WorkerSummary(37, 18, 19, WorkerState.Spare), Calc.Workers(37, Sheet()));
        Assert.Equal(WorkerState.FullyUsed, Calc.Workers(18, Sheet()).State);
        Assert.Equal(WorkerState.OverAssigned, Calc.Workers(10, Sheet()).State);
        Assert.Equal(new WorkerSummary(null, 18, null, null), Calc.Workers(null, Sheet()));
    }

    [Fact]
    public void Upgrade_planner_totals_match_the_sheet()
    {
        var plan = Calc.PlanUpgrade(Amounts.Zero, Sheet(), NoBalance);
        Assert.Equal(new ReserveSet<long?>(560, 4800, 0, 0, 0), plan.OperatingCost);
        Assert.Equal(new ReserveSet<long?>(560, 4800, 0, 0, 0), plan.TotalNeeded);
        Assert.Equal(new ReserveSet<long?>(560, 4800, 0, 0, 0), plan.Shortfall);
        Assert.Equal(Afford.Ready, plan.HoursToAfford.Stone);
        Assert.Equal(Afford.Ready, plan.HoursToAfford.Timber);
        Assert.Equal(Afford.Ready, plan.HoursToAfford.Armaments);
        // The sheet shows 3.2 h and 27.4 h here by counting Calphade as 0 per hour; with its duration unknown,
        // food and funds per hour are unknown, so the time to afford is too.
        Assert.Equal(Afford.Unknown, plan.HoursToAfford.Food);
        Assert.Equal(Afford.Unknown, plan.HoursToAfford.Funds);
    }

    [Fact]
    public void Hours_to_afford_matches_the_sheet_given_its_per_hour_net()
    {
        // Upgrade Planner!G2 and G3: shortfall over the sheet's per-hour net (Portfolio!F2, F3).
        double food = 1300 / 16.0 - 100 / 17.0 - 120 / 18.0 - 140 / 12.0 + 1900 / 16.0;
        double funds = -600 / 16.0 + 3200 / 17.0 + 3400 / 18.0 - 1300 / 12.0 - 900 / 16.0;
        Assert.Equal(3.2, Calc.HoursToAfford(560, food).Hours!.Value, 1);
        Assert.Equal(27.4, Calc.HoursToAfford(4800, funds).Hours!.Value, 1);
        Assert.Equal(AffordKind.Hours, Calc.HoursToAfford(560, food).Kind);
    }

    [Fact]
    public void Upgrade_planner_uses_the_balance_and_upgrade_cost()
    {
        var missions = new List<MissionLine> { SouthernQuarry };
        var plan = Calc.PlanUpgrade(R(stone: 10_000, timber: 500), missions, new ReserveSet<long?>(1000, 5000, 2000, 900, 0));
        Assert.Equal(new ReserveSet<long?>(140, 1300, 10_000, 500, 0), plan.TotalNeeded);
        Assert.Equal(new ReserveSet<long?>(0, 0, 8000, 0, 0), plan.Shortfall);
        Assert.Equal(Afford.Ready, plan.HoursToAfford.Food);
        Assert.Equal(new Afford(AffordKind.Hours, 8000 / (2500 / 12.0)), plan.HoursToAfford.Stone);

        var losing = Calc.PlanUpgrade(R(food: 1000), missions, new ReserveSet<long?>(0, 0, 0, 0, 0));
        Assert.Equal(Afford.NotAtCurrentNet, losing.HoursToAfford.Food);    // quarry eats food
        var unknownBalance = Calc.PlanUpgrade(R(food: 1000), missions, new ReserveSet<long?>(null, 0, 0, 0, 0));
        Assert.Null(unknownBalance.Shortfall.Food);
        Assert.Equal(Afford.Unknown, unknownBalance.HoursToAfford.Food);
    }

    [Fact]
    public void Unknown_cost_or_reward_fails_closed()
    {
        var missions = new List<MissionLine> { Nas, Hillside with { Reward = null } };
        Assert.Equal(new ReserveSet<long?>(null, null, null, null, null), Calc.NetPerCycle(missions));
        Assert.All(ReserveSet<double?>.All, r => Assert.Null(Calc.TotalNetPerHour(missions)[r]));
        Assert.Equal(new ReserveSet<long?>(100, 600, 0, 0, 0), Calc.OperatingCost(missions));   // costs still known
        Assert.Null(Calc.OperatingCost(new[] { Nas with { Cost = null } }).Food);
        Assert.Contains("Hillside Manor: cost or reward unknown", Calc.PortfolioOf(missions, NoBalance).Unknowns);
    }
}
