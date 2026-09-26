using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Relax/Autopilot are folded into a combined gamemode (bancho.py's model) so they get their own
/// leaderboards + stats and never mix with vanilla. Vanilla boards rank by score; relax/autopilot
/// rank by pp.
/// </summary>
public class RelaxGameModeTests
{
    [Theory]
    [InlineData(0, Mods.None, 0)]        // vn!std
    [InlineData(2, Mods.None, 2)]        // vn!catch
    [InlineData(0, Mods.Relax, 4)]       // rx!std
    [InlineData(2, Mods.Relax, 6)]       // rx!catch
    [InlineData(0, Mods.Autopilot, 8)]   // ap!std
    [InlineData(0, Mods.Relax | Mods.Hidden, 4)] // other mods don't affect the variant
    public void ToGameMode_folds_relax_and_autopilot(int vanilla, Mods mods, int expected)
        => Assert.Equal(expected, mods.ToGameMode(vanilla));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]  // rx!std -> osu ruleset
    [InlineData(6, 2)]  // rx!catch -> catch ruleset
    [InlineData(8, 0)]  // ap!std -> osu ruleset
    public void AsVanilla_returns_underlying_ruleset(int gameMode, int expected)
        => Assert.Equal(expected, GameMode.AsVanilla(gameMode));

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]   // relax
    [InlineData(8, true)]   // autopilot
    public void UsesPpLeaderboard_only_for_relax_and_autopilot(int gameMode, bool expected)
        => Assert.Equal(expected, GameMode.UsesPpLeaderboard(gameMode));

    [Fact]
    public async Task Leaderboard_separates_relax_from_vanilla_and_sorts_each_correctly()
    {
        var repo = new InMemoryScoreRepository();
        const string md5 = "abc";

        // Vanilla (mode 0): higher score should win even though its pp is lower.
        await repo.AddAsync(Score(userId: 1, mode: 0, md5: md5, scoreValue: 1_000_000, pp: 5));
        await repo.AddAsync(Score(userId: 2, mode: 0, md5: md5, scoreValue: 500_000, pp: 50));
        // Relax (mode 4): higher pp should win even though its score is lower.
        await repo.AddAsync(Score(userId: 3, mode: 4, md5: md5, scoreValue: 500_000, pp: 50));
        await repo.AddAsync(Score(userId: 4, mode: 4, md5: md5, scoreValue: 1_000_000, pp: 5));

        var vanilla = await repo.GetLeaderboardAsync(md5, 0, 50);
        var relax = await repo.GetLeaderboardAsync(md5, 4, 50);

        // No cross-contamination.
        Assert.All(vanilla, s => Assert.Equal(0, s.Mode));
        Assert.All(relax, s => Assert.Equal(4, s.Mode));

        // Vanilla ranked by score: user 1 (1,000,000) first.
        Assert.Equal(1, vanilla[0].UserId);
        // Relax ranked by pp: user 3 (50pp) first.
        Assert.Equal(3, relax[0].UserId);
    }

    private static Score Score(int userId, int mode, string md5, long scoreValue, double pp) => new()
    {
        UserId = userId,
        BeatmapId = 1,
        BeatmapMd5 = md5,
        Mode = mode,
        ScoreValue = scoreValue,
        Pp = pp,
        Grade = "A",
        Passed = true,
        Status = ScoreStatus.Best,
        SubmittedAt = DateTimeOffset.UnixEpoch,
    };
}
