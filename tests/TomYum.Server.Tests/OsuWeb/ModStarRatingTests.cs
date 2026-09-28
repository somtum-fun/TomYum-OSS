using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Score cards show the star rating with the play's mods (and in the ruleset played).</summary>
public sealed class ModStarRatingTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task Cards_AreRatedWithTheirMods_WhenTheModsChangeDifficulty()
    {
        await NewTomYumContextAsync();
        var player = (await new OsuWebUserRepository(NewOsuWebContext()).AddAsync(new User
        {
            Username = "Player", SafeName = "player", Email = "p@x.dev", PasswordBcrypt = "x", Country = "nl",
        })).Id;
        await new OsuWebBeatmapMirror(NewOsuWebContext()).WriteAsync(new Beatmap
        {
            Id = 100, SetId = 10, Md5 = "a", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "Hard", Artist = "A", Title = "T", Creator = "M", StarRating = 5,
        });
        foreach (var (ruleset, mods) in new[] { (0, "[{\"acronym\":\"HD\"},{\"acronym\":\"CL\"}]"), (0, "[{\"acronym\":\"HD\"},{\"acronym\":\"DT\"}]"), (1, "[]") })
        {
            await ExecuteAsync($$$"""
                INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, max_combo, total_score, data, pp, ended_at)
                VALUES ({{{player}}}, {{{ruleset}}}, 100, 1, 1, 'S', 1, 0.97, 400, 900000, '{"mods":{{{mods}}},"statistics":{"great":300}}', 100, UTC_TIMESTAMP())
                """);
        }

        var stars = new FakeStars();
        var community = new OsuWebCommunity(NewOsuWebContext(), stars);
        var cards = (await community.GetRecentAsync(player, 0, 10)).OrderBy(c => c.ScoreId).ToList();
        Assert.Equal([5.0, 7.25], cards.Select(c => c.StarRating));
        Assert.Equal(1.5, Assert.Single(await community.GetRecentAsync(player, 1, 10)).StarRating); // converted to taiko
        Assert.Equal(["100:0:HD,DT", "100:1:"], stars.Asked);

        // Without the calculator, the map's own rating.
        Assert.All(await new OsuWebCommunity(NewOsuWebContext()).GetRecentAsync(player, 0, 10), c => Assert.Equal(5.0, c.StarRating));
    }

    private sealed class FakeStars : IStarRatings
    {
        public List<string> Asked { get; } = [];

        public Task<double?> GetAsync(int beatmapId, int ruleset, IReadOnlyList<string> mods, CancellationToken cancellationToken = default)
        {
            Asked.Add($"{beatmapId}:{ruleset}:{string.Join(',', mods)}");
            return Task.FromResult<double?>(ruleset == 1 ? 1.5 : 7.25);
        }
    }
}
