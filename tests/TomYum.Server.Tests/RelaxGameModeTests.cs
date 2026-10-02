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

    [Fact]
    public async Task StableBoard_leaves_lazer_plays_out_and_shows_each_players_best_stable_play()
    {
        var repo = new InMemoryScoreRepository();
        const string md5 = "abc";

        // Player 1's best overall is a lazer play; their best stable play is the one stable shows.
        var stable = Score(userId: 1, mode: 4, md5: md5, scoreValue: 1, pp: 40);
        stable.Status = ScoreStatus.Submitted;
        await repo.AddAsync(stable);
        await repo.AddAsync(Score(userId: 1, mode: 4, md5: md5, scoreValue: 1, pp: 90, fromLazer: true));
        // Player 2 only played on lazer: not on stable's board.
        await repo.AddAsync(Score(userId: 2, mode: 4, md5: md5, scoreValue: 1, pp: 70, fromLazer: true));
        await repo.AddAsync(Score(userId: 3, mode: 4, md5: md5, scoreValue: 1, pp: 50));

        var board = await repo.GetLeaderboardAsync(md5, 4, 50, null, new TomYum.Domain.Repositories.LeaderboardFilter(StableOnly: true));
        Assert.Equal([3, 1], board.Select(s => s.UserId));
        Assert.Equal(40, board[1].Pp);
        Assert.Equal(40, (await repo.GetStablePersonalBestAsync(1, md5, 4))!.Pp);
        Assert.Null(await repo.GetStablePersonalBestAsync(2, md5, 4));

        // The website's board (all plays) still has everyone's best.
        Assert.Equal([1, 2, 3], (await repo.GetLeaderboardAsync(md5, 4, 50)).Select(s => s.UserId));
    }

    private static Score Score(int userId, int mode, string md5, long scoreValue, double pp, bool fromLazer = false) => new()
    {
        FromLazer = fromLazer,
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
