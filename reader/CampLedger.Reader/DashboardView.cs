using CampLedger.Core;
using CoreSlot = CampLedger.Core.SlotSkill;

namespace CampLedger.Reader;

/// <summary>
/// What the local dashboard shows, built from the snapshot log with the calc core (the sheet's formulas).
/// Pure: no I/O, so it is tested with hand-made snapshots. Unknown stays null; the page shows "unknown".
/// </summary>
public static class DashboardView
{
    public static readonly string[] ReserveKeys = { "food", "funds", "stone", "timber", "armaments" };

    /// <summary>
    /// In-game time runs about 14.4x real time (one in-game hour ~250 real seconds), measured once on live memory
    /// (docs/re-notes/mission-static-data.md). Only used for "about" real-time estimates, labelled as such.
    /// </summary>
    public const double RealSecondsPerGameHour = 250;

    public sealed record ReserveRow(string Key, long? Balance, long? PerCycle, string? Trend, double? PerHour, double? PerDay,
                                    double? HoursUntilEmpty, bool? Sustainable, bool OperatingBudget);

    public sealed record BonusView(int? Conversion, int? Skill, int? Comrade, int? Total, string? SkillType);

    public sealed record RunningView(
        string Name, string? Type, string? Location, string? Region, string? Role, string? Category,
        int Workers, int? WorkersMin, int? WorkersMax, double? DurationHours, double? ElapsedHours, double? RemainingHours,
        double? Progress, bool Recurring, Reserves? Cost, Reserves? BaseReward, Reserves? Reward, BonusView? Bonus,
        List<string> Comrades, Reserves? Net, Dictionary<string, double>? NetPerHour, string? PersonalStorageReward, long? OperationKey = null,
        string? ItemCost = null);

    public sealed record AvailableView(
        string Name, string? Type, string? Location, string? Region, string? Role, string? Category,
        int? WorkersMin, int? WorkersMax, double? DurationHours, Reserves? Cost, Reserves? BaseReward,
        int? Conversion, string? SkillType, Reserves? MinReward, Dictionary<string, double>? MinNetPerHour,
        string? PersonalStorageReward, List<string>? Unconfirmed, List<string>? Assumptions, long? OperationKey,
        string? ItemCost = null);

    public sealed record SlotView(string Type, int Tier, int Percent, bool Unlocked);
    public sealed record ComradeView(string Name, long? Key, List<SlotView?>? Slots, string? MissionName, long? OperationKey, bool Busy);

    public sealed record PlanAdd(long OperationKey, List<long> Comrades);
    public sealed record PlannedView(long OperationKey, string Name, string? Type, int Workers, int? WorkersMin, int? WorkersMax,
                                     BonusView? Bonus, Reserves? Reward, Dictionary<string, double>? NetPerHour, List<string> Problems);
    public sealed record SimRow(string Key, long? Balance, long? PerCycleBefore, long? PerCycleAfter, double? PerHourBefore,
                                double? PerHourAfter, double? PerDayBefore, double? PerDayAfter, double? HoursUntilEmptyAfter);
    public sealed record Simulation(List<SimRow> Reserves, int? Total, int InUseBefore, int InUseAfter, int? FreeBefore, int? FreeAfter,
                                    bool? OperatingBudgetOkAfter, List<PlannedView> Added, List<string> Stopped, List<string> Problems,
                                    double HoursPerDay);

    public sealed record HistoryPoint(DateTimeOffset T, string Source, long? Food, long? Funds, long? Stone, long? Timber, long? Armaments);

    public sealed record Status(bool GameRunning, DateTimeOffset? LastUpdate, string? ReservesSource, DateTimeOffset? SaveReadAt,
                                string? GameVersion, int? CampLevel, ulong? InGameClock);

    public sealed record WorkersView(int? Total, int InUse, int? Free, string? State);

    public sealed record View(
        Status Status, List<ReserveRow> Reserves, bool? OperatingBudgetOk, double HoursPerDay, WorkersView Workers,
        List<RunningView> Running, List<AvailableView> Available, List<ComradeView> Comrades, List<HistoryPoint> History,
        List<string> PortfolioUnknowns, List<string> Unknowns, double RealSecondsPerGameHour);

    public sealed record AffordView(string Kind, double? Hours);
    public sealed record PlanRow(string Key, long UpgradeCost, long? OperatingCost, long? TotalNeeded, long? Balance,
                                 long? Shortfall, AffordView Afford);

