using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Bancho;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Lazer's Relax plays on TomYum's relax leaderboards.</summary>
public sealed class LazerRelaxTests : OsuWebDatabaseTest
{
    [Theory]
    [InlineData(0, """{"statistics":{"great":300,"ok":10,"meh":2,"miss":1}}""", 300, 10, 2, 1, 0)]
    [InlineData(2, """{"statistics":{"great":200,"large_tick_hit":30,"small_tick_hit":90,"small_tick_miss":4,"miss":2,"large_tick_miss":1}}""", 200, 30, 90, 3, 4)]
    public void LazerStatistics_BecomeStableCounts(int ruleset, string data, int n300, int n100, int n50, int miss, int katu)
    {
        var counts = LazerRelaxImport.Counts(ruleset, data);
        Assert.Equal((n300, n100, n50, miss, katu), (counts.N300, counts.N100, counts.N50, counts.NMiss, counts.NKatu));
    }

    [Fact]
    public void Mods_AreTheStableOnes() =>
        Assert.Equal(Mods.Relax | Mods.Hidden, LazerRelaxImport.LegacyMods("""{"mods":[{"acronym":"RX"},{"acronym":"HD"},{"acronym":"CL"},{"acronym":"DA","settings":{"approach_rate":5}}]}"""));

    [MySqlFact]
    public async Task RelaxPlays_GetRelaxPp_AndARelaxBest()
    {
        await NewTomYumContextAsync();
        var player = (await new OsuWebUserRepository(NewOsuWebContext()).AddAsync(new User
        {
            Username = "Player", SafeName = "player", Email = "p@x.dev", PasswordBcrypt = "x", Country = "nl",
        })).Id;
        var osuWeb = NewOsuWebContext();
        await new OsuWebMirroringBeatmapRepository(new BeatmapRepository(NewTomYumContext()), new OsuWebBeatmapMirror(osuWeb), osuWeb).UpsertAsync(new Beatmap
        {
            Id = 100, SetId = 10, Md5 = "md5-100", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "Hard", Artist = "A", Title = "T", Creator = "M",
        });

        var calculator = new FixedPp();
        var services = new ServiceCollection()
            .AddScoped(_ => new OsuWebLazerRelax(NewOsuWebContext()))
            .AddScoped<IBeatmapRepository>(_ => new BeatmapRepository(NewTomYumContext()))
            .AddScoped<IScoreRepository>(_ => new ScoreRepository(NewTomYumContext()))
            .AddScoped<IUserStatsRepository>(_ => new UserStatsRepository(NewTomYumContext()))
            .AddSingleton<IPerformanceCalculator>(calculator)
            .BuildServiceProvider();
        var import = new LazerRelaxImport(services.GetRequiredService<IServiceScopeFactory>(), new PlayerRegistry(), NullLogger<LazerRelaxImport>.Instance);
        Assert.Equal(0, await import.TickAsync()); // starts at the newest score

        await InsertLazerAsync(player, """{"mods":[{"acronym":"HD"}],"statistics":{"great":300}}""", passed: true); // no RX: the website's
        await InsertLazerAsync(player, """{"mods":[{"acronym":"RX"}],"statistics":{"great":290,"ok":10}}""", passed: true);
        await InsertLazerAsync(player, """{"mods":[{"acronym":"RX"}],"statistics":{"great":50,"miss":3}}""", passed: false);
        Assert.Equal(2, await import.TickAsync());
        Assert.Equal(0, await import.TickAsync()); // not taken twice

        var scores = new ScoreRepository(NewTomYumContext());
        var best = await scores.GetPersonalBestAsync(player, "md5-100", GameMode.RelaxOffset);
        Assert.NotNull(best);
        Assert.Equal((123.0, 290, 10, (int)Mods.Relax), (best!.Pp, best.Count300, best.Count100, best.Mods));
        Assert.Equal((int)(Mods.Relax), calculator.LastMods);

        var stats = await new UserStatsRepository(NewTomYumContext()).GetAsync(player, GameMode.RelaxOffset);
        Assert.Equal(2, stats!.PlayCount);
        Assert.InRange(stats.Pp, 123, 124); // the play, plus osu!'s small bonus for the number of plays
        Assert.Null(await new UserStatsRepository(NewTomYumContext()).GetAsync(player, 0)); // vanilla untouched
    }

    private Task InsertLazerAsync(int userId, string data, bool passed) => ExecuteAsync($$"""
        INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, max_combo, total_score, data, pp, ended_at)
        VALUES ({{userId}}, 0, 100, 1, 0, '{{(passed ? "S" : "F")}}', {{(passed ? 1 : 0)}}, 0.98, 400, 900000, '{{data}}', NULL, UTC_TIMESTAMP())
        """);

    private sealed class FixedPp : IPerformanceCalculator
    {
        public int LastMods { get; private set; }

        public Task<double> CalculateAsync(PerformanceRequest request, CancellationToken cancellationToken = default)
        {
            LastMods = request.Mods;
            return Task.FromResult(123.0);
        }
    }
}
