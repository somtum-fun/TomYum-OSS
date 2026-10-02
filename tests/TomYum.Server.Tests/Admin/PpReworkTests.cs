using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.Repositories;
using TomYum.Server.Admin;
using TomYum.Server.Tests.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.Admin;

/// <summary>A pp rework: nothing moves while it runs, then everything switches over at once.</summary>
public sealed class PpReworkTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task Rework_KeepsEverythingAsItWas_ThenSwitchesOverAtOnce()
    {
        await NewTomYumContextAsync();
        await using (var db = NewTomYumContext())
        {
            db.Beatmaps.Add(new Beatmap { Id = 100, SetId = 10, Md5 = "md5-100", Status = RankedStatus.Ranked, Version = "Hard", Artist = "A", Title = "T", Creator = "M" });
            // Player 1: two relax plays; the second is the better one under the new pp.
            db.Scores.Add(Score(1, 50, ScoreStatus.Best, mods: (int)TomYum.Bancho.Mods.Relax));
            db.Scores.Add(Score(1, 40, ScoreStatus.Submitted, mods: (int)(TomYum.Bancho.Mods.Relax | TomYum.Bancho.Mods.Hidden)));
            db.Scores.Add(Score(2, 30, ScoreStatus.Best, mods: (int)TomYum.Bancho.Mods.Relax));
            db.UserStats.Add(new UserStats { UserId = 1, Mode = GameMode.RelaxOffset, Pp = 50 });
            db.UserStats.Add(new UserStats { UserId = 2, Mode = GameMode.RelaxOffset, Pp = 30 });
            await db.SaveChangesAsync();
        }

        var calculator = new NewPp();
        var services = new ServiceCollection()
            .AddScoped(_ => NewTomYumContext())
            .AddScoped<IStaffLogRepository>(_ => new StaffLogRepository(NewTomYumContext()))
            .AddSingleton<IPerformanceCalculator>(calculator)
            .BuildServiceProvider();
        var reworks = new PpReworks(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<PpReworks>.Instance, TimeProvider.System);
        try
        {
            var (rework, error) = await reworks.StartAsync(new PpReworkRequest([GameMode.RelaxOffset], "akatsuki", "1.1.2", "1.1.3", "New relax formula"), actorId: 5);
            Assert.Null(error);
            Assert.True(RankingPause.IsPaused(GameMode.RelaxOffset));
            Assert.Equal(3, rework!.Total);

            // Another rework can't take the same mode.
            Assert.NotNull((await reworks.StartAsync(new PpReworkRequest([GameMode.RelaxOffset], null, null, null, null), 5)).Error);

            Assert.True(await reworks.TickAsync()); // the scores' new pp, kept aside
            await using (var db = NewTomYumContext())
            {
                Assert.Equal([50.0, 40.0, 30.0], await db.Scores.OrderBy(s => s.Id).Select(s => s.Pp).ToListAsync()); // nothing moved yet
                Assert.Equal(50, (await db.UserStats.SingleAsync(s => s.UserId == 1)).Pp);
                // A score made now waits for its pp.
                db.Scores.Add(Score(2, 99, ScoreStatus.Submitted, mods: (int)TomYum.Bancho.Mods.Relax, held: true));
                await db.SaveChangesAsync();
            }

            Assert.True(await reworks.TickAsync());  // the held score joins in
            Assert.False(await reworks.TickAsync()); // switch-over

            await using (var check = NewTomYumContext())
            {
                var scores = await check.Scores.OrderBy(s => s.Id).ToListAsync();
                Assert.Equal([100.0, 200.0, 60.0, 198.0], scores.Select(s => s.Pp));          // the new pp (double the old)
                Assert.Equal([ScoreStatus.Submitted, ScoreStatus.Best, ScoreStatus.Submitted, ScoreStatus.Best], scores.Select(s => s.Status));
                Assert.All(scores, s => Assert.False(s.PpHeld));
                Assert.InRange((await check.UserStats.SingleAsync(s => s.UserId == 1)).Pp, 200, 201);
                Assert.Equal(PpRework.Done, (await check.PpReworks.SingleAsync()).State);
                Assert.Empty(await check.PpReworkScores.ToListAsync());
            }
            Assert.False(RankingPause.IsPaused(GameMode.RelaxOffset));
        }
        finally
        {
            RankingPause.Set([]);
        }
    }

    private static Score Score(int userId, double pp, int status, int mods, bool held = false) => new()
    {
        UserId = userId,
        BeatmapId = 100,
        BeatmapMd5 = "md5-100",
        Mode = GameMode.RelaxOffset,
        ScoreValue = 1000,
        Count300 = 300,
        Grade = "S",
        Mods = mods,
        Passed = true,
        Pp = pp,
        Status = status,
        PpHeld = held,
        SubmittedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>The "new library": fixed new pp per play, by mods, in score order.</summary>
    private sealed class NewPp : IPerformanceCalculator
    {
        private readonly Dictionary<int, Queue<double>> _next = new()
        {
            [(int)TomYum.Bancho.Mods.Relax] = new([100.0, 60.0, 198.0]),
            [(int)(TomYum.Bancho.Mods.Relax | TomYum.Bancho.Mods.Hidden)] = new([200.0]),
        };

        public Task<double> CalculateAsync(PerformanceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_next[request.Mods].Dequeue());
    }
}