    public static View Build(SnapshotLog.State log, double hoursPerDay, bool gameRunning, int maxHistory = 400)
    {
        if (hoursPerDay <= 0 || double.IsNaN(hoursPerDay) || hoursPerDay > 24) hoursPerDay = Calc.DefaultHoursPerDay;
        var save = log.LatestSave;
        var balanceSnap = Newer(log.LatestSave, log.LatestProcess);
        var reserves = balanceSnap?.Reserves ?? new Reserves();
        int? campLevel = save?.CampLevel;

        var running = (save?.Missions ?? new()).Select(m => RunningOf(m, campLevel)).ToList();
        var lines = running.Where(r => r.Recurring).Select(LineOf).ToList();
        var balance = Balance(reserves);
        var portfolio = Calc.PortfolioOf(lines, balance, hoursPerDay);

        var rows = ReserveSet<long?>.All.Select(r => new ReserveRow(
            Key(r), balance[r], portfolio.NetPerCycle[r], portfolio.Status[r]?.ToString(), portfolio.NetPerHour[r],
            portfolio.NetPerDay[r], portfolio.HoursUntilEmpty[r], Calc.Sustainable(portfolio.NetPerHour[r]),
            r is Reserve.Food or Reserve.Funds)).ToList();

        // Workers in use counts every running mission, one-off builds included: their comrades are busy too.
        var allLines = running.Select(LineOf).ToList();
        var w = Calc.Workers(save?.Workers.Total, allLines);

        var unknowns = new List<string>(save?.Unknowns ?? new List<string> { "no save read yet" });
        if (log.LatestProcess is not null && ReferenceEquals(balanceSnap, log.LatestProcess))
            unknowns.Insert(0, "reserves are live from game memory; missions are from the last save");

        return new View(
            new Status(gameRunning, balanceSnap?.CapturedAt, balanceSnap?.Source, save?.CapturedAt, save?.GameVersion ?? balanceSnap?.GameVersion,
                       campLevel, save?.InGameClock),
            rows, portfolio.OperatingBudgetOk, portfolio.HoursPerDay,
            new WorkersView(w.Total, w.InUse, w.Free, w.State?.ToString()),
            running,
            (save?.AvailableMissions ?? new()).Select(AvailableOf).ToList(),
            (save?.Comrades ?? new()).Select(c => ComradeOf(c, campLevel)).ToList(),
            Downsample(log.History, maxHistory),
            portfolio.Unknowns.ToList(),
            unknowns,
            RealSecondsPerGameHour);
    }

    public static List<PlanRow> Plan(SnapshotLog.State log, ReserveSet<long> upgradeCost)
    {
        var save = log.LatestSave;
        var reserves = Newer(log.LatestSave, log.LatestProcess)?.Reserves ?? new Reserves();
        var lines = (save?.Missions ?? new()).Select(m => RunningOf(m, save?.CampLevel)).Where(r => r.Recurring).Select(LineOf).ToList();
        var balance = Balance(reserves);
        var plan = Calc.PlanUpgrade(upgradeCost, lines, balance);
        return ReserveSet<long>.All.Select(r => new PlanRow(Key(r), plan.UpgradeCost[r], plan.OperatingCost[r], plan.TotalNeeded[r],
            balance[r], plan.Shortfall[r], new AffordView(plan.HoursToAfford[r].Kind.ToString(), plan.HoursToAfford[r].Hours))).ToList();
    }

    static Snapshot? Newer(Snapshot? a, Snapshot? b) => a is null ? b : b is null ? a : b.CapturedAt > a.CapturedAt ? b : a;

    static string Key(Reserve r) => r.ToString().ToLowerInvariant();

    static ReserveSet<long?> Balance(Reserves r) => new(r.Food, r.Funds, r.Stone, r.Timber, r.Armaments);

    static ReserveSet<long>? Set(Reserves? r) =>
        r is { Food: long f, Funds: long u, Stone: long s, Timber: long t, Armaments: long a } ? new(f, u, s, t, a) : null;

    static Reserves? Res(ReserveSet<long>? s) =>
        s is null ? null : new Reserves { Food = s.Food, Funds = s.Funds, Stone = s.Stone, Timber = s.Timber, Armaments = s.Armaments };

    static Dictionary<string, double>? PerHour(ReserveSet<double>? s) =>
        s is null ? null : ReserveSet<double>.All.ToDictionary(Key, r => s[r]);

    static bool AllZero(ReserveSet<long>? s) => s is not null && ReserveSet<long>.All.All(r => s[r] == 0);

    static MissionLine LineOf(RunningView r) =>
        new(r.Name, true, r.Workers, r.DurationHours, Set(r.Cost), Set(r.Reward));

