using System.Text.Json;
using StackExchange.Redis;
using TomYum.Caching;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>
/// <see cref="OsuWebScoreMirror"/> / <see cref="OsuWebSync"/> against a real TomYum database and
/// osu-web's real <c>scores</c> and <c>osu_user_stats*</c> tables.
/// </summary>
public sealed class OsuWebScoreMirrorTests : OsuWebDatabaseTest
{
    private const int RankedMapId = 100, LovedMapId = 200;

    [MySqlFact]
    public async Task WriteScore_StoresALegacyScoreOsuWebCanShow()
    {
        await using var tomyum = await SeedAsync();
        var score = await AddScoreAsync(tomyum, Play(userId: 2, RankedMapId, value: 1_234_567, pp: 150.5, mods: 1 << 3));

        var id = await new OsuWebScoreMirror(tomyum, NewOsuWebContext()).WriteScoreAsync(score);

        Assert.NotNull(id);
        var row = $"FROM scores WHERE id = {id}";
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT legacy_score_id {row}"));             // legacy (stable) score
        Assert.Equal(1_234_567L, await ScalarAsync<long>($"SELECT legacy_total_score {row}"));
        Assert.Equal(1_234_567L, await ScalarAsync<long>($"SELECT total_score {row}"));
        Assert.Equal(1, await ScalarAsync<int>($"SELECT ranked {row}"));
        Assert.Equal(1, await ScalarAsync<int>($"SELECT preserve {row}"));
        Assert.Equal("S", await ScalarAsync<string>($"SELECT `rank` {row}"));
        Assert.Equal(150.5, await ScalarAsync<double>($"SELECT pp {row}"), precision: 2);
        Assert.InRange(await ScalarAsync<double>($"SELECT accuracy {row}"), 0.9, 1.0);          // fraction, not percent

        using var data = JsonDocument.Parse(await ScalarAsync<string>($"SELECT data {row}"));
        var mods = data.RootElement.GetProperty("mods").EnumerateArray().Select(m => m.GetProperty("acronym").GetString());
        Assert.Equal(["HD", "CL"], mods);
    }

    [MySqlFact]
    public async Task WriteScore_IsIdempotent()
    {
        await using var tomyum = await SeedAsync();
        var score = await AddScoreAsync(tomyum, Play(userId: 2, RankedMapId));
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());

        var first = await mirror.WriteScoreAsync(score);
        var second = await mirror.WriteScoreAsync(score);

