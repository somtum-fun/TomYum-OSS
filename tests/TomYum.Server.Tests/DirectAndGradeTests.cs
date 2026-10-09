using Microsoft.Extensions.Configuration;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>osu!direct's status filter and tabs.</summary>
public sealed class DirectAndGradeTests
{
    [Theory]
    [InlineData(0, 1)]     // "Ranked" (Classée): was sent as pending
    [InlineData(2, 0)]     // Pending
    [InlineData(3, 3)]     // Qualified
    [InlineData(4, null)]  // All
    [InlineData(5, -2)]    // Graveyard
    [InlineData(7, 1)]     // Ranked (played)
    [InlineData(8, 4)]     // Loved
    public void TheClientsStatusFilter_IsTheMirrorsStatus(int client, int? mirror) =>
        Assert.Equal(mirror, DirectEndpoints.ToMirrorStatus(client));

    [Theory]
    [InlineData("Newest", true)]
    [InlineData("Top Rated", true)]   // '+' arrives as a space
    [InlineData("Top+Rated", true)]
    [InlineData("Most Played", true)]
    [InlineData("operation ground and pound", false)]
    public void Tabs_AreNotSearchText(string query, bool tab) => Assert.Equal(tab, DirectEndpoints.IsTab(query));
}
