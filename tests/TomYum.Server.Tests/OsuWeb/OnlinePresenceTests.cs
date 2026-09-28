using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Who's online across clients (<see cref="OnlinePlayers"/>, <see cref="LazerPresence"/>), the website's online statistics, featured artist tracks.</summary>
public sealed class OnlinePresenceTests : OsuWebDatabaseTest
{
    [Fact]
    public void TheLazerFeed_IsRead_AndBadEntriesSkipped()
    {
        var players = LazerPresence.Parse(
        [
            ("8", """{"status":"Online","activity":"InSoloGame","text":"Playing","details":"DragonForce - Operation Ground and Pound [Legend]","beatmap_id":720314,"ruleset_id":0}"""),
            ("9", """{"status":"DoNotDisturb","activity":"ChoosingBeatmap","text":"Choosing a beatmap","details":null,"beatmap_id":null,"ruleset_id":null}"""),
            ("x", "{}"),
            ("10", "not json"),
        ]);

        Assert.Equal([8, 9], players.Select(p => p.UserId));
        Assert.True(players[0].IsPlaying);
        Assert.Equal("Playing: DragonForce - Operation Ground and Pound [Legend]", players[0].Describe());
        Assert.Equal("Choosing a beatmap", players[1].Describe());
        Assert.False(players[1].IsPlaying);
    }

    [MySqlAndRedisFact]
    public async Task Everyone_IsListedOnce_StableThenLazerThenWebsite_AndTheStatsRowCountsGameClients()
    {
        await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var stable = await users.AddAsync(NewUser("Stable"));
        var both = await users.AddAsync(NewUser("Both"));
        var lazer = await users.AddAsync(NewUser("Lazer"));
        var browsing = await users.AddAsync(NewUser("Browsing"));
        var hidden = await users.AddAsync(NewUser("Hidden"));
        await ExecuteAsync("UPDATE phpbb_users SET user_lastvisit = 0");
        await ExecuteAsync($"UPDATE phpbb_users SET user_lastvisit = UNIX_TIMESTAMP() WHERE user_id IN ({browsing.Id}, {lazer.Id})");
        await ExecuteAsync($"UPDATE phpbb_users SET user_warnings = 1 WHERE user_id = {hidden.Id}");
        await ExecuteAsync("INSERT INTO multiplayer_rooms (name, type, ends_at) VALUES ('open', 'head_to_head', NULL), ('closed', 'team_versus', '2020-01-01'), ('playlist', 'playlists', NULL)");

        var registry = new PlayerRegistry();
        registry.Add(new BanchoPresence { UserId = stable.Id, Username = "Stable" });
        registry.Add(new BanchoPresence { UserId = both.Id, Username = "Both" });
        var matches = new MatchRegistry();
        matches.Create(stable.Id, "room", string.Empty, "map", 1, "md5", 0, 0, 0, false, 0, 0);

        await using var redis = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable(MySqlAndRedisFactAttribute.RedisEnvVar)!);
        var database = redis.GetDatabase();
        await database.HashSetAsync(LazerPresence.Key,
        [
            new HashEntry(both.Id, """{"status":"Online","activity":"ChoosingBeatmap","text":"Choosing a beatmap"}"""),
            new HashEntry(lazer.Id, """{"status":"Online","activity":"InSoloGame","text":"Playing","details":"A - B [C]","beatmap_id":5,"ruleset_id":1}"""),
            new HashEntry(hidden.Id, """{"status":"Online","activity":null,"text":null}"""),
        ]);
        try
        {
            var services = new ServiceCollection()
                .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
                .AddScoped(_ => NewOsuWebContext())
                .BuildServiceProvider();
            var scopes = services.GetRequiredService<IServiceScopeFactory>();
            var online = new OnlinePlayers(registry, scopes, new LazerPresence(NullLogger<LazerPresence>.Instance, redis));

            var list = await online.GetAsync();
            Assert.Equal(
                [("Both", OnlineClient.Stable), ("Stable", OnlineClient.Stable), ("Lazer", OnlineClient.Lazer), ("Browsing", OnlineClient.Website)],
                list.Select(p => (p.Username, p.Client)));
            var onLazer = list.Single(p => p.Client == OnlineClient.Lazer);
            Assert.Equal(("Playing: A - B [C]", 5, 1, true), (onLazer.Doing, onLazer.BeatmapId, onLazer.Mode, onLazer.Playing));

            var job = new BanchoStatsJob(
                new ServiceCollection().AddSingleton(online).AddScoped(_ => NewOsuWebContext()).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                matches, NullLogger<BanchoStatsJob>.Instance);
            Assert.Equal((2, 1, 1, 1), await job.RecordAsync());
            Assert.Equal(3L, await ScalarAsync<long>("SELECT users_osu + users_lazer FROM osu_banchostats"));
        }
        finally
        {
            await database.KeyDeleteAsync(LazerPresence.Key);
        }
    }

    [Fact]
    public async Task Imports_ReadTheFeaturedArtistTrack()
    {
        var handler = new ApiHandler();
        var api = new OsuApiV2Client(
            new HttpClient(handler) { BaseAddress = new Uri("https://osu.test/") },
            new OsuApiOptions { ClientId = "1", ClientSecret = "s" },
            NullLogger<OsuApiV2Client>.Instance);

        Assert.Equal(4231, (await api.GetBeatmapsetExtrasAsync(100))!.TrackId);
        Assert.Null((await api.GetBeatmapsetExtrasAsync(200))!.TrackId);
    }

    private static User NewUser(string name) => new()
    {
        Username = name,
        SafeName = User.MakeSafeName(name),
        Email = $"{name.ToLowerInvariant()}@x.dev",
        PasswordBcrypt = "x",
        Country = "nl",
    };

    /// <summary>osu! API v2 with two sets: 100 is a featured artist's song, 200 isn't.</summary>
    private sealed class ApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/oauth/token" => """{"access_token":"t","expires_in":3600}""",
                "/api/v2/beatmapsets/100" => """{"id":100,"track_id":4231,"current_nominations":[],"related_users":[]}""",
                "/api/v2/beatmapsets/200" => """{"id":200,"track_id":null,"current_nominations":[],"related_users":[]}""",
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
