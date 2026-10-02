using TomYum.Domain.Entities;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>Leaderboard replies in the older formats (`vv`) old clients ask for (docs/old-clients.md).</summary>
public class LegacyLeaderboardTests
{
    [Theory]
    // Today's format: unchanged.
    [InlineData(2, 4, 2)]
    [InlineData(5, 4, 5)]
    // Format 3: no loved (approved instead).
    [InlineData(5, 3, 3)]
    [InlineData(2, 3, 2)]
    [InlineData(4, 3, 4)]
    // Formats 1–2: ranked is 4, qualified 2, loved approved.
    [InlineData(2, 2, 4)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 2, 3)]
    [InlineData(3, 1, 3)]
    [InlineData(0, 2, 0)]
    [InlineData(-1, 2, -1)]
    public void TheStatus_IsInTheClientsNumbering(int status, int formatVersion, int expected) =>
        Assert.Equal(expected, WebEndpoints.StatusFor(status, formatVersion));

    [Fact]
    public void ScoreLines_FollowTheFormatVersion()
    {
        var score = new Score
        {
            Id = 7, UserId = 2, ScoreValue = 123456, MaxCombo = 300, Count300 = 250, Count100 = 3, Count50 = 1,
            CountMiss = 0, CountGeki = 40, CountKatu = 2, Perfect = true, Mods = 64,
            SubmittedAt = new DateTimeOffset(2014, 6, 24, 12, 30, 0, TimeSpan.Zero),
        };

        Assert.Equal("7|Lilian|123456|300|1|3|250|0|2|40|1|64|2|1|1403613000|1",
            WebEndpoints.FormatScoreLine(score, "Lilian", 1, hasReplay: true, showPp: false, formatVersion: 4));
        Assert.Equal("7|Lilian|123456|300|1|3|250|0|2|40|1|64|2|1|1403613000",
            WebEndpoints.FormatScoreLine(score, "Lilian", 1, hasReplay: true, showPp: false, formatVersion: 2));
        Assert.Equal("7|Lilian|123456|300|1|3|250|0|2|40|1|64|2|1|2014-06-24 12:30:00",
            WebEndpoints.FormatScoreLine(score, "Lilian", 1, hasReplay: true, showPp: false, formatVersion: 1));
    }
}
