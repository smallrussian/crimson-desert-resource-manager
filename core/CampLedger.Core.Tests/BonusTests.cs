using CampLedger.Core;
using Xunit;

namespace CampLedger.Core.Tests;

/// <summary>
/// Cases from the owner's game: Glenbright Farm "Simple Farming", 1,100 Food base, Evelyn and Terry assigned,
/// camp level 4, Religion_Hernand 20. The game showed Conversion +40%, Skills +40%, Comrade +0%, total 1.9K.
/// Slots are as the reader returned them from the game data on 2026-11-12.
/// </summary>
public class BonusTests
{
    static SlotSkill S(string type, int percent) => new(type, percent);

    static readonly ComradeSlots Evelyn = new("Evelyn", new[]
        { S("Farming", 10), S("Foodprocessing", 10), S("Farming", 30), S("Foodprocessing", 30), S("Farming", 60) });

    static readonly ComradeSlots Terry = new("Terry", new[]
        { S("Farming", 10), S("Farming", 30), S("Crafting", 10), S("Foodprocessing", 10), S("Engineering", 10) });

    static BonusInputs Glenbright(int workers = 2) => new(40, "Farming", new[] { Evelyn, Terry }, workers, 2);

    [Fact]
    public void Glenbright_matches_the_game_screen()
    {
        var b = Bonus.Of(Glenbright(), campLevel: 4);
        Assert.Equal(new BonusBreakdown(40, 40, 0, 80), b);
        var reward = Bonus.Apply(new ReserveSet<long>(1100, 0, 0, 0, 0), b.Total);
        Assert.Equal(1980, reward!.Food);   // shown in game as "1.9K"
    }

    [Fact]
    public void Skill_bonus_is_the_best_single_comrade_not_a_sum()
    {
        Assert.Equal(40, Bonus.SkillPercent("Farming", new[] { Evelyn, Terry }, 4));
        Assert.Equal(100, Bonus.SkillPercent("Farming", new[] { Evelyn, Terry }, 5));   // Evelyn's Farming III unlocks
        Assert.Equal(10, Bonus.SkillPercent("Farming", new[] { Evelyn }, 2));
        Assert.Equal(0, Bonus.SkillPercent("Mining", new[] { Evelyn, Terry }, 5));
        Assert.Equal(0, Bonus.SkillPercent("Farming", Array.Empty<ComradeSlots>(), 4));
    }

    [Fact]
    public void Gate_and_empty_slots_add_nothing()
    {
        var laurent = new ComradeSlots("Laurent", new SlotSkill?[] { S("Building", 0), null, S("Fishing", 10), S("Fishing", 30) });
        Assert.Equal(40, Bonus.SkillPercent("Fishing", new[] { laurent }, 4));
        Assert.Equal(0, Bonus.SkillPercent("Building", new[] { laurent }, 4));
    }

    [Fact]
    public void Comrade_bonus_is_20_percent_per_extra_comrade_up_to_40()
    {
        Assert.Equal(0, Bonus.ComradePercent(5, 5));
        Assert.Equal(20, Bonus.ComradePercent(6, 5));
        Assert.Equal(40, Bonus.ComradePercent(7, 5));
        Assert.Equal(40, Bonus.ComradePercent(9, 5));
        Assert.Null(Bonus.ComradePercent(4, 5));
        Assert.Null(Bonus.ComradePercent(4, null));
        Assert.Equal(120, Bonus.Of(Glenbright(workers: 4), 4).Total);
    }

    [Fact]
    public void Any_unknown_part_makes_the_total_unknown()
    {
        Assert.Null(Bonus.Of(Glenbright(), campLevel: null).Total);
        Assert.Null(Bonus.Of(Glenbright() with { ConversionPercent = null }, 4).Total);
        Assert.Null(Bonus.Of(Glenbright() with { SkillType = null }, 4).Total);
        Assert.Null(Bonus.Of(Glenbright() with { Comrades = null }, 4).Total);
        Assert.Null(Bonus.Apply(new ReserveSet<long>(1100, 0, 0, 0, 0), null));
        Assert.Null(Bonus.Apply(null, 80));
    }

    [Fact]
    public void Bonus_applies_to_every_reserve_and_rounds_down()
    {
        var r = Bonus.Apply(new ReserveSet<long>(0, 3400, 1405, 0, 0), 40)!;
        Assert.Equal(4760, r.Funds);
        Assert.Equal(1967, r.Stone);   // 1405 x 1.4 = 1967.0
        Assert.Equal(1, Bonus.Apply(new ReserveSet<long>(1, 0, 0, 0, 0), 50)!.Food);   // 1.5 -> 1
    }
}
