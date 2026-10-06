using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary><c>/np</c> and BanchoBot's <c>!map</c> (<see cref="MapCommands"/>, <see cref="NowPlaying"/>).</summary>
public sealed class MapCommandTests : OsuWebDatabaseTest
{
    [Theory]
    [InlineData("\u0001ACTION is listening to [https://osu.ppy.sh/beatmapsets/41823#/129891 Artist - Title [Hard]]\u0001", 129891, 41823, 0, Mods.None)]
    [InlineData("\u0001ACTION is playing [https://osu.sundei.eu/beatmapsets/1#osu/2 A - B [C]] +Hidden +DoubleTime\u0001", 2, 1, 0, Mods.Hidden | Mods.DoubleTime)]
    [InlineData("\u0001ACTION is playing [https://osu.ppy.sh/beatmapsets/1#taiko/2 A - B [C]] <Taiko> +Nightcore ~Relax~\u0001", 2, 1, 1, Mods.Nightcore | Mods.DoubleTime | Mods.Relax)]
    [InlineData("\u0001ACTION is watching [https://osu.ppy.sh/beatmapsets/1#/2 A - B [C [2]]] <osu!mania> -Easy |Autopilot|\u0001", 2, 1, 3, Mods.Easy | Mods.Autopilot)]
    [InlineData("\u0001ACTION is editing [https://osu.ppy.sh/b/129891 Artist - Title]\u0001", 129891, 0, 0, Mods.None)]
    [InlineData("\u0001ACTION is listening to [https://osu.ppy.sh/beatmapsets/41823 Artist - Title]\u0001", 0, 41823, 0, Mods.None)]
    [InlineData("\u0001ACTION is playing [https://osu.ppy.sh/beatmapsets/1#/2 A - B [C]] <CatchTheBeat> +Perfect\u0001", 2, 1, 2, Mods.Perfect | Mods.SuddenDeath)]
    public void NowPlaying_IsReadFromEveryKindOfNp(string text, int beatmapId, int setId, int mode, Mods mods) =>
        Assert.Equal(new NowPlaying(beatmapId, setId, mode, mods), NowPlaying.TryParse(text));

    [Theory]
    [InlineData("is listening to [https://osu.sundei.eu/b/4622078 Camellia - Parallel Universe Shifter (iLyne) [To Infinity and Beyond]]", 4622078, 0, 0, Mods.None)]
    [InlineData("is playing [https://osu.sundei.eu/b/5 A - B [C]] <osu!taiko> +HD +DT ", 5, 0, 1, Mods.Hidden | Mods.DoubleTime)]
    [InlineData("is playing [https://osu.sundei.eu/b/5 A - B [C]] <osu!> +NC -EZ", 5, 0, 0, Mods.Nightcore | Mods.DoubleTime | Mods.Easy)]
    [InlineData("is playing [https://osu.sundei.eu/b/5 A - B [C]] <osu!catch>", 5, 0, 2, Mods.None)]
    [InlineData("is editing [https://osu.sundei.eu/b/5 A - B [C]]", 5, 0, 0, Mods.None)]
    public void LazerNp_IsReadToo(string text, int beatmapId, int setId, int mode, Mods mods) =>
        Assert.Equal(new NowPlaying(beatmapId, setId, mode, mods), NowPlaying.TryParseAction(text));

    [Theory]
    [InlineData("hello")]
    [InlineData("\u0001ACTION waves\u0001")]
    [InlineData("\u0001ACTION is listening to [https://example.com/nothing here]\u0001")]
    [InlineData("is playing [https://osu.ppy.sh/b/1 x]")]
    public void OtherMessages_AreNotNowPlaying(string text) => Assert.Null(NowPlaying.TryParse(text));

    [Fact]
    public void ModNames_DontRepeatImpliedMods() =>
        Assert.Equal("HDPFNC", MapCommands.ModNames(Mods.Hidden | Mods.Nightcore | Mods.DoubleTime | Mods.Perfect | Mods.SuddenDeath));

    [MySqlFact]
    public async Task Map_ChangesTheNpdSet_ForNominatorsOnly()
    {
        var (maps, services, player, nominator, store) = await SetUpAsync();
        var bn = new PlayerRegistry().Add(new BanchoPresence { UserId = nominator, Username = "Nominator" });

        Assert.StartsWith("Usage:", await maps.RunAsync(bn, []));
        Assert.StartsWith("Usage:", await maps.RunAsync(bn, ["destroy"]));
        Assert.Equal("/np the map first.", await maps.RunAsync(bn, ["love"]));
        Assert.Equal("That command is for the Beatmap Nominators, NAT and admins.", await maps.RunAsync(player, ["love"]));

        store.Set(nominator, new NowPlaying(101, 10, 0, Mods.None));
        Assert.Equal("Artist - Song: all difficulties → loved.", await maps.RunAsync(bn, ["love"]));
        Assert.Equal("Nothing changed: Artist - Song already has loved.", await maps.RunAsync(bn, ["loved", "set"]));
        Assert.Equal("Artist - Song: [Insane] → qualified.", await maps.RunAsync(bn, ["qualify", "map"]));

        // Ranking directly (no qualified period) is the NAT's and admins'.
        Assert.StartsWith("Only the NAT and admins rank directly.", await maps.RunAsync(bn, ["rank", "map"]));
        Assert.StartsWith("Only the NAT and admins rank directly.", await maps.RunAsync(bn, ["approve"]));
        await ExecuteAsync($"INSERT INTO phpbb_user_group (group_id, user_id, group_leader, user_pending) SELECT group_id, {nominator}, 0, 0 FROM phpbb_groups WHERE identifier = 'nat'");
        Assert.Equal("Artist - Song: [Insane] → ranked.", await maps.RunAsync(bn, ["rank", "map"]));

        using (var scope = services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IBeatmapRepository>();
            Assert.Equal(RankedStatus.Loved, (await repository.GetByIdAsync(100))!.Status);
            Assert.Equal(RankedStatus.Ranked, (await repository.GetByIdAsync(101))!.Status);
            var log = await scope.ServiceProvider.GetRequiredService<IStaffLogRepository>().ListAsync(new StaffLogQuery("beatmapset.status"));
            Assert.Equal(3, log.Count);
            Assert.All(log, e => Assert.Equal(nominator, e.ActorId));
        }

        // BNs on probation nominate on the website only.
        await ExecuteAsync($"INSERT INTO phpbb_user_group (group_id, user_id, group_leader, user_pending) SELECT group_id, {player.UserId}, 0, 0 FROM phpbb_groups WHERE identifier = 'bng_limited'");
        Assert.StartsWith("Probationary BNs nominate on the website", await maps.RunAsync(player, ["qualify"]));

        // The (fake) osu! API doesn't know the set: there's no bancho status to go back to.
        Assert.Equal("Nothing to reset: Artist - Song has no staff-set status, or osu! doesn't have it.", await maps.RunAsync(bn, ["reset"]));
    }

