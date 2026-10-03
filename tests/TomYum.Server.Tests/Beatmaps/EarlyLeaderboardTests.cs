using TomYum.Domain.Enums;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>osu-getscores6.php (numbered builds): the status line is the status alone, in their numbers.</summary>
public class EarlyLeaderboardTests
{
    [Theory]
    [InlineData(RankedStatus.NotSubmitted, "-1")]
    [InlineData(RankedStatus.Pending, "0")]
    [InlineData(RankedStatus.UpdateAvailable, "1")]
    [InlineData(RankedStatus.Ranked, "2")]
    [InlineData(RankedStatus.Approved, "3")]
    [InlineData(RankedStatus.Qualified, "3")]
    [InlineData(RankedStatus.Loved, "3")]
    public void TheStatus_IsInTheBuildsNumbers(RankedStatus status, string line) =>
        Assert.Equal(line, WebEndpoints.EarlyStatus(status));
}
