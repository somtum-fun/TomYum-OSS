using Microsoft.EntityFrameworkCore;
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

    private static ScoreRecalculation NewRecalculation(TomYumDbContext tomyum, OsuWebScoreMirror mirror) => new(
        tomyum, new ComboPp(), new UserStatsRepository(tomyum, new RestrictedUsers()), new PlayerRegistry(),
        NullLogger<ScoreRecalculation>.Instance, mirror);

    private static Score Play(int userId, long value, int status, bool passed = true) => new()
    {
        UserId = userId,
        BeatmapId = 1,
        BeatmapMd5 = "md5-1",
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
}
