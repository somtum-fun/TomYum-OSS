using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Services;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Admin;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Relax/Autopilot statistics in the website's format, for lazer's profiles in those modes.</summary>
public sealed class ModeStatsTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task RelaxStats_AreShapedLikeTheWebsites_WithRanks()
    {
        var db = await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var me = await users.AddAsync(new User { Username = "Me", SafeName = "me", Email = "m@x.dev", PasswordBcrypt = "x", Country = "nl" });
        var better = await users.AddAsync(new User { Username = "Better", SafeName = "better", Email = "b@x.dev", PasswordBcrypt = "x", Country = "de" });
        var countryman = await users.AddAsync(new User { Username = "Countryman", SafeName = "countryman", Email = "c@x.dev", PasswordBcrypt = "x", Country = "nl" });

        db.UserStats.AddRange(
            new UserStats { UserId = me.Id, Mode = 4, Pp = 100, Accuracy = 0.95f, PlayCount = 3, RankedScore = 5_000_000, TotalScore = 10_000_000 },
            new UserStats { UserId = better.Id, Mode = 4, Pp = 500, Accuracy = 0.99f, PlayCount = 9 },
            new UserStats { UserId = countryman.Id, Mode = 4, Pp = 50, Accuracy = 0.9f, PlayCount = 1 },
            new UserStats { UserId = me.Id, Mode = 0, Pp = 999 });
        db.Beatmaps.Add(new Beatmap { Id = 1, SetId = 1, Md5 = "m1", TotalLength = 120, Artist = "A", Title = "T", Version = "V", Creator = "C" });
        db.Scores.AddRange(
            new Score { UserId = me.Id, BeatmapMd5 = "m1", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Count300 = 100, Count100 = 5, CountMiss = 1, MaxCombo = 300 },
            new Score { UserId = me.Id, BeatmapMd5 = "m1", Mode = 4, Grade = "A", Status = ScoreStatus.Submitted, Passed = true, Count300 = 90, Count100 = 10, MaxCombo = 450 },
            new Score { UserId = me.Id, BeatmapMd5 = "m1", Mode = 0, Grade = "X", Status = ScoreStatus.Best, Passed = true, Count300 = 999, MaxCombo = 999 });
        await db.SaveChangesAsync();

        var result = await AdminModeStatsEndpoints.GetAsync(me.Id, 4, users, NewTomYumContext(), new RestrictedUsers(), CancellationToken.None);
        var json = await Body(result);

        Assert.Equal((2, 1), (json.GetProperty("global_rank").GetInt32(), json.GetProperty("country_rank").GetInt32()));   // behind Better; first in nl
        Assert.Equal(100, json.GetProperty("pp").GetDouble());
        Assert.Equal(95, json.GetProperty("hit_accuracy").GetDouble(), 3);
        Assert.Equal(0.95, json.GetProperty("accuracy").GetDouble(), 3);
        Assert.Equal((190, 15, 1, 450), (json.GetProperty("count_300").GetInt32(), json.GetProperty("count_100").GetInt32(), json.GetProperty("count_miss").GetInt32(), json.GetProperty("maximum_combo").GetInt32()));
        Assert.Equal((1, 0), (json.GetProperty("grade_counts").GetProperty("s").GetInt32(), json.GetProperty("grade_counts").GetProperty("a").GetInt32()));   // best scores only
        Assert.Equal(240, json.GetProperty("play_time").GetInt64());
        Assert.True(json.GetProperty("level").GetProperty("current").GetInt32() > 1);
        Assert.True(json.GetProperty("is_ranked").GetBoolean());

        var vanilla = await AdminModeStatsEndpoints.GetAsync(me.Id, 0, users, NewTomYumContext(), new RestrictedUsers(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(vanilla).StatusCode);
    }

    [MySqlFact]
    public async Task RelaxScores_ForTheProfile_Best_Recent_AndFirsts()
    {
        var db = await NewTomYumContextAsync();
        var now = DateTimeOffset.UtcNow;
        db.Scores.AddRange(
            new Score { UserId = 1, BeatmapId = 10, BeatmapMd5 = "a", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Pp = 100, Count300 = 10, SubmittedAt = now.AddDays(-3) },
            new Score { UserId = 1, BeatmapId = 11, BeatmapMd5 = "b", Mode = 4, Grade = "A", Status = ScoreStatus.Best, Passed = true, Pp = 200, Count300 = 9, Count100 = 1, SubmittedAt = now.AddHours(-2) },
            new Score { UserId = 1, BeatmapId = 11, BeatmapMd5 = "b", Mode = 4, Grade = "F", Status = ScoreStatus.Failed, Passed = false, SubmittedAt = now.AddHours(-1) },
            new Score { UserId = 2, BeatmapId = 10, BeatmapMd5 = "a", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Pp = 150, SubmittedAt = now.AddDays(-2) },
            new Score { UserId = 1, BeatmapId = 12, BeatmapMd5 = "c", Mode = 0, Grade = "X", Status = ScoreStatus.Best, Passed = true, Pp = 999, SubmittedAt = now });
        await db.SaveChangesAsync();

        async Task<string> List(string type, int offset = 0, int limit = 5, bool fails = false)
        {
            var json = await Body(await AdminModeScoresEndpoints.GetAsync(1, 4, type, offset, limit, fails, NewTomYumContext(), new RestrictedUsers(), TimeProvider.System, new ConfigurationBuilder().Build(), CancellationToken.None));
            return json.GetProperty("total").GetInt32() + ": " + string.Join(",", json.GetProperty("scores").EnumerateArray().Select(s => s.GetProperty("beatmapId").GetInt64()));
        }

        Assert.Equal("2: 11,10", await List("best"));          // by pp; the vanilla play isn't here
        Assert.Equal("2: 10", await List("best", offset: 1, limit: 1));
        Assert.Equal("1: 11", await List("recent"));            // the last day, passes only
        Assert.Equal("2: 11,11", await List("recent", fails: true));
        Assert.Equal("1: 11", await List("firsts"));            // player 2 is ahead on map 10
    }

    [MySqlFact]
    public async Task RelaxBoard_ByPp_OrByScore_WithTheLazerReplay()
    {
        var db = await NewTomYumContextAsync();
        db.Beatmaps.Add(new Beatmap { Id = 10, SetId = 1, Md5 = "a", Artist = "A", Title = "T", Version = "V", Creator = "C" });
        db.Scores.AddRange(
            new Score { UserId = 1, BeatmapId = 10, BeatmapMd5 = "a", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Pp = 300, ScoreValue = 1_000 },
            new Score { UserId = 2, BeatmapId = 10, BeatmapMd5 = "a", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Pp = 200, ScoreValue = 3_000 },
            new Score { UserId = 3, BeatmapId = 10, BeatmapMd5 = "a", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Pp = 100, ScoreValue = 2_000, FromLazer = true, OsuWebScoreId = 77 });
        await db.SaveChangesAsync();

        // The spectator server saved player 3's lazer replay (named by its website score id).
        var lazerReplays = Directory.CreateTempSubdirectory().FullName;
        await File.WriteAllBytesAsync(Path.Combine(lazerReplays, "77"), [1, 2, 3]);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Replays:LazerPath"] = lazerReplays }).Build();

        async Task<JsonElement> Board(string? sort) => await Body(await AdminModeScoresEndpoints.LeaderboardAsync(
            10, 4, 2, viewerId: 3, mods: null, sort, stableOnly: null, new TomYum.Persistence.Repositories.BeatmapRepository(NewTomYumContext()), NewTomYumContext(), new RestrictedUsers(), configuration, CancellationToken.None));
        static string Players(JsonElement board) => string.Join(",", board.GetProperty("scores").EnumerateArray().Select(e => e.GetProperty("userId").GetInt32()));

        var byPp = await Board(null);
        Assert.Equal("1,2", Players(byPp));                                          // the top 2 by pp
        Assert.Equal(3, byPp.GetProperty("userScore").GetProperty("position").GetInt32());
        var byScore = await Board("score");
        Assert.Equal("2,3", Players(byScore));
        Assert.Equal(2, byScore.GetProperty("userScore").GetProperty("position").GetInt32());
        Assert.True(byScore.GetProperty("scores")[1].GetProperty("score").GetProperty("hasReplay").GetBoolean());
        Assert.False(byScore.GetProperty("scores")[0].GetProperty("score").GetProperty("hasReplay").GetBoolean());
        // The lazer play carries its website score id (its replay's id); the stable one has none.
        Assert.Equal(77UL, byScore.GetProperty("scores")[1].GetProperty("score").GetProperty("osuWebScoreId").GetUInt64());
        Assert.Equal(JsonValueKind.Null, byScore.GetProperty("scores")[0].GetProperty("score").GetProperty("osuWebScoreId").ValueKind);
    }

    [MySqlFact]
    public async Task RelaxBoard_InStableMode_HasOnlyStablePlays()
    {
        var db = await NewTomYumContextAsync();
        db.Beatmaps.Add(new Beatmap { Id = 10, SetId = 1, Md5 = "a", Artist = "A", Title = "T", Version = "V", Creator = "C" });
        db.Scores.AddRange(
            new Score { UserId = 1, BeatmapId = 10, BeatmapMd5 = "a", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Pp = 300, ScoreValue = 1_000 },
            // Player 2's best is a lazer play (a custom DT speed, say); stable's board has their stable one.
            new Score { UserId = 2, BeatmapId = 10, BeatmapMd5 = "a", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Pp = 500, ScoreValue = 3_000, FromLazer = true, OsuWebScoreId = 77 },
            new Score { UserId = 2, BeatmapId = 10, BeatmapMd5 = "a", Mode = 4, Grade = "S", Status = ScoreStatus.Submitted, Passed = true, Pp = 200, ScoreValue = 2_000 },
            // Player 3 only played it on lazer.
            new Score { UserId = 3, BeatmapId = 10, BeatmapMd5 = "a", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Pp = 400, ScoreValue = 2_500, FromLazer = true, OsuWebScoreId = 78 });
        await db.SaveChangesAsync();

        async Task<JsonElement> Board(bool? stableOnly, int? viewerId) => await Body(await AdminModeScoresEndpoints.LeaderboardAsync(
            10, 4, 50, viewerId, mods: null, null, stableOnly, new TomYum.Persistence.Repositories.BeatmapRepository(NewTomYumContext()), NewTomYumContext(),
            new RestrictedUsers(), new ConfigurationBuilder().Build(), CancellationToken.None));
        static string Plays(JsonElement board) => string.Join(",", board.GetProperty("scores").EnumerateArray()
            .Select(e => $"{e.GetProperty("userId").GetInt32()}:{e.GetProperty("score").GetProperty("pp").GetDouble()}"));

        Assert.Equal("2:500,3:400,1:300", Plays(await Board(null, null)));     // lazer mode: everyone's best
        Assert.Equal("1:300,2:200", Plays(await Board(true, null)));           // stable mode: best stable plays
        var mine = await Board(true, 2);
        Assert.Equal(2, mine.GetProperty("userScore").GetProperty("position").GetInt32());
        Assert.Equal(200, mine.GetProperty("userScore").GetProperty("score").GetProperty("pp").GetDouble());
        Assert.False((await Board(true, 3)).TryGetProperty("userScore", out var none) && none.ValueKind != JsonValueKind.Null);
    }

    [MySqlFact]
    public async Task TopPlays_AreEveryonesBests_ByPp_WithoutRestrictedPlayers()
    {
        var db = await NewTomYumContextAsync();
        db.Scores.AddRange(
            new Score { UserId = 1, BeatmapId = 10, BeatmapMd5 = "a", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Pp = 300 },
            new Score { UserId = 1, BeatmapId = 10, BeatmapMd5 = "a", Mode = 4, Grade = "S", Status = ScoreStatus.Submitted, Passed = true, Pp = 250 },
            new Score { UserId = 2, BeatmapId = 11, BeatmapMd5 = "b", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Pp = 400, FromLazer = true, OsuWebScoreId = 5 },
            new Score { UserId = 3, BeatmapId = 10, BeatmapMd5 = "a", Mode = 4, Grade = "S", Status = ScoreStatus.Best, Passed = true, Pp = 900 },
            new Score { UserId = 4, BeatmapId = 10, BeatmapMd5 = "a", Mode = 8, Grade = "S", Status = ScoreStatus.Best, Passed = true, Pp = 800 });
        await db.SaveChangesAsync();
        var restricted = new RestrictedUsers();
        restricted.Set(3, true);

        var json = await Body(await AdminModeScoresEndpoints.TopPlaysAsync(4, null, NewTomYumContext(), restricted, new ConfigurationBuilder().Build(), CancellationToken.None));
        var plays = string.Join(",", json.GetProperty("scores").EnumerateArray()
            .Select(e => $"{e.GetProperty("position").GetInt32()}:{e.GetProperty("userId").GetInt32()}:{e.GetProperty("score").GetProperty("pp").GetDouble()}"));
        Assert.Equal("1:2:400,2:1:300", plays);   // bests only, lazer too, no restricted player, Relax only
    }

    [MySqlFact]
    public async Task RankHistory_IsSnapshotted_AndShownAsTheGraph()
    {
        var db = await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var me = await users.AddAsync(new User { Username = "Me", SafeName = "me", Email = "m@x.dev", PasswordBcrypt = "x", Country = "nl" });
        var other = await users.AddAsync(new User { Username = "Other", SafeName = "other", Email = "o@x.dev", PasswordBcrypt = "x", Country = "nl" });
        db.UserStats.AddRange(
            new UserStats { UserId = me.Id, Mode = 4, Pp = 100 },
            new UserStats { UserId = other.Id, Mode = 4, Pp = 200 });
        // Ten days ago I was first.
        db.ModeRankHistory.Add(new ModeRankSnapshot { UserId = me.Id, Mode = 4, Day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10), Rank = 1 });
        await db.SaveChangesAsync();

        using var services = new ServiceCollection().AddScoped(_ => NewTomYumContext()).BuildServiceProvider();
        var job = new ModeRankHistoryJob(services.GetRequiredService<IServiceScopeFactory>(), new RestrictedUsers(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModeRankHistoryJob>.Instance);
        Assert.Equal(2, await job.SnapshotAsync());
        Assert.Equal(2, await job.SnapshotAsync());   // again the same day: replaced, not doubled

        var json = await Body(await AdminModeStatsEndpoints.GetAsync(me.Id, 4, users, NewTomYumContext(), new RestrictedUsers(), CancellationToken.None));
        var graph = json.GetProperty("rank_history").EnumerateArray().Select(r => r.GetInt32()).ToArray();
        Assert.Equal(ModeRankHistoryJob.Days, graph.Length);
        Assert.Equal((1, 2), (graph[^11], graph[^1]));   // 10 days ago, and today
        Assert.Equal(0, graph[0]);
        Assert.Equal(1, json.GetProperty("rank_highest").GetProperty("rank").GetInt32());
    }

    private static async Task<JsonElement> Body(IResult result)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (await JsonDocument.ParseAsync(context.Response.Body)).RootElement;
    }
}