        Assert.Equal(first, second);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM scores"));
    }

    [MySqlFact]
    public async Task FailedScore_IsNotRankedOrPreserved()
    {
        await using var tomyum = await SeedAsync();
        var fail = Play(userId: 2, RankedMapId, pp: 0, passed: false);
        fail.Status = ScoreStatus.Failed;
        var score = await AddScoreAsync(tomyum, fail);

        var id = await new OsuWebScoreMirror(tomyum, NewOsuWebContext()).WriteScoreAsync(score);

        Assert.Equal(0, await ScalarAsync<int>($"SELECT ranked FROM scores WHERE id = {id}"));
        Assert.Equal(0, await ScalarAsync<int>($"SELECT preserve FROM scores WHERE id = {id}"));
        Assert.Equal("F", await ScalarAsync<string>($"SELECT `rank` FROM scores WHERE id = {id}"));
        Assert.Null(await ScalarAsync<double?>($"SELECT pp FROM scores WHERE id = {id}"));
    }

    [MySqlFact]
    public async Task LovedScore_IsRankedWithoutPp()
    {
        await using var tomyum = await SeedAsync();
        var score = await AddScoreAsync(tomyum, Play(userId: 2, LovedMapId, pp: 0));

        var id = await new OsuWebScoreMirror(tomyum, NewOsuWebContext()).WriteScoreAsync(score);

        Assert.Equal(1, await ScalarAsync<int>($"SELECT ranked FROM scores WHERE id = {id}"));
        Assert.Null(await ScalarAsync<double?>($"SELECT pp FROM scores WHERE id = {id}"));
    }

    [MySqlFact]
    public async Task RelaxScores_AndUnknownMaps_AreNotMirrored()
    {
        await using var tomyum = await SeedAsync();
        var relax = Play(userId: 2, RankedMapId, mode: GameMode.RelaxOffset);
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());

        Assert.Null(await mirror.WriteScoreAsync(await AddScoreAsync(tomyum, relax)));
        Assert.Null(await mirror.WriteScoreAsync(await AddScoreAsync(tomyum, Play(userId: 2, beatmapId: 999))));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM scores"));
    }

    [MySqlFact]
    public async Task WriteStats_RebuildsTheProfileRowFromTomYum()
    {
        await using var tomyum = await SeedAsync();
        await AddScoreAsync(tomyum, Play(userId: 2, RankedMapId, grade: "X", combo: 700));
        await AddScoreAsync(tomyum, Play(userId: 2, LovedMapId, grade: "SH", combo: 300));
        var old = Play(userId: 2, RankedMapId, grade: "A");
        old.Status = ScoreStatus.Submitted; // superseded: not counted in grade totals
        await AddScoreAsync(tomyum, old);
        tomyum.UserStats.Add(new UserStats
        {
            UserId = 2, Mode = 0, Pp = 412.5, Accuracy = 0.9876f, PlayCount = 3, RankedScore = 5_000_000, TotalScore = 9_000_000,
        });
        await tomyum.SaveChangesAsync();
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());

        await mirror.WriteStatsAsync(2, 0);
        await mirror.WriteStatsAsync(2, 0); // rewrite, not duplicate

        const string row = "FROM osu_user_stats WHERE user_id = 2";
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_user_stats"));
        Assert.Equal(412.5, await ScalarAsync<double>($"SELECT rank_score {row}"), precision: 1);
        Assert.Equal(98.76, await ScalarAsync<double>($"SELECT accuracy_new {row}"), precision: 2); // percent
        Assert.Equal(3, await ScalarAsync<int>($"SELECT playcount {row}"));
        Assert.Equal(5_000_000L, await ScalarAsync<long>($"SELECT ranked_score {row}"));
        Assert.Equal(1, await ScalarAsync<int>($"SELECT x_rank_count {row}"));
        Assert.Equal(1, await ScalarAsync<int>($"SELECT sh_rank_count {row}"));
        Assert.Equal(0, await ScalarAsync<int>($"SELECT a_rank_count {row}"));
        Assert.Equal(700, await ScalarAsync<int>($"SELECT max_combo {row}"));
        Assert.Equal(3 * 120L, await ScalarAsync<long>($"SELECT total_seconds_played {row}"));
        Assert.Equal("DE", await ScalarAsync<string>($"SELECT country_acronym {row}"));
        Assert.True(await ScalarAsync<double>($"SELECT level {row}") > 1);
    }

    [MySqlFact]
    public async Task GlobalRanks_OrderByPp_AndLeaveOutRestrictedAndPpless()
    {
        await using var tomyum = await SeedAsync();
        await ExecuteAsync(
            "INSERT INTO phpbb_users (user_id, username, username_clean, user_sig, user_occ, user_interests, user_warnings) VALUES " +
            "(3, 'b', 'b', '', '', '', 0), (4, 'c', 'c', '', '', '', 0), (5, 'cheater', 'cheater', '', '', '', 1)");
        await ExecuteAsync(
            "INSERT INTO osu_user_stats (user_id, accuracy_total, accuracy_count, accuracy, playcount, ranked_score, total_score, " +
            "x_rank_count, s_rank_count, a_rank_count, `rank`, level, rank_score, rank_score_index, accuracy_new) VALUES " +
            "(2, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 300, 0, 0), (3, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 500, 0, 0), " +
            "(4, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 9, 0), (5, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 900, 1, 0)");

        await new OsuWebScoreMirror(tomyum, NewOsuWebContext()).RecomputeGlobalRanksAsync(0);

        Assert.Equal(2L, await ScalarAsync<long>("SELECT rank_score_index FROM osu_user_stats WHERE user_id = 2"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT rank_score_index FROM osu_user_stats WHERE user_id = 3"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT rank_score_index FROM osu_user_stats WHERE user_id = 4"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT rank_score_index FROM osu_user_stats WHERE user_id = 5"));
    }

    [MySqlAndRedisFact]
    public async Task Sync_WritesScoreStatsRanks_AndQueuesForTheIndexer()
    {
        var redisConfig = Environment.GetEnvironmentVariable(MySqlAndRedisFactAttribute.RedisEnvVar)!;
        await using var tomyum = await SeedAsync();
        var score = await AddScoreAsync(tomyum, Play(userId: 2, RankedMapId));
        tomyum.UserStats.Add(new UserStats { UserId = 2, Mode = 0, Pp = 100, Accuracy = 0.95f, PlayCount = 1, RankedScore = 1, TotalScore = 1 });
        await tomyum.SaveChangesAsync();

        var prefix = $"test_{Guid.NewGuid():N}_";
        await using var redis = await ConnectionMultiplexer.ConnectAsync(redisConfig);
        var db = redis.GetDatabase();
        await db.SetAddAsync($"osu-queue:score-index:{prefix}active-schemas", prefix + "schema1");
        try
        {
            var sync = new OsuWebSync(
                new OsuWebScoreMirror(tomyum, NewOsuWebContext()),
                new OsuWebScoreIndexing(redis, prefix));

            await sync.ScoreSubmittedAsync(score);

            var id = await ScalarAsync<long>("SELECT id FROM scores");
            Assert.Equal(1L, await ScalarAsync<long>("SELECT rank_score_index FROM osu_user_stats WHERE user_id = 2"));
            var queued = await db.ListRangeAsync($"osu-queue:{prefix}schema1");
            Assert.Equal([$"{{\"ScoreId\":{id}}}"], queued.Select(v => v.ToString()));
        }
        finally
        {
            await db.KeyDeleteAsync([$"osu-queue:score-index:{prefix}active-schemas", $"osu-queue:{prefix}schema1"]);
        }
    }

    /// <summary>
    /// An osu-web account (id 2, DE), a ranked and a loved map on both sides, and the TomYum schema
    /// with the link table.
    /// </summary>
    private async Task<TomYumDbContext> SeedAsync()
    {
        await ExecuteAsync(
            "INSERT INTO phpbb_users (user_id, username, username_clean, user_sig, user_occ, user_interests, country_acronym) " +
            "VALUES (2, 'Player', 'player', '', '', '', 'DE')");

        var tomyum = await NewTomYumContextAsync();
        var mirror = new OsuWebBeatmapMirror(NewOsuWebContext());
        foreach (var (id, status, api) in new[] { (RankedMapId, RankedStatus.Ranked, 1), (LovedMapId, RankedStatus.Loved, 4) })
        {
            var map = new Beatmap
            {
                Id = id, SetId = id, Md5 = $"md5-{id}", Artist = "A", Title = "T", Version = "V", Creator = "C",
                Status = status, OsuApiStatus = api, TotalLength = 120, LastUpdate = DateTimeOffset.UtcNow,
            };
            tomyum.Beatmaps.Add(map);
            await mirror.WriteAsync(map);
        }
        await tomyum.SaveChangesAsync();
        await new OsuWebScoreMirror(tomyum, NewOsuWebContext()).EnsureLinkTableAsync();
        return tomyum;
    }

    private static async Task<Score> AddScoreAsync(TomYumDbContext tomyum, Score score)
    {
        tomyum.Scores.Add(score);
        await tomyum.SaveChangesAsync();
        return score;
    }

    private static Score Play(int userId, int beatmapId, long value = 1_000_000, double pp = 100, string grade = "S", int combo = 500, int mods = 0, int mode = 0, bool passed = true) => new()
    {
        UserId = userId,
        BeatmapId = beatmapId,
        BeatmapMd5 = $"md5-{beatmapId}",
        Mode = mode,
        ScoreValue = value,
        MaxCombo = combo,
        Count300 = 480,
        Count100 = 15,
        Count50 = 3,
        CountMiss = 2,
        Grade = grade,
        Mods = mods,
        Passed = passed,
        Pp = pp,
        Status = ScoreStatus.Best,
        SubmittedAt = DateTimeOffset.UtcNow,
    };
}
