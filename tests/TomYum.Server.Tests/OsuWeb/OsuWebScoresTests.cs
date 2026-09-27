using System.Text.Json;
using TomYum.Domain.Entities;
using TomYum.Persistence.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Translation rules from stable scores to osu-web's score/stats rows (no database needed).</summary>
public class OsuWebScoresTests
{
    private const int HD = 1 << 3, SD = 1 << 5, DT = 1 << 6, NC = 1 << 9, PF = 1 << 14, K4 = 1 << 15, TP = 1 << 23;

    [Theory]
    [InlineData(0, new[] { "CL" })]
    [InlineData(HD | DT, new[] { "HD", "DT", "CL" })]
    [InlineData(NC | DT, new[] { "NC", "CL" })]     // stable sets both bits; osu-web keeps NC only
    [InlineData(PF | SD, new[] { "PF", "CL" })]
    [InlineData(K4, new[] { "4K", "CL" })]
    [InlineData(TP, new[] { "CL" })]                // bits osu-web doesn't know are dropped
    public void ModAcronyms_FollowOsuWebsBitsetTable(int mods, string[] expected)
    {
        Assert.Equal(expected, OsuWebScores.ModAcronyms(mods));
    }

    [Fact]
    public void HitStatistics_Osu()
    {
        var (statistics, maximum) = OsuWebScores.HitStatistics(Score(mode: 0, n300: 90, n100: 8, n50: 0, miss: 2));

        Assert.Equal(new Dictionary<string, int> { ["great"] = 90, ["ok"] = 8, ["miss"] = 2 }, statistics); // zero meh dropped
        Assert.Equal(new Dictionary<string, int> { ["great"] = 100 }, maximum);
    }

    [Fact]
    public void HitStatistics_Catch_MapsTicksLikeOsuWeb()
    {
        var (statistics, maximum) = OsuWebScores.HitStatistics(Score(mode: 2, n300: 300, n100: 40, n50: 120, katu: 5, miss: 3));

        Assert.Equal(300, statistics["great"]);
        Assert.Equal(40, statistics["large_tick_hit"]);
        Assert.Equal(120, statistics["small_tick_hit"]);
        Assert.Equal(5, statistics["small_tick_miss"]);
        Assert.Equal(3, statistics["miss"]);
        Assert.Equal(125, maximum["small_tick_hit"]);
    }

    [Fact]
    public void HitStatistics_Mania_UsesGekiAndKatu()
    {
        var (statistics, maximum) = OsuWebScores.HitStatistics(Score(mode: 3, n300: 50, n100: 5, n50: 1, geki: 400, katu: 20, miss: 4));

        Assert.Equal(400, statistics["perfect"]);
        Assert.Equal(50, statistics["great"]);
        Assert.Equal(20, statistics["good"]);
        Assert.Equal(480, maximum["perfect"]);
    }

    [Fact]
    public void DataJson_HasModsStatisticsAndMaximum()
    {
        using var json = JsonDocument.Parse(OsuWebScores.DataJson(Score(mode: 0, n300: 10, mods: HD)));

        var mods = json.RootElement.GetProperty("mods").EnumerateArray().Select(m => m.GetProperty("acronym").GetString()).ToList();
        Assert.Equal(["HD", "CL"], mods);
        Assert.Equal(10, json.RootElement.GetProperty("statistics").GetProperty("great").GetInt32());
        Assert.Equal(10, json.RootElement.GetProperty("maximum_statistics").GetProperty("great").GetInt32());
    }

    [Fact]
    public void DataJson_EmptyStatistics_IsNotAnEmptyObject()
    {
        using var json = JsonDocument.Parse(OsuWebScores.DataJson(Score(mode: 0)));

        Assert.Equal(0, json.RootElement.GetProperty("statistics").GetProperty("miss").GetInt32());
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(30_000, 2.0)]              // level 2 needs 5000/3·(32−12−2) = 30,000
    [InlineData(26_931_190_828, 100.0)]    // level 100 threshold
    [InlineData(126_931_190_828, 101.0)]   // then one level per 99,999,999,999
    public void Level_FollowsTheOsuFormula(long totalScore, double expected)
    {
        Assert.Equal(expected, OsuWebScores.Level(totalScore), precision: 3);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]   // relax
    [InlineData(8, false)]   // autopilot
    public void OnlyVanillaModesAreMirrored(int gameMode, bool expected)
    {
        Assert.Equal(expected, OsuWebScores.IsMirrored(gameMode));
    }

    private static Score Score(int mode, int n300 = 0, int n100 = 0, int n50 = 0, int geki = 0, int katu = 0, int miss = 0, int mods = 0) => new()
    {
        Mode = mode,
        Count300 = n300,
        Count100 = n100,
        Count50 = n50,
        CountGeki = geki,
        CountKatu = katu,
        CountMiss = miss,
        Mods = mods,
    };
}
