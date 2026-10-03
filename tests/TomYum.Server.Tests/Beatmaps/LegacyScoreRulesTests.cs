using TomYum.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>Old clients' scores on today's leaderboards (docs/old-clients.md).</summary>
public class LegacyScoreRulesTests
{
    private static string[] Parts(string version)
    {
        var parts = new string[18];
        Array.Fill(parts, "0");
        parts[17] = version;
        return parts;
    }

    [Theory]
    [InlineData("20140624", 2014, 6, 24)]
    [InlineData("20131231  ", 2013, 12, 31)] // padded with anti-cheat flag spaces
    [InlineData("b20150101", 2015, 1, 1)]
    public void TheBuild_IsTheScoresVersion(string version, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), LegacyScoreRules.BuildOf(Parts(version)).Date);

    [Theory]
    [InlineData(15)] // b337–b476
    [InlineData(16)] // b497–b558
    [InlineData(17)] // b595b–b1218
    public void WithoutAVersion_ItsAnOldBuild(int fields)
    {
        var build = LegacyScoreRules.BuildOf(new string[fields]);
        Assert.True(build.IsLegacy);
        // The player's session says which, when they're online.
        Assert.Equal(1122, LegacyScoreRules.BuildOf(new string[fields], ClientBuild.Parse("b1122")).Number);
        // A session of today's client doesn't make the score today's.
        Assert.True(LegacyScoreRules.BuildOf(new string[fields], ClientBuild.Parse("b20240101")).IsLegacy);
    }

    [Theory]
    [InlineData("1553")]
    [InlineData("1700 ")]
    [InlineData("")]
    public void NumberedBuilds_AreOld(string version)
    {
        const int hiddenAndBit4 = (int)(Mods.Hidden | Mods.TouchDevice);
        var build = LegacyScoreRules.BuildOf(Parts(version));

        Assert.True(build.IsLegacy);
        Assert.Equal((int)Mods.Hidden, LegacyScoreRules.Mods(hiddenAndBit4, build));
    }

    [Fact]
    public void NoVideo_IsntTouchDevice_Before2014()
    {
        const int hiddenAndBit4 = (int)(Mods.Hidden | Mods.TouchDevice);

        Assert.Equal((int)Mods.Hidden, LegacyScoreRules.Mods(hiddenAndBit4, ClientBuild.Parse("b20131231")));
        Assert.Equal((int)Mods.Hidden, LegacyScoreRules.Mods(hiddenAndBit4, ClientBuild.Parse("b1122")));
        Assert.Equal(hiddenAndBit4, LegacyScoreRules.Mods(hiddenAndBit4, ClientBuild.Parse("b20140101")));
        Assert.Equal(hiddenAndBit4, LegacyScoreRules.Mods(hiddenAndBit4, ClientBuild.Parse("b20240101")));
    }

    [Fact]
    public void CoOp_IsRefused_FromOldClients()
    {
        Assert.True(LegacyScoreRules.IsRefused((int)Mods.KeyCoop, ClientBuild.Parse("b20150101")));
        Assert.False(LegacyScoreRules.IsRefused((int)Mods.Hidden, ClientBuild.Parse("b20150101")));
        // Today's clients are left to today's rules.
        Assert.False(LegacyScoreRules.IsRefused((int)Mods.KeyCoop, ClientBuild.Parse("b20240101")));
    }

    [Theory]
    // taiko: 300s + 100s + misses = the map's max combo in taiko
    [InlineData(1, 500, 20, 5, 525, true)]
    [InlineData(1, 500, 20, 5, 530, false)]
    // catch: fruits and droplets caught (300s, 100s) + misses
    [InlineData(2, 300, 80, 2, 382, true)]
    [InlineData(2, 300, 80, 2, 390, false)]
    // standard and mania aren't checked; an unknown max combo counts as a match
    [InlineData(0, 1, 1, 1, 999, true)]
    [InlineData(3, 1, 1, 1, 999, true)]
    [InlineData(1, 500, 20, 5, null, true)]
    public void Judgements_MustCoverTheMap_InTaikoAndCatch(int mode, int n300, int n100, int miss, int? maxCombo, bool expected) =>
        Assert.Equal(expected, LegacyScoreRules.JudgementsMatch(mode, n300, n100, miss, maxCombo));
}
