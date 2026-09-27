using System.Text.Json;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>Reading osu! API v2 beatmapsets and description threads (<see cref="OsuApiV2Client"/>).</summary>
public sealed class OsuApiV2ClientTests
{
    [Fact]
    public void Beatmapset_GivesNamedCurrentNominators_AndTheDescriptionThread()
    {
        using var set = JsonDocument.Parse("""
        {
          "id": 950289,
          "legacy_thread_url": "https://osu.ppy.sh/community/forums/topics/844541",
          "current_nominations": [
            {"beatmapset_id": 950289, "rulesets": ["osu"], "reset": false, "user_id": 101},
            {"beatmapset_id": 950289, "rulesets": null, "reset": false, "user_id": 202},
            {"beatmapset_id": 950289, "rulesets": ["osu"], "reset": true, "user_id": 303}
          ],
          "related_users": [{"id": 101, "username": "Nominator One"}, {"id": 999, "username": "Mapper"}]
        }
        """);

        var (nominators, topicId) = OsuApiV2Client.ParseBeatmapset(set.RootElement);

        Assert.Equal(844541, topicId);
        Assert.Equal(2, nominators.Count); // the reset nomination doesn't count
        Assert.Equal((101, "Nominator One"), (nominators[0].OsuUserId, nominators[0].Username));
        Assert.Equal(["osu"], nominators[0].Rulesets);
        Assert.Equal((202, "#202"), (nominators[1].OsuUserId, nominators[1].Username)); // not in related_users
        Assert.Null(nominators[1].Rulesets);
    }

    [Fact]
    public void Beatmapset_WithoutThreadOrNominations_GivesNothing()
    {
        using var set = JsonDocument.Parse("""{"id": 1, "legacy_thread_url": null, "current_nominations": []}""");

        var (nominators, topicId) = OsuApiV2Client.ParseBeatmapset(set.RootElement);

        Assert.Empty(nominators);
        Assert.Null(topicId);
    }

    [Theory]
    [InlineData("[notice][b]Artist:[/b] X[/notice]\n---------------\n[b]Hi![/b] Thanks for playing.\n", "[b]Hi![/b] Thanks for playing.")]
    [InlineData("[notice]header only[/notice]\n---------------\n   \n", null)]
    [InlineData("no separator here", null)]
    public void Description_IsWhatFollowsTheGeneratedHeader(string raw, string? expected)
    {
        using var topic = JsonDocument.Parse(JsonSerializer.Serialize(new { posts = new[] { new { body = new { html = "<p/>", raw } } } }));

        Assert.Equal(expected, OsuApiV2Client.DescriptionFromTopic(topic.RootElement));
    }
}
