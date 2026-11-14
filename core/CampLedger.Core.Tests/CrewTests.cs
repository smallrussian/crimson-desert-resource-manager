using CampLedger.Core;
using Xunit;

namespace CampLedger.Core.Tests;

/// <summary>Crew optimizer. Hand-made comrades; slots are already cut to the unlocked ones.</summary>
public class CrewTests
{
    static SlotSkill S(string type, int percent) => new(type, percent);
    static CrewComrade C(long key, params SlotSkill?[] slots) => new(key, slots);
    static CrewMission M(string id, string? skill, int workers, double weight = 1, params long[] current) =>
        new(id, skill, workers, weight, current);

    [Fact]
    public void Leads_are_assigned_optimally_not_greedily()
    {
        // Greedy would give X (Farming 60) to the farm and leave the dock with +0%: 60 in total.
        // Best is X to the dock (Fishing 50) and Y to the farm (Farming 50): 100.
        var x = C(1, S("Farming", 60), S("Fishing", 50));
        var y = C(2, S("Farming", 50));
        var r = Crew.Optimize(new[] { M("farm", "Farming", 1), M("dock", "Fishing", 1) }, new[] { x, y });
        Assert.Equal(new long[] { 2 }, r.Crews["farm"]);
        Assert.Equal(new long[] { 1 }, r.Crews["dock"]);
        Assert.Empty(r.Short);
    }

    [Fact]
    public void Fillers_are_the_comrades_worth_least_so_strong_ones_stay_free()
    {
        var lead = C(1, S("Farming", 10), S("Farming", 30));
        var strong = C(2, S("Mining", 10), S("Mining", 30), S("Mining", 60));
        var weak = C(3, S("Weaving", 10));
        var none = C(4);
        var r = Crew.Optimize(new[] { M("farm", "Farming", 3) }, new[] { strong, weak, lead, none });
        Assert.Equal(new long[] { 1, 4, 3 }, r.Crews["farm"]);
    }

    [Fact]
    public void Current_crew_is_kept_when_nothing_is_gained()
    {
        var a = C(1, S("Farming", 40));
        var b = C(2, S("Farming", 40));
        var filler1 = C(3);
        var filler2 = C(4);
        var r = Crew.Optimize(new[] { M("farm", "Farming", 2, 1, 2, 4) }, new[] { a, b, filler1, filler2 });
        Assert.Equal(new long[] { 2, 4 }, r.Crews["farm"]);
    }

    [Fact]
    public void Heavier_missions_get_the_better_lead()
    {
        var best = C(1, S("Farming", 60));
        var ok = C(2, S("Farming", 10));
        var r = Crew.Optimize(new[] { M("small", "Farming", 1, 10), M("big", "Farming", 1, 300) }, new[] { ok, best });
        Assert.Equal(new long[] { 1 }, r.Crews["big"]);
        Assert.Equal(new long[] { 2 }, r.Crews["small"]);
    }

    [Fact]
    public void Missing_comrades_are_reported_as_short()
    {
        var r = Crew.Optimize(new[] { M("farm", "Farming", 3) }, new[] { C(1), C(2) });
        Assert.Equal(2, r.Crews["farm"].Count);
        Assert.Equal(1, r.Short["farm"]);
    }

    [Fact]
    public void Unknown_skill_type_still_gets_a_crew()
    {
        var r = Crew.Optimize(new[] { M("build", null, 2) }, new[] { C(1, S("Mining", 60)), C(2), C(3) });
        Assert.Equal(new long[] { 2, 3 }, r.Crews["build"]);   // nobody's skill counts, so the strong miner stays free
    }

    [Fact]
    public void Assignment_with_more_rows_than_columns_leaves_some_rows_out()
    {
        var a = Crew.MaxAssignment(new double[,] { { 1 }, { 5 }, { 3 } });
        Assert.Equal(new[] { -1, 0, -1 }, a);
    }

    [Fact]
    public void Match_and_worth()
    {
        var c = C(1, S("Farming", 10), S("Cooking", 10), S("Farming", 30), null);
        Assert.Equal(40, Crew.Match(c, "Farming"));
        Assert.Equal(0, Crew.Match(c, null));
        Assert.Equal(40, Crew.Worth(c));
        Assert.Equal(0, Crew.Worth(C(2)));
    }
}
