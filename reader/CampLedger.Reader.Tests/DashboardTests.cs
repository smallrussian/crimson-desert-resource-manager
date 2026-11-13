using System.Text;
using System.Text.Json;
using CampLedger.Core;
using CampLedger.Reader;
using Xunit;

namespace CampLedger.Reader.Tests;

/// <summary>
/// Dashboard view model and snapshot log, on hand-made snapshots shaped like the owner's 2026-11-12 save
/// (Glenbright Farm: 1,100 Food base, +40% conversion, Evelyn and Terry, camp level 4 -> 1,980).
/// </summary>
public class DashboardTests
{
    static Reserves R(long food = 0, long funds = 0, long stone = 0, long timber = 0, long arm = 0) =>
        new() { Food = food, Funds = funds, Stone = stone, Timber = timber, Armaments = arm };

    static AssignedComrade Comrade(string name, params (string Type, int Pct)?[] slots) =>
        new() { Name = name, Slots = slots.Select(s => s is null ? null : new CampLedger.Reader.SlotSkill(s.Value.Type, s.Value.Pct)).ToList() };

    static Mission Glenbright() => new()
    {
        Name = "Glenbright Farm", Type = "Simple Farming", Location = "Glenbright Farm", Region = "Hernand", Active = true,
        Workers = 2, WorkersMin = 2, WorkersMax = 4, DurationHours = 16, ElapsedHours = 4, Repeat = true,
        Cost = R(funds: 900), Reward = R(food: 1100),
        Bonus = new MissionBonus
        {
            ConversionPercent = 40, SkillType = "Farming",
            Comrades = new()
            {
                Comrade("Evelyn", ("Farming", 10), ("Foodprocessing", 10), ("Farming", 30), ("Foodprocessing", 30), ("Farming", 60)),
                Comrade("Terry", ("Farming", 10), ("Farming", 30), ("Crafting", 10), ("Foodprocessing", 10), ("Engineering", 10)),
            },
        },
    };

    static Mission Pond() => new()
    {
        Name = "Howling Hill Camp Pond Construction", Active = true, Workers = 8, WorkersMin = 8, DurationHours = 48,
        ElapsedHours = 6, Repeat = null, Cost = R(food: 1850, funds: 20000, stone: 850, timber: 500), Reward = R(),
        Bonus = new MissionBonus { ConversionPercent = null, SkillType = null, Comrades = new() },
    };

    static Snapshot Save(DateTimeOffset at, params Mission[] missions) => new()
    {
        CapturedAt = at, Source = "save", CampLevel = 4, Reserves = R(9274, 50456, 65831, 2515, 3207),
        Workers = new Workers { Total = 37, InUse = missions.Sum(m => m.Workers ?? 0) }, Missions = missions.ToList(),
        AvailableMissions = new()
        {
            new Mission { Name = "Pollengarth Apiary", Region = "Hernand", Role = "Food", WorkersMin = 5, WorkersMax = 7, DurationHours = 19,
                          Cost = R(funds: 3500), Reward = R(food: 3500), Bonus = new MissionBonus { ConversionPercent = 40 } },
        },
    };

