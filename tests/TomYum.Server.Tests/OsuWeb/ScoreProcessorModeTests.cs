using System.Text.Json;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>
/// The website when ppy's score processor owns pp (lazer support, docs/lazer.md): stable and lazer
/// scores share the stats row, and totals follow ppy's formula.
/// </summary>
public sealed class ScoreProcessorModeTests : OsuWebDatabaseTest
{
    private static readonly OsuWebMirrorOptions Processor = new() { ScoreProcessor = true };

    [Fact]
    public void TotalPerformance_TakesTheBestPerMap_WeightsAndAddsBonus_LikePpy()
    {
        // Two scores on map 1 (from either client): only the better counts, but both count for the bonus.
        var (pp, accuracy) = OsuWebScores.TotalPerformance([(1, 200, 0.98), (2, 100, 0.90), (1, 150, 1.0)]);

        var bonus = (417.0 - 1.0 / 3.0) * (1 - Math.Pow(0.995, 3));
        Assert.Equal(200 + 100 * 0.95 + bonus, pp, 6);
        Assert.Equal((0.98 + 0.90 * 0.95) * 100 / (20 * (1 - Math.Pow(0.95, 2))), accuracy, 6);
        Assert.Equal((0, 0), OsuWebScores.TotalPerformance([]));
    }

    [Fact]
    public void ProcessingItem_HasTheRowAndOptionallyAHistory()
    {
        var row = new OsuWebScore
        {
            Id = 42, UserId = 3, RulesetId = 0, BeatmapId = 100, Rank = "S", Passed = true, Preserve = true, Ranked = true,
            Accuracy = 0.97f, TotalScore = 1000, Data = """{"mods":[{"acronym":"CL"}]}""", LegacyScoreId = 0,
            EndedAt = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc),
        };

        using var fresh = JsonDocument.Parse(OsuWebScores.ProcessingItem(row));
        var score = fresh.RootElement.GetProperty("Score");
        Assert.Equal(42UL, score.GetProperty("id").GetUInt64());
        Assert.Equal("S", score.GetProperty("rank").GetString());
        Assert.Equal("""{"mods":[{"acronym":"CL"}]}""", score.GetProperty("data").GetString()); // a string, like osu-web sends
        Assert.Equal(0UL, score.GetProperty("legacy_score_id").GetUInt64());
        Assert.False(fresh.RootElement.TryGetProperty("ProcessHistory", out _));

