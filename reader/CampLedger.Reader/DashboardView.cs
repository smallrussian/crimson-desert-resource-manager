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
        string? ItemCost = null, bool? Repeat = null);

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
                                    List<PlannedView> Switched);
    public sealed record OptimizeResult(List<PlanAdd> Add, List<PlanAdd> Swap, List<string> Notes);

    public sealed record HistoryPoint(DateTimeOffset T, string Source, long? Food, long? Funds, long? Stone, long? Timber, long? Armaments);

    public sealed record Status(bool GameRunning, DateTimeOffset? LastUpdate, string? ReservesSource, DateTimeOffset? SaveReadAt,
                                string? GameVersion, int? CampLevel, ulong? InGameClock);

    public sealed record WorkersView(int? Total, int InUse, int? Free, string? State);

    public sealed record View(
        Status Status, List<ReserveRow> Reserves, bool? OperatingBudgetOk, WorkersView Workers,
        List<RunningView> Running, List<AvailableView> Available, List<ComradeView> Comrades, List<HistoryPoint> History,
        List<string> PortfolioUnknowns, List<string> Unknowns, double RealSecondsPerGameHour);

    public sealed record AffordView(string Kind, double? Hours);
    public sealed record PlanRow(string Key, long UpgradeCost, long? OperatingCost, long? TotalNeeded, long? Balance,
                                 long? Shortfall, AffordView Afford);

    public static View Build(SnapshotLog.State log, bool gameRunning, int maxHistory = 400)
    {
        var save = log.LatestSave;
        var balanceSnap = Newer(log.LatestSave, log.LatestProcess);
        var reserves = balanceSnap?.Reserves ?? new Reserves();
        int? campLevel = save?.CampLevel;

        var running = (save?.Missions ?? new()).Select(m => RunningOf(m, campLevel)).ToList();
        var lines = running.Where(r => r.Recurring).Select(LineOf).ToList();
        var balance = Balance(reserves);
        var portfolio = Calc.PortfolioOf(lines, balance);

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
            rows, portfolio.OperatingBudgetOk,
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
    /// Repeat = the flag as read (null when the save has no repeat field), so the UI can say "unknown".
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
            Res(Calc.Net(line)), PerHour(Calc.NetPerHour(line)), m.PersonalStorageReward, m.OperationKey, m.ItemCost, m.Repeat);
    }

    /// <summary>Slots up to the camp level are unlocked (docs/re-notes/comrade-bonuses.md).</summary>
    public static ComradeView ComradeOf(Comrade c, int? campLevel) => new(
        c.Name ?? "?", c.CharacterKey,
        c.Slots?.Select((s, i) => s is null ? null : new SlotView(s.Type, s.Tier, s.Percent, campLevel is int lv && i < lv)).ToList(),
        c.MissionName, c.OperationKey, c.OperationKey is not null);

    static ComradeSlots SlotsOf(Comrade c) =>
        new(c.Name, (c.Slots ?? new()).Select(s => s is null ? null : new CoreSlot(s.Type, s.Percent)).ToList());

    /// <summary>
    /// What-if: the running repeating missions, minus <paramref name="stop"/>, with crews switched as in
    /// <paramref name="swap"/>, plus available missions with chosen comrades. Rewards use the calc core's bonus rules
    /// with the chosen comrades' slots. A mission given fewer than its minimum is counted at the minimum, as if the
    /// rest had no matching skill, and flagged. A mission both stopped and switched counts as stopped.
    /// </summary>
    public static Simulation Simulate(SnapshotLog.State log, IReadOnlyList<PlanAdd> add, IReadOnlyCollection<long> stop,
                                      IReadOnlyList<PlanAdd>? swap = null)
    {
        var save = log.LatestSave;
        int? campLevel = save?.CampLevel;
        var balance = Balance(Newer(log.LatestSave, log.LatestProcess)?.Reserves ?? new Reserves());
        var missions = save?.Missions ?? new();
        var running = missions.Select(m => RunningOf(m, campLevel)).ToList();
        var roster = Roster(save);
        var problems = new List<string>();

        var swaps = (swap ?? Array.Empty<PlanAdd>())
            .Where(s => !stop.Contains(s.OperationKey))
            .GroupBy(s => s.OperationKey).ToDictionary(g => g.Key, g => g.First());
        var freed = stop.Concat(swaps.Keys).ToHashSet();

        var before = running.Where(r => r.Recurring).Select(LineOf).ToList();
        var stopped = running.Where(r => r.OperationKey is long k && stop.Contains(k)).ToList();
        var used = new HashSet<long>();
        int inUseBefore = running.Sum(r => r.Workers);
        int inUseAfter = inUseBefore - stopped.Sum(r => r.Workers);

        var after = new List<MissionLine>();
        var switched = new List<PlannedView>();
        for (int i = 0; i < running.Count; i++)
        {
            var r = running[i];
            if (stopped.Contains(r)) continue;
            if (r.OperationKey is long op && swaps.TryGetValue(op, out var s))
            {
                var staffed = Staff(missions[i], s.Comrades, roster, used, freed, campLevel);
                inUseAfter += staffed.View.Workers - r.Workers;
                switched.Add(staffed.View);
                if (r.Recurring) after.Add(staffed.Line);
            }
            else if (r.Recurring) after.Add(LineOf(r));
        }
        foreach (long op in swaps.Keys)
            if (!running.Any(r => r.OperationKey == op))
                problems.Add($"mission {op} is no longer running, so its crew cannot be switched");

        var added = new List<PlannedView>();
        foreach (var a in add)
        {
            var m = save?.AvailableMissions.FirstOrDefault(x => x.OperationKey == a.OperationKey);
            if (m is null) { problems.Add($"mission {a.OperationKey} is no longer available"); continue; }
            var staffed = Staff(m, a.Comrades, roster, used, freed, campLevel);
            inUseAfter += staffed.View.Workers;
            after.Add(staffed.Line);
            added.Add(staffed.View);
        }

        var pb = Calc.PortfolioOf(before, balance);
        var pa = Calc.PortfolioOf(after, balance);
        int? total = save?.Workers.Total;
        if (total is int t && inUseAfter > t) problems.Add($"the plan needs {inUseAfter} comrades; you have {t}");
        var rows = ReserveSet<long?>.All.Select(r => new SimRow(Key(r), balance[r], pb.NetPerCycle[r], pa.NetPerCycle[r],
            pb.NetPerHour[r], pa.NetPerHour[r], pb.NetPerDay[r], pa.NetPerDay[r], pa.HoursUntilEmpty[r])).ToList();
        return new Simulation(rows, total, inUseBefore, inUseAfter, total - inUseBefore, total - inUseAfter, pa.OperatingBudgetOk,
            added, stopped.Select(r => r.Location ?? r.Name).ToList(), problems, switched);
    }

    static Dictionary<long, Comrade> Roster(Snapshot? save) =>
        (save?.Comrades ?? new()).Where(c => c.CharacterKey is not null)
            .GroupBy(c => c.CharacterKey!.Value).ToDictionary(g => g.Key, g => g.First());

    /// <summary>
    /// One mission with a chosen crew: bonus, reward and problems. A comrade is busy unless they are free, already on
    /// this mission, or on a mission in <paramref name="freed"/> (stopped or switched in the plan).
    /// </summary>
    static (PlannedView View, MissionLine Line) Staff(Mission m, IReadOnlyList<long> crew, Dictionary<long, Comrade> roster,
        HashSet<long> used, HashSet<long> freed, int? campLevel)
    {
        var own = new List<string>();
        var chosen = new List<Comrade>();
        foreach (long ck in crew.Distinct())
        {
            if (!roster.TryGetValue(ck, out var c)) { own.Add($"comrade {ck} not found"); continue; }
            if (!used.Add(ck)) { own.Add($"{c.Name} is already picked for another mission in the plan"); continue; }
            if (c.OperationKey is long busy && busy != m.OperationKey && !freed.Contains(busy)) own.Add($"{c.Name} is busy on {c.MissionName}");
            if (c.Slots is null) own.Add($"{c.Name}: skill slots unknown");
            chosen.Add(c);
        }
        int min = m.WorkersMin ?? chosen.Count, max = m.WorkersMax ?? int.MaxValue;
        if (chosen.Count < min)
            own.Add($"needs {min - chosen.Count} more comrade{(min - chosen.Count == 1 ? "" : "s")}; counted as {min} with no matching skill");
        if (chosen.Count > max) own.Add($"takes at most {max} comrades");
        int workers = Math.Max(chosen.Count, min);
        BonusBreakdown? b = null;
        if (m.Bonus is { } mb)
            b = Bonus.Of(new BonusInputs(mb.ConversionPercent, mb.SkillType,
                chosen.Any(c => c.Slots is null) ? null : chosen.Select(SlotsOf).ToList(), workers, m.WorkersMin), campLevel);
        var baseReward = Set(m.Reward);
        var reward = AllZero(baseReward) ? baseReward : Bonus.Apply(baseReward, b?.Total);
        var line = new MissionLine(m.Name ?? "", true, workers, m.DurationHours, Set(m.Cost), reward);
        var view = new PlannedView(m.OperationKey ?? 0, m.Location ?? m.Name ?? "?", m.Type, workers, m.WorkersMin, m.WorkersMax,
            b is null ? null : new BonusView(b.Conversion, b.Skill, b.Comrade, b.Total, m.Bonus?.SkillType),
            Res(reward), PerHour(Calc.NetPerHour(line)), own);
        return (view, line);
    }

    /// <summary>
    /// Suggested crews for the plan (calc core <see cref="Crew.Optimize"/>). Planned missions are always staffed.
    /// Running repeating missions are re-crewed only when <paramref name="includeRunning"/> is set or they are already
    /// switched in the plan, and a switch is suggested only when the crew actually changes. Stopped missions release
    /// their comrades. Comrades in <paramref name="keep"/> are never placed, so they are also taken off any mission the
    /// optimizer may re-crew. Crew sizes stay as they are (planned missions get at least their minimum).
    /// </summary>
    public static OptimizeResult Optimize(SnapshotLog.State log, IReadOnlyList<PlanAdd> add, IReadOnlyCollection<long> stop,
        IReadOnlyList<PlanAdd> swap, IReadOnlyCollection<long> keep, bool includeRunning)
    {
        var save = log.LatestSave;
        if (save is null) return new OptimizeResult(add.ToList(), swap.ToList(), new() { "no save read yet" });
        var notes = new List<string>();
        int level = save.CampLevel ?? 0;
        if (save.CampLevel is null) notes.Add("camp level unknown, so no skill slots count as unlocked");
        var roster = Roster(save);
        var swaps = swap.Where(s => !stop.Contains(s.OperationKey)).GroupBy(s => s.OperationKey).ToDictionary(g => g.Key, g => g.First());
        var crewNow = roster.Values.Where(c => c.OperationKey is not null)
            .GroupBy(c => c.OperationKey!.Value).ToDictionary(g => g.Key, g => g.Select(c => c.CharacterKey!.Value).ToList());

        var plan = new List<CrewMission>();
        var movable = new HashSet<long>(stop);
        var runningById = new Dictionary<string, Mission>();
        foreach (var m in save.Missions)
        {
            if (m.OperationKey is not long op || stop.Contains(op)) continue;
            bool switching = swaps.TryGetValue(op, out var s);
            if (!switching && !(includeRunning && m.Repeat == true)) continue;
            var current = switching ? s!.Comrades : crewNow.GetValueOrDefault(op) ?? new List<long>();
            int workers = switching ? Math.Max(s!.Comrades.Distinct().Count(), m.WorkersMin ?? 0) : m.Workers ?? current.Count;
            string id = "r" + op;
            plan.Add(new CrewMission(id, m.Bonus?.SkillType, workers, Weight(m), current));
            runningById[id] = m;
            movable.Add(op);
        }
        var addById = new Dictionary<string, Mission>();
        foreach (var a in add)
        {
            string id = "a" + a.OperationKey;
            var m = save.AvailableMissions.FirstOrDefault(x => x.OperationKey == a.OperationKey);
            if (m is null || addById.ContainsKey(id)) continue;
            plan.Add(new CrewMission(id, m.Bonus?.SkillType, Math.Max(a.Comrades.Distinct().Count(), m.WorkersMin ?? 0), Weight(m), a.Comrades));
            addById[id] = m;
        }

        var pool = roster.Values
            .Where(c => !keep.Contains(c.CharacterKey!.Value) && (c.OperationKey is not long op || movable.Contains(op)))
            .Select(c => new CrewComrade(c.CharacterKey!.Value,
                (c.Slots ?? new()).Take(level).Select(x => x is null ? null : new CoreSlot(x.Type, x.Percent)).ToList()))
            .ToList();
        var result = Crew.Optimize(plan, pool);

        string NameOf(string id) =>
            (runningById.GetValueOrDefault(id) ?? addById.GetValueOrDefault(id)) is { } m ? m.Location ?? m.Name ?? id : id;
        foreach (var (id, n) in result.Short)
            notes.Add($"{NameOf(id)}: {n} comrade{(n == 1 ? "" : "s")} short, nobody else is free{(keep.Count > 0 ? " who is not kept on hand" : "")}");

        var newAdd = add.Select(a => addById.ContainsKey("a" + a.OperationKey)
            ? new PlanAdd(a.OperationKey, result.Crews["a" + a.OperationKey].ToList()) : a).ToList();
        var newSwap = new List<PlanAdd>();
        foreach (var (id, m) in runningById)
        {
            long op = m.OperationKey!.Value;
            var crew = result.Crews[id];
            if (crew.ToHashSet().SetEquals(crewNow.GetValueOrDefault(op) ?? new List<long>())) continue;   // same people: no switch
            newSwap.Add(new PlanAdd(op, crew.ToList()));
        }
        return new OptimizeResult(newAdd, newSwap, notes);
    }

    /// <summary>Base reward per in-game hour, all reserves summed (one unit of any reserve counts the same); 1 when unknown.</summary>
    static double Weight(Mission m)
    {
        var r = Set(m.Reward);
        if (r is null || m.DurationHours is not double d || d <= 0) return 1;
        return ReserveSet<long>.All.Sum(k => (double)r[k]) / d;
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