    [MySqlFact]
    public async Task NpReply_ShowsTheMap_ItsStatus_AndPp()
    {
        var (maps, _, _, _, _) = await SetUpAsync();

        var reply = await maps.DescribeAsync(new NowPlaying(101, 10, 0, Mods.Hidden | Mods.DoubleTime));
        Assert.Equal("[https://osu.test.local/b/101 Artist - Song [Insane]] +HDDT | ★5.50 | Unranked | 95%: 95pp | 98%: 98pp | 99%: 99pp | 100%: 100pp", reply);
        Assert.StartsWith("I don't know that map", await maps.DescribeAsync(new NowPlaying(999, 0, 0, Mods.None)));
    }

    private async Task<(MapCommands, IServiceProvider, OnlinePlayer Player, int Nominator, NowPlayingStore Store)> SetUpAsync()
    {
        await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var player = await users.AddAsync(new User { Username = "Player", SafeName = "player", Email = "p@x.dev", PasswordBcrypt = "x", Country = "nl" });
        var nominator = await users.AddAsync(new User { Username = "Nominator", SafeName = "nominator", Email = "n@x.dev", PasswordBcrypt = "x", Country = "nl" });
        await ExecuteAsync($"INSERT INTO phpbb_user_group (group_id, user_id, group_leader, user_pending) SELECT group_id, {nominator.Id}, 0, 0 FROM phpbb_groups WHERE identifier = 'bng'");

        var beatmapService = new BeatmapService(new NoApi());
        var services = new ServiceCollection()
            .AddSingleton(beatmapService)
            .AddSingleton(TimeProvider.System)
            .AddScoped(_ => NewTomYumContext())
            .AddScoped<IBeatmapRepository>(sp => new BeatmapRepository(sp.GetRequiredService<TomYum.Persistence.TomYumDbContext>()))
            .AddScoped<IStaffLogRepository>(sp => new StaffLogRepository(sp.GetRequiredService<TomYum.Persistence.TomYumDbContext>()))
            .AddScoped<IUserRepository>(_ => new OsuWebUserRepository(NewOsuWebContext()))
            .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
            .AddScoped<IPerformanceCalculator, FakePerformance>()
            .AddScoped(sp => new BeatmapStatusChanger(
                beatmapService, sp.GetRequiredService<IBeatmapRepository>(), sp.GetRequiredService<IStaffLogRepository>(),
                TimeProvider.System, NullLogger<BeatmapStatusChanger>.Instance))
            .BuildServiceProvider();

        using (var scope = services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IBeatmapRepository>();
            await repository.UpsertAsync(new Beatmap { Id = 100, SetId = 10, Md5 = "md5-100", Status = RankedStatus.Pending, Version = "Hard", Artist = "Artist", Title = "Song", Creator = "Mapper", StarRating = 4.5 });
            await repository.UpsertAsync(new Beatmap { Id = 101, SetId = 10, Md5 = "md5-101", Status = RankedStatus.Pending, Version = "Insane", Artist = "Artist", Title = "Song", Creator = "Mapper", StarRating = 5.5 });
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [BanchoHosts.DomainConfigKey] = "test.local" }).Build();
        var store = new NowPlayingStore();
        var maps = new MapCommands(services.GetRequiredService<IServiceScopeFactory>(), beatmapService, store, configuration);
        var online = new PlayerRegistry().Add(new BanchoPresence { UserId = player.Id, Username = "Player" });
        store.Set(player.Id, new NowPlaying(100, 10, 0, Mods.None));
        return (maps, services, online, nominator.Id, store);
    }

    /// <summary>pp = accuracy × 100, so the reply's numbers are predictable.</summary>
    private sealed class FakePerformance : IPerformanceCalculator, IAccuracyPerformance
    {
        public Task<double> CalculateAsync(PerformanceRequest request, CancellationToken cancellationToken = default) => Task.FromResult(0.0);

        public Task<IReadOnlyList<double>?> ForAccuraciesAsync(int beatmapId, int rulesetId, int mods, IReadOnlyList<double> accuracies, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<double>?>(accuracies.Select(a => a * 100).ToList());

        public Task<double?> StarRatingAsync(int beatmapId, int rulesetId, int mods, CancellationToken cancellationToken = default) => Task.FromResult<double?>(null);
    }

    private sealed class NoApi : IOsuApiClient
    {
        public bool SupportsSince => false;

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);
    }
}
