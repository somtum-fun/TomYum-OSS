using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Lazer's daily challenge queue.</summary>
public sealed class DailyChallengeTests
{
    [Theory]
    [InlineData("2026-10-01", 3.0, 3.5)] // Thursday: the week starts easy
    [InlineData("2026-10-02", 3.5, 4.0)]
    [InlineData("2026-10-03", 4.0, 4.5)]
    [InlineData("2026-10-04", 4.5, 5.0)]
    [InlineData("2026-10-05", 5.0, 5.5)]
    [InlineData("2026-10-06", 5.5, 6.0)]
    [InlineData("2026-10-07", 6.0, 6.5)] // Wednesday: and ends hard
    [InlineData("2026-10-08", 3.0, 3.5)]
    public void Stars_FollowTheWeek(string day, double min, double max)
    {
        Assert.Equal((min, max), new DailyChallengeOptions().StarsFor(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Stars_WithoutTheWeek_AreTheSameEveryDay()
    {
        var options = new DailyChallengeOptions { Weekly = false, MinStars = 4, MaxStars = 6.5 };

        Assert.Equal((4.0, 6.5), options.StarsFor(new DateOnly(2026, 10, 1)));
        Assert.Equal((4.0, 6.5), options.StarsFor(new DateOnly(2026, 10, 7)));
    }
}