    static readonly DateTimeOffset T0 = new(2026, 11, 12, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Running_mission_reward_includes_the_bonus()
    {
        var r = DashboardView.RunningOf(Glenbright(), campLevel: 4);
        Assert.Equal(80, r.Bonus!.Total);
        Assert.Equal(1980, r.Reward!.Food);
        Assert.Equal(1100, r.BaseReward!.Food);
        Assert.Equal((1980 - 0) / 16.0, r.NetPerHour!["food"], 6);
        Assert.Equal(-900 / 16.0, r.NetPerHour!["funds"], 6);
        Assert.Equal(12, r.RemainingHours);
        Assert.Equal(0.25, r.Progress);
    }

    [Fact]
    public void Unknown_bonus_makes_the_reward_unknown_unless_the_base_is_zero()
    {
        var g = Glenbright();
        g.Bonus!.ConversionPercent = null;
        Assert.Null(DashboardView.RunningOf(g, 4).Reward);
        var pond = DashboardView.RunningOf(Pond(), 4);
        Assert.NotNull(pond.Reward);
        Assert.Equal(0, pond.Reward!.Food);
        Assert.False(pond.Recurring);
    }

    [Fact]
    public void One_off_builds_stay_out_of_totals_but_count_as_busy_comrades()
    {
        var state = new SnapshotLog.State();
        SnapshotLog.Add(state, Save(T0, Glenbright(), Pond()));
        var v = DashboardView.Build(state, 24, gameRunning: true);
        var food = v.Reserves.Single(r => r.Key == "food");
        Assert.Equal(1980, food.PerCycle);
        Assert.Equal(1980 / 16.0 * 24, food.PerDay!.Value, 6);
        Assert.Equal(-900, v.Reserves.Single(r => r.Key == "funds").PerCycle);
        Assert.Equal(10, v.Workers.InUse);
        Assert.Equal(27, v.Workers.Free);
        Assert.Equal(4, v.Status.CampLevel);
        Assert.Equal(2, v.Running.Count);
    }

    [Fact]
    public void Newer_live_reserves_replace_the_save_balance()
    {
        var state = new SnapshotLog.State();
        SnapshotLog.Add(state, Save(T0, Glenbright()));
        SnapshotLog.Add(state, new Snapshot { CapturedAt = T0.AddMinutes(5), Source = "process", Reserves = R(1, 2, 3, 4, 5) });
        var v = DashboardView.Build(state, 24, true);
        Assert.Equal(2, v.Reserves.Single(r => r.Key == "funds").Balance);
        Assert.Equal("process", v.Status.ReservesSource);
        Assert.Single(v.Running);       // missions still from the save
        Assert.Equal(2, v.History.Count);
    }

    [Fact]
    public void Available_mission_minimum_uses_conversion_only()
    {
        var state = new SnapshotLog.State();
        SnapshotLog.Add(state, Save(T0));
        var a = DashboardView.Build(state, 24, false).Available.Single();
        Assert.Equal(4900, a.MinReward!.Food);
        Assert.Equal((4900.0) / 19, a.MinNetPerHour!["food"], 6);
    }

    [Fact]
    public void Available_build_with_no_reserve_reward_has_a_known_net_and_its_item_cost()
    {
        // Vellua Shipbuilding and the bridge builds: reward all zeros (not unknown), net is the reserve cost per hour.
        var build = new Mission
        {
            Name = "Vellua", Type = "Shipbuilding", Role = "Building", WorkersMin = 7, WorkersMax = 7, DurationHours = 36,
            Cost = R(food: 5000, funds: 100000, stone: 2400, timber: 2400), ItemCost = "QuartzOre x30; Malachite x20",
            Reward = R(), Bonus = new MissionBonus { ConversionPercent = null },
        };
        var a = DashboardView.AvailableOf(build);
        Assert.Equal((0L, 0L, 0L, 0L, 0L), (a.MinReward!.Food, a.MinReward.Funds, a.MinReward.Stone, a.MinReward.Timber, a.MinReward.Armaments));
        Assert.Equal(-100000.0 / 36, a.MinNetPerHour!["funds"], 6);
        Assert.Equal("QuartzOre x30; Malachite x20", a.ItemCost);
    }

    [Fact]
    public void Planner_adds_one_cycle_of_running_costs()
    {
        var state = new SnapshotLog.State();
        SnapshotLog.Add(state, Save(T0, Glenbright(), Pond()));
        var rows = DashboardView.Plan(state, new ReserveSet<long>(0, 50000, 0, 0, 0));
        var funds = rows.Single(r => r.Key == "funds");
        Assert.Equal(50900, funds.TotalNeeded);          // pond is a one-off: not part of the operating cycle
        Assert.Equal(444, funds.Shortfall);
        Assert.Equal("NotAtCurrentNet", funds.Afford.Kind);   // funds net is negative
        Assert.Equal("Ready", rows.Single(r => r.Key == "food").Afford.Kind);
    }

    [Fact]
    public void Bad_hours_per_day_falls_back_to_24()
    {
        var state = new SnapshotLog.State();
        SnapshotLog.Add(state, Save(T0, Glenbright()));
        Assert.Equal(24, DashboardView.Build(state, 0, false).HoursPerDay);
        Assert.Equal(24, DashboardView.Build(state, 99, false).HoursPerDay);
        Assert.Equal(6, DashboardView.Build(state, 6, false).HoursPerDay);
    }

    [Fact]
    public void Snapshot_log_reads_only_complete_new_lines()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cl-test-{Guid.NewGuid():N}.jsonl");
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        try
        {
            string a = JsonSerializer.Serialize(Save(T0, Glenbright()), json);
            string b = JsonSerializer.Serialize(new Snapshot { CapturedAt = T0.AddMinutes(1), Source = "process", Reserves = R(funds: 7) }, json);
            File.WriteAllText(path, a + "\n" + b[..20]);       // second line still being written
            var log = new SnapshotLog(path);
            var s1 = log.Current();
            Assert.Single(s1.History);
            Assert.Null(s1.LatestProcess);
            File.AppendAllText(path, b[20..] + "\n{not json}\n");
            var s2 = log.Current();
            Assert.Equal(2, s2.History.Count);                  // damaged line skipped
            Assert.Equal(7, s2.LatestProcess!.Reserves.Funds);
            Assert.Equal(4, s2.LatestSave!.CampLevel);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Page_files_are_embedded()
    {
        foreach (var f in new[] { "index.html", "app.js", "app.css", "crest.svg" })
            Assert.NotNull(DashboardServer.Asset(f));
        Assert.Null(DashboardServer.Asset("../CampLedgerReader.dll"));
        Assert.Contains("Camp Ledger", Encoding.UTF8.GetString(DashboardServer.Asset("index.html")!));
    }

    // ---- comrades and what-if plans ----

    static Comrade Roster(string name, long key, long? op, params (string Type, int Tier, int Pct)?[] slots) => new()
    {
        Name = name, CharacterKey = key, OperationKey = op, MissionName = op is null ? null : "Glenbright Farm: Simple Farming",
        Slots = slots.Select(s => s is null ? null : new ComradeSkill(s.Value.Type, s.Value.Tier, s.Value.Pct)).ToList(),
    };

    static SnapshotLog.State PlanState()
    {
        var g = Glenbright();
        g.OperationKey = 3624579346;
        var save = Save(T0, g, Pond());
        save.AvailableMissions[0].OperationKey = 856813139;
        save.AvailableMissions[0].Bonus!.SkillType = "Ranching";
        save.AvailableMissions[0].Reward = R(food: 3500);
        var roster = new List<Comrade>
        {
            Roster("Evelyn", 1002973, 3624579346, ("Farming", 1, 10), ("Foodprocessing", 1, 10), ("Farming", 2, 30), ("Foodprocessing", 2, 30), ("Farming", 3, 60)),
            Roster("Ashton", 574, null, ("Ranching", 1, 10), ("Weaving", 1, 10), ("Ranching", 2, 30), ("Foodprocessing", 1, 10), ("Ranching", 3, 60)),
            Roster("Aldric", 660, null, ("Building", 1, 0), ("Crafting", 1, 10), null, ("Crafting", 2, 30), null),
            Roster("Anders", 655, null), Roster("Brennan", 1002983, null), Roster("Devan", 1002435, null),
        };
        var withRoster = new Snapshot
        {
            CapturedAt = save.CapturedAt, Source = "save", CampLevel = 4, Reserves = save.Reserves, Workers = save.Workers,
            Missions = save.Missions, AvailableMissions = save.AvailableMissions, Comrades = roster,
        };
        var state = new SnapshotLog.State();
        SnapshotLog.Add(state, withRoster);
        return state;
    }

    [Fact]
    public void Comrade_slots_unlock_up_to_the_camp_level()
    {
        var v = DashboardView.Build(PlanState(), 24, false);
        var evelyn = v.Comrades.Single(c => c.Name == "Evelyn");
        Assert.True(evelyn.Busy);
        Assert.Equal(new[] { true, true, true, true, false }, evelyn.Slots!.Select(s => s!.Unlocked));
        Assert.Null(v.Comrades.Single(c => c.Name == "Aldric").Slots![2]);
        Assert.False(v.Comrades.Single(c => c.Name == "Ashton").Busy);
    }

    [Fact]
    public void Plan_adds_a_mission_with_chosen_comrades_and_their_bonus()
    {
        var add = new[] { new DashboardView.PlanAdd(856813139, new List<long> { 574, 660, 655, 1002983, 1002435 }) };
        var sim = DashboardView.Simulate(PlanState(), add, Array.Empty<long>(), 24);
        var a = sim.Added.Single();
        Assert.Equal(40, a.Bonus!.Skill);              // Ashton: Ranching I + II unlocked = 40
        Assert.Equal(80, a.Bonus.Total);
        Assert.Equal(6300, a.Reward!.Food);
        Assert.Empty(a.Problems);
        Assert.Equal(10, sim.InUseBefore);
        Assert.Equal(15, sim.InUseAfter);
        Assert.Equal(22, sim.FreeAfter);
        var food = sim.Reserves.Single(r => r.Key == "food");
        Assert.Equal(1980 / 16.0, food.PerHourBefore!.Value, 6);
        Assert.Equal(1980 / 16.0 + 6300 / 19.0, food.PerHourAfter!.Value, 6);
    }

    [Fact]
    public void Plan_flags_busy_and_missing_comrades_and_frees_stopped_ones()
    {
        var state = PlanState();
        var busy = DashboardView.Simulate(state, new[] { new DashboardView.PlanAdd(856813139, new List<long> { 1002973 }) }, Array.Empty<long>(), 24);
        var p = busy.Added.Single().Problems;
        Assert.Contains(p, x => x.Contains("Evelyn is busy"));
        Assert.Contains(p, x => x.Contains("needs 4 more"));
        Assert.Equal(5, busy.Added.Single().Workers);   // counted at the minimum

        var stopped = DashboardView.Simulate(state, new[] { new DashboardView.PlanAdd(856813139, new List<long> { 1002973 }) }, new long[] { 3624579346 }, 24);
        Assert.DoesNotContain(stopped.Added.Single().Problems, x => x.Contains("busy"));
        Assert.Equal(new[] { "Glenbright Farm" }, stopped.Stopped);
        Assert.Equal(-3500, stopped.Reserves.Single(r => r.Key == "funds").PerCycleAfter);   // Glenbright gone (-900), Apiary added (-3,500)
        Assert.Equal(10 - 2 + 5, stopped.InUseAfter);
    }

    [Fact]
    public void Plan_query_is_parsed_defensively()
    {
        var p = DashboardServer.ParseAdd("856813139:574.660,abc,12:,13:x.5");
        Assert.Equal(3, p.Count);
        Assert.Equal(new long[] { 574, 660 }, p[0].Comrades);
        Assert.Empty(p[1].Comrades);
        Assert.Equal(new long[] { 5 }, p[2].Comrades);
    }
}