        using var again = JsonDocument.Parse(OsuWebScores.ProcessingItem(row, previousVersion: 10));
        Assert.Equal(10, again.RootElement.GetProperty("ProcessHistory").GetProperty("processed_version").GetInt32());
    }

    [MySqlFact]
    public async Task StableCounters_AreAddedAsChanges_SoLazersShareStays()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var player = await NewPlayerAsync();
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext(), Processor);
        await mirror.EnsureLinkTableAsync();
        tomyum.UserStats.Add(new UserStats { UserId = player, Mode = 0, PlayCount = 2, RankedScore = 1_000, TotalScore = 3_000, Pp = 50 });
        await tomyum.SaveChangesAsync();
        tomyum.ChangeTracker.Clear();

        await mirror.WriteStatsAsync(player, 0);
        // Lazer plays and the processor's pp land in the same row.
        await ExecuteAsync($"UPDATE osu_user_stats SET playcount = playcount + 5, total_score = total_score + 70000, rank_score = 321, x_rank_count = 2 WHERE user_id = {player}");

        var stats = await tomyum.UserStats.FindAsync(player, 0);
        stats!.PlayCount = 3;
        stats.TotalScore = 4_000;
        await tomyum.SaveChangesAsync();
        await mirror.WriteStatsAsync(player, 0);

        Assert.Equal(8, await ScalarAsync<int>($"SELECT playcount FROM osu_user_stats WHERE user_id = {player}"));
        Assert.Equal(74_000L, await ScalarAsync<long>($"SELECT total_score FROM osu_user_stats WHERE user_id = {player}"));
        // pp and grade counts belong to the processor: TomYum's 50 pp never overwrites them.
        Assert.Equal(321f, await ScalarAsync<float>($"SELECT rank_score FROM osu_user_stats WHERE user_id = {player}"));
        Assert.Equal(2, await ScalarAsync<int>($"SELECT x_rank_count FROM osu_user_stats WHERE user_id = {player}"));
    }

    [MySqlFact]
    public async Task TotalPerformance_CombinesStableAndLazerScores()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var player = await NewPlayerAsync();
        await ExecuteAsync($"""
            INSERT INTO osu_user_stats (user_id, accuracy_total, accuracy_count, accuracy, playcount, ranked_score, total_score,
              x_rank_count, s_rank_count, a_rank_count, `rank`, level, rank_score, rank_score_index, accuracy_new)
            VALUES ({player}, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0)
            """);
        // A stable (legacy) and a lazer score on map 1, a lazer score on map 2, and one that doesn't count.
        await AddWebsiteScoreAsync(player, 1, pp: 100, accuracy: 0.95, legacy: true);
        await AddWebsiteScoreAsync(player, 1, pp: 120, accuracy: 0.99, legacy: false);
        await AddWebsiteScoreAsync(player, 2, pp: 80, accuracy: 0.90, legacy: false);
        await AddWebsiteScoreAsync(player, 3, pp: 500, accuracy: 1, legacy: false, ranked: false);

        var result = await new OsuWebScoreMirror(tomyum, NewOsuWebContext(), Processor).RecomputeTotalPerformanceAsync(player, 0);

        var expected = OsuWebScores.TotalPerformance([(1, 120, 0.99), (1, 100, 0.95), (2, 80, 0.90)]);
        Assert.Equal(expected.Pp, result!.Pp, 3);
        Assert.Equal((float)expected.Pp, await ScalarAsync<float>($"SELECT rank_score FROM osu_user_stats WHERE user_id = {player}"), 2);
        Assert.Equal((float)(expected.Accuracy / 100), result.Accuracy, 5);
    }

    [MySqlFact]
    public async Task UnrankingAMap_TakesPpFromItsLazerScores()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var player = await NewPlayerAsync();
        await new OsuWebBeatmapMirror(NewOsuWebContext()).WriteAsync(new Beatmap
        {
            Id = 1, SetId = 1, Md5 = "md5-1", Status = RankedStatus.Loved, OsuApiStatus = 4, Version = "x", Artist = "a", Title = "t", Creator = "c",
        });
        await AddWebsiteScoreAsync(player, 1, pp: 120, accuracy: 0.99, legacy: false);
        await AddWebsiteScoreAsync(player, 1, pp: 100, accuracy: 0.95, legacy: true); // TomYum sets stable pp itself

        var players = await new OsuWebScoreMirror(tomyum, NewOsuWebContext(), Processor).ClearLazerPpOnUnrankedAsync([1]);

        Assert.Equal([(player, 0)], players);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM scores WHERE pp IS NULL"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM scores WHERE legacy_score_id IS NOT NULL AND pp = 100"));
    }

    private async Task<int> NewPlayerAsync()
    {
        var user = await new OsuWebUserRepository(NewOsuWebContext()).AddAsync(new User
        {
            Username = "Both", SafeName = "both", Email = "both@example.com", PasswordBcrypt = "x", Country = "th",
        });
        return user.Id;
    }

    private Task AddWebsiteScoreAsync(int userId, int beatmapId, double pp, double accuracy, bool legacy, bool ranked = true) =>
        ExecuteAsync(FormattableString.Invariant($"""
            INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, total_score, data, pp, legacy_score_id, ended_at)
            VALUES ({userId}, 0, {beatmapId}, 1, {(ranked ? 1 : 0)}, 'S', 1, {accuracy}, 1000, '{"{}"}', {pp}, {(legacy ? "0" : "NULL")}, UTC_TIMESTAMP())
            """));
}