    /// <summary>
    /// A running mission with its reward raised by the bonus (calc core). A base reward of all zeros stays zero
    /// whatever the bonus, so one-off builds without reserve rewards are not made unknown by an unknown bonus.
    /// Recurring = the save's repeat flag is set; one-off builds are shown but kept out of the portfolio.
    /// </summary>
    public static RunningView RunningOf(Mission m, int? campLevel)
    {
        var baseReward = Set(m.Reward);
        BonusBreakdown? b = null;
        if (m.Bonus is { } mb)
        {
            var comrades = mb.Comrades?.Select(c => new ComradeSlots(c.Name,
                c.Slots.Select(s => s is null ? null : new CoreSlot(s.Type, s.Percent)).ToList())).ToList();
            b = Bonus.Of(new BonusInputs(mb.ConversionPercent, mb.SkillType, comrades, m.Workers ?? 0, m.WorkersMin), campLevel);
        }
        var reward = AllZero(baseReward) ? baseReward : Bonus.Apply(baseReward, b?.Total);
        var line = new MissionLine(m.Name ?? "", true, m.Workers ?? 0, m.DurationHours, Set(m.Cost), reward);
        double? remaining = m.DurationHours is double d && m.ElapsedHours is double e ? Math.Max(0, d - e) : null;
        double? progress = m.DurationHours is double d2 && d2 > 0 && m.ElapsedHours is double e2 ? Math.Clamp(e2 / d2, 0, 1) : null;
        return new RunningView(
            m.Name ?? m.InternalName ?? "unknown", m.Type, m.Location, m.Region, m.Role, m.Category,
            m.Workers ?? 0, m.WorkersMin, m.WorkersMax, m.DurationHours, m.ElapsedHours, remaining, progress,
            m.Repeat == true, m.Cost, m.Reward, Res(reward),
            b is null ? null : new BonusView(b.Conversion, b.Skill, b.Comrade, b.Total, m.Bonus?.SkillType),
            m.Bonus?.Comrades?.Select(c => c.Name ?? "?").ToList() ?? new List<string>(),
            Res(Calc.Net(line)), PerHour(Calc.NetPerHour(line)), m.PersonalStorageReward, m.OperationKey, m.ItemCost);
    }

    /// <summary>Slots up to the camp level are unlocked (docs/re-notes/comrade-bonuses.md).</summary>
    public static ComradeView ComradeOf(Comrade c, int? campLevel) => new(
        c.Name ?? "?", c.CharacterKey,
        c.Slots?.Select((s, i) => s is null ? null : new SlotView(s.Type, s.Tier, s.Percent, campLevel is int lv && i < lv)).ToList(),
        c.MissionName, c.OperationKey, c.OperationKey is not null);

    static ComradeSlots SlotsOf(Comrade c) =>
        new(c.Name, (c.Slots ?? new()).Select(s => s is null ? null : new CoreSlot(s.Type, s.Percent)).ToList());

