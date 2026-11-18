using Somtum.Plara;
using Xunit;

namespace TomYum.Plara.Tests;

public class PlaraPerformanceTests
{
    private static readonly PlaraProfile hishigata = PlaraProfiles.Find("osu.Game.Rulesets.Hishigata")!;
    private static readonly PlaraProfile sentakki = PlaraProfiles.Find("osu.Game.Rulesets.Sentakki")!;

    [Fact]
    public void A_real_play_gets_what_the_sidecar_gave_it()
    {
        // kahvi's hishigata pass of 4750576 (score 36333): 4.8191★, 194 notes, 85.567%, 28 misses → 32.655pp.
        double? pp = PlaraPerformanceCalculator.Calculate(hishigata, 4.8191, 194, 0.85567, 28, []);
        Assert.NotNull(pp);
        Assert.Equal(32.655, System.Math.Round(pp!.Value, 3));
    }

    [Fact]
    public void Misses_cost_their_share_of_the_notes()
    {
        double clean = PlaraPerformanceCalculator.Calculate(hishigata, 5, 1000, 1, 0, [])!.Value;
        double tenth = PlaraPerformanceCalculator.Calculate(hishigata, 5, 1000, 1, 100, [])!.Value;
        Assert.Equal(System.Math.Pow(0.9, 4), tenth / clean, 6);
    }

    [Fact]
    public void A_mod_nobody_has_looked_at_gives_no_pp()
    {
        Assert.Null(PlaraPerformanceCalculator.Calculate(sentakki, 5, 1000, 0.9, 0, ["WU"]));
        Assert.Null(PlaraPerformanceCalculator.Calculate(sentakki, 5, 1000, 0.9, 0, ["DA"]));
        Assert.Null(PlaraPerformanceCalculator.Calculate(sentakki, 5, 1000, 0.9, 0, ["NT"]));
    }

    [Fact]
    public void Touch_device_and_rate_mods_keep_full_pp()
    {
        double plain = PlaraPerformanceCalculator.Calculate(sentakki, 5, 1000, 0.9, 0, [])!.Value;
        Assert.Equal(plain, PlaraPerformanceCalculator.Calculate(sentakki, 5, 1000, 0.9, 0, ["TD"])!.Value, 9);
        Assert.Equal(plain, PlaraPerformanceCalculator.Calculate(sentakki, 5, 1000, 0.9, 0, ["DT", "Ex", "TD"])!.Value, 9);
    }

    [Fact]
    public void No_fail_costs_two_percent_a_miss_down_to_ninety()
    {
        double plain = PlaraPerformanceCalculator.Calculate(hishigata, 5, 1000, 1, 3, [])!.Value;
        Assert.Equal(0.94, PlaraPerformanceCalculator.Calculate(hishigata, 5, 1000, 1, 3, ["NF"])!.Value / plain, 6);
        double many = PlaraPerformanceCalculator.Calculate(hishigata, 5, 1000, 1, 50, [])!.Value;
        Assert.Equal(0.9, PlaraPerformanceCalculator.Calculate(hishigata, 5, 1000, 1, 50, ["NF"])!.Value / many, 6);
    }

    [Fact]
    public void Sandbox_gives_no_pp()
    {
        Assert.Null(PlaraPerformanceCalculator.Calculate(PlaraProfiles.Find("osu.Game.Rulesets.Sandbox")!, 5, 1000, 1, 0, []));
    }

    [Fact]
    public void Broken_stars_give_nothing()
    {
        Assert.Equal(0, PlaraPerformanceCalculator.Calculate(hishigata, double.PositiveInfinity, 1000, 1, 0, []));
        Assert.Equal(0, PlaraPerformanceCalculator.Calculate(hishigata, -1, 1000, 1, 0, []));
    }

    [Fact]
    public void Totals_weigh_each_map_less_than_the_one_before()
    {
        Assert.Equal(100 + 95 + 90.25, PlaraPerformanceCalculator.Total([100, 100, 100]), 6);
    }

    [Theory]
    [InlineData("osu.Game.Rulesets.Sentakki")]
    [InlineData("osu.Game.Rulesets.Tau")]
    [InlineData("osu.Game.Rulesets.Rush")]
    [InlineData("osu.Game.Rulesets.Hishigata")]
    [InlineData("osu.Game.Rulesets.Soyokaze")]
    [InlineData("osu.Game.Rulesets.Fposu")]
    [InlineData("osu.Game.Rulesets.FPSosu")]
    [InlineData("osu.Game.Rulesets.Swing")]
    [InlineData("osu.Game.Rulesets.Sandbox")]
    [InlineData("osu.Game.Rulesets.Bosu")]
    [InlineData("osu.Game.Rulesets.Touhosu")]
    public void Every_enabled_ruleset_has_a_profile(string assembly) => Assert.NotNull(PlaraProfiles.Find(assembly));

    [Fact]
    public void Osus_own_rulesets_are_left_alone() => Assert.Null(PlaraProfiles.Find("osu.Game.Rulesets.Osu"));
}
