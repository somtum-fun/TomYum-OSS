using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Services;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Recalculating scores after staff change a map's status, against real databases.</summary>
public sealed class ScoreRecalculationTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task Ranking_GivesPp_PicksBests_AndUpdatesStatsAndTheWebsite()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var player = await new OsuWebUserRepository(NewOsuWebContext()).AddAsync(new User
        {
            Username = "Recalc", SafeName = "recalc", Email = "recalc@example.com", PasswordBcrypt = "x", Country = "th",
        });
        var map = new Beatmap { Id = 1, SetId = 1, Md5 = "md5-1", Status = RankedStatus.Pending, OsuApiStatus = 0, Version = "x", Artist = "a", Title = "t", Creator = "c" };
        await new OsuWebBeatmapMirror(NewOsuWebContext()).WriteAsync(map);

        // On the pending map the first pass became the best (no pp to compare).
        var first = Play(player.Id, 500_000, ScoreStatus.Best);
        var better = Play(player.Id, 900_000, ScoreStatus.Submitted);
        tomyum.Scores.AddRange(first, better, Play(player.Id, 1_000_000, ScoreStatus.Failed, passed: false));
        tomyum.UserStats.Add(new UserStats { UserId = player.Id, Mode = 0, PlayCount = 3, RankedScore = 500_000 });
        await tomyum.SaveChangesAsync();
        tomyum.ChangeTracker.Clear();
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());
        await mirror.EnsureLinkTableAsync();
        await mirror.WriteScoreAsync(first);
        await mirror.WriteScoreAsync(better);

        // Staff rank it (the website's map already says ranked).
        await ExecuteAsync("UPDATE osu_beatmaps SET approved = 1 WHERE beatmap_id = 1");
        map.Status = RankedStatus.Ranked;
        var result = await NewRecalculation(tomyum, mirror).RecalculateAsync([map]);

        Assert.Equal((2, 2, 1), (result.Scores, result.Changed, result.Players));
        var scores = await tomyum.Scores.AsNoTracking().Where(s => s.Passed).OrderBy(s => s.ScoreValue).ToListAsync();
        Assert.Equal([500.0, 900.0], scores.Select(s => s.Pp));
        Assert.Equal([ScoreStatus.Submitted, ScoreStatus.Best], scores.Select(s => s.Status));
        var stats = await tomyum.UserStats.AsNoTracking().SingleAsync();
        Assert.Equal(900_000, stats.RankedScore);
        Assert.Equal(900, stats.Pp, 0);
        Assert.Equal(3, stats.PlayCount);
        Assert.Equal(900f, await ScalarAsync<float>($"SELECT pp FROM scores WHERE total_score = 900000"));
        Assert.True(await ScalarAsync<bool>($"SELECT ranked FROM scores WHERE total_score = 900000"));

        // And back to pending: no pp anywhere, the website's copies stop being ranked.
        await ExecuteAsync("UPDATE osu_beatmaps SET approved = 0 WHERE beatmap_id = 1");
        map.Status = RankedStatus.Pending;
        await NewRecalculation(tomyum, mirror).RecalculateAsync([map]);

        Assert.All(await tomyum.Scores.AsNoTracking().ToListAsync(), s => Assert.Equal(0, s.Pp));
        Assert.Equal(0, (await tomyum.UserStats.AsNoTracking().SingleAsync()).Pp);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM scores WHERE ranked = 1 OR pp IS NOT NULL"));
    }

    [MySqlFact]
    public async Task Clearing_DeletesEveryScoreOnTheMap_HereAndOnTheWebsite()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var player = await new OsuWebUserRepository(NewOsuWebContext()).AddAsync(new User
        {
            Username = "Cleared", SafeName = "cleared", Email = "cleared@example.com", PasswordBcrypt = "x", Country = "th",
        });
        var map = new Beatmap { Id = 1, SetId = 1, Md5 = "md5-1", Status = RankedStatus.Qualified, OsuApiStatus = 0, Version = "x", Artist = "a", Title = "t", Creator = "c" };
        var other = new Beatmap { Id = 2, SetId = 2, Md5 = "md5-2", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "y", Artist = "a", Title = "u", Creator = "c" };
        await new OsuWebBeatmapMirror(NewOsuWebContext()).WriteAsync(map);
        await new OsuWebBeatmapMirror(NewOsuWebContext()).WriteAsync(other);

        // Vanilla, relax and autopilot plays (and a fail) on the map, and a ranked play elsewhere.
        var vanilla = Play(player.Id, 500_000, ScoreStatus.Best);
        var relax = Play(player.Id, 600_000, ScoreStatus.Best, mode: GameMode.RelaxOffset);
        var autopilot = Play(player.Id, 700_000, ScoreStatus.Best, mode: GameMode.AutopilotOffset);
        var elsewhere = Play(player.Id, 800_000, ScoreStatus.Best, beatmapId: 2);
        elsewhere.Pp = 800;
        tomyum.Scores.AddRange(vanilla, relax, autopilot, Play(player.Id, 1_000, ScoreStatus.Failed, passed: false), elsewhere);
        tomyum.UserStats.Add(new UserStats { UserId = player.Id, Mode = 0, PlayCount = 5, RankedScore = 1_300_000, Pp = 900 });
        await tomyum.SaveChangesAsync();
        tomyum.ChangeTracker.Clear();
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());
        await mirror.EnsureLinkTableAsync();
        await mirror.WriteScoreAsync(vanilla);
        await mirror.WriteScoreAsync(elsewhere);

        // On the website: a lazer play, and one a multiplayer room links (kept for the room, off the board).
        const string Lazer = """
            INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, max_combo, total_score, data, pp, ended_at)
            VALUES ({0}, 0, 1, 1, 1, 'S', 1, 0.97, 400, {1}, '{{}}', 100, UTC_TIMESTAMP())
            """;
        await ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, Lazer, player.Id, 900_000));
        await ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, Lazer, player.Id, 950_000));
        var roomScore = await ScalarAsync<ulong>("SELECT MAX(id) FROM scores");
        await ExecuteAsync($"INSERT INTO multiplayer_score_links (user_id, playlist_item_id, score_id) VALUES ({player.Id}, 1, {roomScore})");
        await ExecuteAsync("INSERT INTO beatmap_leaders (score_id, beatmap_id, ruleset_id, user_id) VALUES (1, 1, 0, 4)");

        var result = await NewRecalculation(tomyum, mirror).ClearLeaderboardsAsync([map]);

        Assert.Equal((4, 2), (result.Scores, result.WebsiteScores));
        Assert.Equal(["md5-2"], await tomyum.Scores.AsNoTracking().Select(s => s.BeatmapMd5).ToListAsync());
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM scores WHERE beatmap_id = 1"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM scores WHERE beatmap_id = 1 AND (ranked = 1 OR pp IS NOT NULL)"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM scores WHERE beatmap_id = 2"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM beatmap_leaders WHERE beatmap_id = 1"));
        // The player's stats follow from what's left; their plays still count.
        var stats = await tomyum.UserStats.AsNoTracking().SingleAsync(s => s.Mode == 0);
        Assert.Equal((800_000L, 5), (stats.RankedScore, stats.PlayCount));
    }

    [MySqlFact]
    public async Task StatusChanges_ClearTheLeaderboard_OnlyWhenTheMapBecomesRanked()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var player = await new OsuWebUserRepository(NewOsuWebContext()).AddAsync(new User
        {
            Username = "Ranker", SafeName = "ranker", Email = "ranker@example.com", PasswordBcrypt = "x", Country = "th",
        });
        var repository = new BeatmapRepository(tomyum);
        await repository.UpsertAsync(new Beatmap { Id = 1, SetId = 1, Md5 = "md5-1", Status = RankedStatus.Pending, OsuApiStatus = -2, Version = "x", Artist = "a", Title = "t", Creator = "c" });
        tomyum.Scores.AddRange(Play(player.Id, 500_000, ScoreStatus.Best), Play(player.Id, 400_000, ScoreStatus.Submitted));
        await tomyum.SaveChangesAsync();
        tomyum.ChangeTracker.Clear();
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());
        await mirror.EnsureLinkTableAsync();

        BeatmapStatusChanger Changer(bool clear) => new(
            new BeatmapService(new NoApi()), repository, new StaffLogRepository(tomyum), TimeProvider.System,
            NullLogger<BeatmapStatusChanger>.Instance, NewRecalculation(tomyum, mirror),
            configuration: new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [BeatmapStatusChanger.ClearScoresOnRankKey] = clear ? "true" : "false" }).Build());

        // Qualified and loved keep the scores; ranked from loved starts empty.
        Assert.Null((await Changer(clear: true).ChangeAsync(1, RankedStatus.Qualified, null, player.Id))!.Cleared);
        Assert.Null((await Changer(clear: true).ChangeAsync(1, RankedStatus.Loved, null, player.Id))!.Cleared);
        Assert.Equal(2, await tomyum.Scores.CountAsync());
        var ranked = await Changer(clear: true).ChangeAsync(1, RankedStatus.Ranked, null, player.Id);
        Assert.Equal(2, ranked!.Cleared!.Scores);
        Assert.Equal(0, await tomyum.Scores.CountAsync());

        // Ranked to approved isn't becoming ranked: new plays stay.
        tomyum.Scores.Add(Play(player.Id, 600_000, ScoreStatus.Best));
        await tomyum.SaveChangesAsync();
        tomyum.ChangeTracker.Clear();
        Assert.Null((await Changer(clear: true).ChangeAsync(1, RankedStatus.Approved, null, player.Id))!.Cleared);
        Assert.Equal(1, await tomyum.Scores.CountAsync());

        // Turned off, ranking keeps them (and gives them pp).
        await Changer(clear: false).ChangeAsync(1, RankedStatus.Pending, null, player.Id);
        Assert.Null((await Changer(clear: false).ChangeAsync(1, RankedStatus.Ranked, null, player.Id))!.Cleared);
        Assert.Equal(600.0, (await tomyum.Scores.AsNoTracking().SingleAsync()).Pp);
    }

    private static ScoreRecalculation NewRecalculation(TomYumDbContext tomyum, OsuWebScoreMirror mirror) => new(
        tomyum, new ComboPp(), new UserStatsRepository(tomyum, new RestrictedUsers()), new PlayerRegistry(),
        NullLogger<ScoreRecalculation>.Instance, mirror);

    private static Score Play(int userId, long value, int status, bool passed = true, int mode = 0, int beatmapId = 1) => new()
    {
        UserId = userId,
        BeatmapId = beatmapId,
        BeatmapMd5 = $"md5-{beatmapId}",
        Mode = mode,
        ScoreValue = value,
        MaxCombo = (int)(value / 1000),
        Count300 = 480,
        Grade = passed ? "S" : "F",
        Passed = passed,
        Status = status,
        SubmittedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>pp = max combo, which <see cref="Play"/> sets to score / 1000.</summary>
    private sealed class ComboPp : IPerformanceCalculator
    {
        public Task<double> CalculateAsync(PerformanceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult((double)request.MaxCombo);
    }

    private sealed class NoApi : IOsuApiClient
    {
        public bool SupportsSince => false;

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);
    }
}