    /// <summary>
    /// What-if: the running repeating missions, minus <paramref name="stop"/>, plus available missions with chosen
    /// comrades. Rewards use the calc core's bonus rules with the chosen comrades' slots. A mission given fewer than
    /// its minimum is counted at the minimum, as if the rest had no matching skill, and flagged.
    /// </summary>
    public static Simulation Simulate(SnapshotLog.State log, IReadOnlyList<PlanAdd> add, IReadOnlyCollection<long> stop, double hoursPerDay)
    {
        if (hoursPerDay <= 0 || double.IsNaN(hoursPerDay) || hoursPerDay > 24) hoursPerDay = Calc.DefaultHoursPerDay;
        var save = log.LatestSave;
        int? campLevel = save?.CampLevel;
        var balance = Balance(Newer(log.LatestSave, log.LatestProcess)?.Reserves ?? new Reserves());
        var running = (save?.Missions ?? new()).Select(m => RunningOf(m, campLevel)).ToList();
        var roster = (save?.Comrades ?? new()).Where(c => c.CharacterKey is not null)
                     .GroupBy(c => c.CharacterKey!.Value).ToDictionary(g => g.Key, g => g.First());
        var problems = new List<string>();

        var before = running.Where(r => r.Recurring).Select(LineOf).ToList();
        var stopped = running.Where(r => r.OperationKey is long k && stop.Contains(k)).ToList();
        var after = running.Where(r => r.Recurring && !stopped.Contains(r)).Select(LineOf).ToList();

        var used = new HashSet<long>();
        var added = new List<PlannedView>();
        int addedWorkers = 0;
        foreach (var a in add)
        {
            var m = save?.AvailableMissions.FirstOrDefault(x => x.OperationKey == a.OperationKey);
            if (m is null) { problems.Add($"mission {a.OperationKey} is no longer available"); continue; }
            var own = new List<string>();
            var chosen = new List<Comrade>();
            foreach (long ck in a.Comrades.Distinct())
            {
                if (!roster.TryGetValue(ck, out var c)) { own.Add($"comrade {ck} not found"); continue; }
                if (!used.Add(ck)) { own.Add($"{c.Name} is already picked for another planned mission"); continue; }
                if (c.OperationKey is long busy && !stop.Contains(busy)) own.Add($"{c.Name} is busy on {c.MissionName}");
                if (c.Slots is null) own.Add($"{c.Name}: skill slots unknown");
                chosen.Add(c);
            }
            int min = m.WorkersMin ?? chosen.Count, max = m.WorkersMax ?? int.MaxValue;
            if (chosen.Count < min)
                own.Add($"needs {min - chosen.Count} more comrade{(min - chosen.Count == 1 ? "" : "s")}; counted as {min} with no matching skill");
            if (chosen.Count > max) own.Add($"takes at most {max} comrades");
            int workers = Math.Max(chosen.Count, min);
            addedWorkers += workers;
            BonusBreakdown? b = null;
            if (m.Bonus is { } mb)
                b = Bonus.Of(new BonusInputs(mb.ConversionPercent, mb.SkillType,
                    chosen.Any(c => c.Slots is null) ? null : chosen.Select(SlotsOf).ToList(), workers, m.WorkersMin), campLevel);
            var baseReward = Set(m.Reward);
            var reward = AllZero(baseReward) ? baseReward : Bonus.Apply(baseReward, b?.Total);
            var line = new MissionLine(m.Name ?? "", true, workers, m.DurationHours, Set(m.Cost), reward);
            after.Add(line);
            added.Add(new PlannedView(a.OperationKey, m.Location ?? m.Name ?? "?", m.Type, workers, m.WorkersMin, m.WorkersMax,
                b is null ? null : new BonusView(b.Conversion, b.Skill, b.Comrade, b.Total, m.Bonus?.SkillType),
                Res(reward), PerHour(Calc.NetPerHour(line)), own));
        }

        var pb = Calc.PortfolioOf(before, balance, hoursPerDay);
        var pa = Calc.PortfolioOf(after, balance, hoursPerDay);
        int inUseBefore = running.Sum(r => r.Workers);
        int inUseAfter = inUseBefore - stopped.Sum(r => r.Workers) + addedWorkers;
        int? total = save?.Workers.Total;
        if (total is int t && inUseAfter > t) problems.Add($"the plan needs {inUseAfter} comrades; you have {t}");
        var rows = ReserveSet<long?>.All.Select(r => new SimRow(Key(r), balance[r], pb.NetPerCycle[r], pa.NetPerCycle[r],
            pb.NetPerHour[r], pa.NetPerHour[r], pb.NetPerDay[r], pa.NetPerDay[r], pa.HoursUntilEmpty[r])).ToList();
        return new Simulation(rows, total, inUseBefore, inUseAfter, total - inUseBefore, total - inUseAfter, pa.OperatingBudgetOk,
            added, stopped.Select(r => r.Location ?? r.Name).ToList(), problems, hoursPerDay);
    }


    /// <summary>
    /// An available mission. Its minimum reward uses the conversion bonus only: skill and comrade bonuses depend on
    /// who is sent and can only add, so the per-hour figure is a floor ("at least").
    /// </summary>
    public static AvailableView AvailableOf(Mission m)
    {
        var baseReward = Set(m.Reward);
        int? conv = m.Bonus?.ConversionPercent;
        var minReward = AllZero(baseReward) ? baseReward : Bonus.Apply(baseReward, conv);
        var line = new MissionLine(m.Name ?? "", false, m.WorkersMin ?? 0, m.DurationHours, Set(m.Cost), minReward);
        return new AvailableView(
            m.Name ?? m.InternalName ?? "unknown", m.Type, m.Location, m.Region, m.Role, m.Category,
            m.WorkersMin, m.WorkersMax, m.DurationHours, m.Cost, m.Reward, conv, m.Bonus?.SkillType, Res(minReward),
            PerHour(Calc.NetPerHour(line)), m.PersonalStorageReward, m.Unconfirmed, m.Assumptions, m.OperationKey, m.ItemCost);
    }

    static List<HistoryPoint> Downsample(IReadOnlyList<HistoryPoint> h, int max)
    {
        if (h.Count <= max) return h.ToList();
        var result = new List<HistoryPoint>(max);
        double step = (h.Count - 1) / (double)(max - 1);
        for (int i = 0; i < max; i++) result.Add(h[(int)Math.Round(i * step)]);
        return result;
    }
}
