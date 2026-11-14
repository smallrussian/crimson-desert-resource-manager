namespace CampLedger.Core;

/// <summary>
/// A mission the optimizer may staff. <paramref name="Workers"/> is how many comrades it gets (the optimizer does not
/// change counts). <paramref name="Weight"/> scales its skill bonus: its base reward per in-game hour, all reserves
/// summed (one unit of any reserve counts the same), or 1 when unknown. <paramref name="Current"/> is its crew now;
/// keeping them is preferred when nothing is gained by moving them.
/// </summary>
public sealed record CrewMission(string Id, string? SkillType, int Workers, double Weight, IReadOnlyList<long> Current);

/// <summary>A comrade the optimizer may place, with unlocked skill slots only (slot order kept, null = no reward skill).</summary>
public sealed record CrewComrade(long Key, IReadOnlyList<SlotSkill?> Unlocked);

public sealed record CrewResult(IReadOnlyDictionary<string, IReadOnlyList<long>> Crews, IReadOnlyDictionary<string, int> Short);

/// <summary>
/// Picks crews to maximise the plan's skill bonus. Only the best single comrade's matching skill counts towards a
/// mission's bonus (docs/re-notes/comrade-bonuses.md), so each mission gets one lead chosen by an optimal assignment
/// (Hungarian method) on skill percent x mission weight; the remaining places go to the comrades whose skills are
/// worth least, keeping the current crew where possible, so strong comrades stay free.
/// Comrades the caller wants kept on hand are simply left out of the pool.
/// </summary>
public static class Crew
{
    /// <summary>Sum of a comrade's unlocked slots matching the skill type. 0 when the type is unknown.</summary>
    public static int Match(CrewComrade c, string? skillType) =>
        skillType is null ? 0 : c.Unlocked.Where(s => s is not null && s.Type == skillType).Sum(s => s!.Percent);

    /// <summary>How much a comrade could add anywhere: their best single skill total. Used to keep strong comrades free.</summary>
    public static int Worth(CrewComrade c) =>
        c.Unlocked.Where(s => s is not null).GroupBy(s => s!.Type).Select(g => g.Sum(s => s!.Percent)).DefaultIfEmpty(0).Max();

    // A tie-breaker far smaller than any real skill difference: keep the current lead when nothing is gained.
    const double KeepBonus = 1e-6;

    public static CrewResult Optimize(IReadOnlyList<CrewMission> missions, IReadOnlyList<CrewComrade> pool)
    {
        var comrades = pool.GroupBy(c => c.Key).Select(g => g.First()).ToList();
        var staffed = missions.Where(m => m.Workers > 0).ToList();
        var crews = missions.ToDictionary(m => m.Id, _ => new List<long>());
        var taken = new HashSet<long>();

        // 1. Leads: an optimal one-to-one assignment of missions to comrades.
        if (staffed.Count > 0 && comrades.Count > 0)
        {
            var value = new double[staffed.Count, comrades.Count];
            for (int i = 0; i < staffed.Count; i++)
                for (int j = 0; j < comrades.Count; j++)
                {
                    var m = staffed[i];
                    value[i, j] = Match(comrades[j], m.SkillType) * Math.Max(0, m.Weight)
                                  + (m.Current.Contains(comrades[j].Key) ? KeepBonus : 0);
                }
            var lead = MaxAssignment(value);
            for (int i = 0; i < staffed.Count; i++)
            {
                int j = lead[i];
                if (j < 0) continue;
                var m = staffed[i];
                bool worthIt = Match(comrades[j], m.SkillType) > 0 || m.Current.Contains(comrades[j].Key);
                if (!worthIt) continue;   // no gain from this comrade: leave the place to the fillers
                crews[m.Id].Add(comrades[j].Key);
                taken.Add(comrades[j].Key);
            }
        }

        // 2. Fillers. First every mission keeps its own current crew (so a new mission never takes a running one's
        // people just to fill places), then the heaviest missions take the comrades worth least.
        var byKey = comrades.ToDictionary(c => c.Key);
        var heaviest = staffed.OrderByDescending(m => m.Weight).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        foreach (var m in heaviest)
            foreach (long k in m.Current.Distinct())
            {
                if (crews[m.Id].Count >= m.Workers) break;
                if (!byKey.ContainsKey(k) || !taken.Add(k)) continue;
                crews[m.Id].Add(k);
            }
        var shortBy = new Dictionary<string, int>();
        foreach (var m in heaviest)
        {
            var crew = crews[m.Id];
            foreach (var c in comrades.Where(c => !taken.Contains(c.Key)).OrderBy(Worth).ThenBy(c => c.Key))
            {
                if (crew.Count >= m.Workers) break;
                crew.Add(c.Key);
                taken.Add(c.Key);
            }
            if (crew.Count < m.Workers) shortBy[m.Id] = m.Workers - crew.Count;
        }

        return new CrewResult(crews.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<long>)kv.Value), shortBy);
    }

    /// <summary>
    /// Maximum-weight assignment of rows to distinct columns (Hungarian method with potentials, O(n^2 m)).
    /// Returns the column for each row, or -1 when there are more rows than columns and the row got none.
    /// </summary>
    public static int[] MaxAssignment(double[,] value)
    {
        int rows = value.GetLength(0), cols = value.GetLength(1);
        int n = rows, m = Math.Max(rows, cols);   // pad with zero-value columns so every row can be placed
        double max = 0;
        for (int i = 0; i < rows; i++) for (int j = 0; j < cols; j++) max = Math.Max(max, value[i, j]);
        double Cost(int i, int j) => j < cols ? max - value[i, j] : max;   // minimise cost = maximise value

        var u = new double[n + 1]; var v = new double[m + 1];
        var p = new int[m + 1]; var way = new int[m + 1];
        for (int i = 1; i <= n; i++)
        {
            p[0] = i;
            int j0 = 0;
            var minv = Enumerable.Repeat(double.PositiveInfinity, m + 1).ToArray();
            var used = new bool[m + 1];
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                double delta = double.PositiveInfinity;
                for (int j = 1; j <= m; j++)
                {
                    if (used[j]) continue;
                    double cur = Cost(i0 - 1, j - 1) - u[i0] - v[j];
                    if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                    if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                }
                for (int j = 0; j <= m; j++)
                {
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                    else minv[j] -= delta;
                }
                j0 = j1;
            } while (p[j0] != 0);
            do { int j1 = way[j0]; p[j0] = p[j1]; j0 = j1; } while (j0 != 0);
        }

        var result = Enumerable.Repeat(-1, rows).ToArray();
        for (int j = 1; j <= m; j++)
            if (p[j] != 0 && j - 1 < cols) result[p[j] - 1] = j - 1;
        return result;
    }
}
