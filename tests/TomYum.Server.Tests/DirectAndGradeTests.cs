using Microsoft.Extensions.Configuration;
using TomYum.Server.Beatmaps;
using TomYum.Server.Discord;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>osu!direct's status filter and tabs, and grades on Discord.</summary>
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

    [Fact]
    public void Grades_UseTheirEmoji_WhenOneIsSet()
    {
        var options = DiscordOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GradeEmojis:XH"] = "<:rankingXH:1554341902537588786>",
            ["GradeEmojis:f"] = "<:rankingF:1554341901400940635>",
            ["GradeEmojis:A"] = "not an emoji",
            ["GradeEmojis:Z"] = "<:rankingZ:1>",
        }).Build());
        Assert.Equal(["F", "XH"], options.GradeEmojis.Keys.Order(StringComparer.Ordinal));

        Assert.Equal("<:rankingXH:1554341902537588786>", DiscordFormat.Grade("XH", options.GradeEmojis));
        Assert.Equal("<:rankingF:1554341901400940635>", DiscordFormat.Grade("", options.GradeEmojis)); // failed
        Assert.Equal("A", DiscordFormat.Grade("A", options.GradeEmojis));
        Assert.Equal("S (silver)", DiscordFormat.Grade("SH", new Dictionary<string, string>()));
    }
}
