using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Bancho;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>
/// Stable shows what the website and lazer show: the panel's numbers come from the website, and
/// stable plays get the same "achieved rank #N" activity and #1 announcements as lazer plays.
/// </summary>
public sealed class StableParityTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task ThePanel_ShowsTheWebsitesNumbers_InTheVanillaModes()
    {
        var tomyum = await NewTomYumContextAsync();
        var player = await AddUserAsync("Player");
        var stats = new UserStatsRepository(tomyum, new TomYum.Domain.Services.RestrictedUsers());
        var own = await stats.GetOrCreateAsync(player, 0);
        own.TotalScore = 999_999_999; // stable-only classic totals: a much higher level than the website's
        own.RankedScore = 5;
        own.PlayCount = 3;
        own.Pp = 700.2;
        await stats.UpdateAsync(own);
        var relax = await stats.GetOrCreateAsync(player, 4);
        relax.Pp = 321.4;
        relax.TotalScore = 1234;
        await stats.UpdateAsync(relax);
        await ExecuteAsync($"INSERT INTO osu_user_stats (user_id, accuracy_total, accuracy_count, accuracy, playcount, ranked_score, total_score, x_rank_count, s_rank_count, a_rank_count, `rank`, level, rank_score, rank_score_index, accuracy_new, country_acronym) VALUES ({player}, 0, 0, 0.94, 12, 25722552, 42731411, 0, 0, 0, 0, 18, 737.6, 0, 94.07, 'NL')");

        var filler = new PresenceStats(stats, new OsuWebScoreMirror(tomyum, NewOsuWebContext()));
        var presence = new BanchoPresence { UserId = player, Username = "Player" };
        Assert.True(await filler.FillAsync(presence, player, 0));
        Assert.Equal((25722552L, 42731411L, 12, (short)738), (presence.RankedScore, presence.TotalScore, presence.PlayCount, presence.Performance));
        Assert.Equal(0.9407f, presence.Accuracy, 4);

        // The website's rank too (TomYum's own ranking only knows the pp it last copied).
        await ExecuteAsync($"UPDATE osu_user_stats SET rank_score_index = 2 WHERE user_id = {player}");
        Assert.True(await filler.FillAsync(presence, player, 0));
        Assert.Equal(2, presence.GlobalRank);

        // Relax is TomYum's own.
        Assert.True(await filler.FillAsync(presence, player, 4));
        Assert.Equal((1234L, (short)321), (presence.TotalScore, presence.Performance));

        // Without the website: TomYum's numbers, pp rounded the same way.
        Assert.True(await new PresenceStats(stats).FillAsync(presence, player, 0));
        Assert.Equal((999_999_999L, (short)700), (presence.TotalScore, presence.Performance));
    }

    [MySqlFact]
    public async Task NewBests_GetTheirPlace_AndFirstPlacesAreAnnounced()
    {
        await NewTomYumContextAsync();
        var alpha = await AddUserAsync("Alpha");
        var bravo = await AddUserAsync("Bravo");
        await new OsuWebBeatmapMirror(NewOsuWebContext()).WriteAsync(new Beatmap
        {
            Id = 100, SetId = 10, Md5 = "a", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "Hard", Artist = "Artist", Title = "Song", Creator = "M",
        });

        var interop = new RecordingInterop();
        var channels = new ChannelRegistry();
        var listener = new PlayerRegistry().Add(new BanchoPresence { UserId = 50, Username = "Listener" });
        channels.Get("#announce")!.Join(listener);
        var services = new ServiceCollection()
            .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
            .AddScoped(_ => new OsuWebInterop(new HttpClient(interop), new OsuWebInteropOptions { InteropUrl = "http://osu-web", InteropSecret = "s" }))
            .BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Domain"] = "tomyum.test" }).Build();
        var events = new ScoreRankEvents(services.GetRequiredService<IServiceScopeFactory>(), channels, config, NullLogger<ScoreRankEvents>.Instance);
        await events.TickAsync(); // notes where to start

        await InsertAsync(alpha, 800_000, "A", stable: true);                   // #1 (nobody before)
        await InsertAsync(bravo, 700_000, "B", stable: false);                  // #2
        await InsertAsync(alpha, 600_000, "C", stable: true);                   // not Alpha's best: nothing
        await InsertAsync(bravo, 900_000, "S", stable: false);                  // #1, Alpha loses first place
        await InsertAsync(alpha, 100_000, "D", stable: true, passed: false);    // failed: nothing
        await events.TickAsync();

        Assert.Equal(
        [
            $"/_lio/users/{alpha}/100/0/rank-achieved?position_after=1&rank=A&legacy_score_event=1",
            $"/_lio/users/{bravo}/100/0/rank-achieved?position_after=2&rank=B&legacy_score_event=0",
            $"/_lio/users/{bravo}/100/0/rank-achieved?position_after=1&rank=S&legacy_score_event=0",
            $"/_lio/users/{alpha}/100/0/first-place-lost?legacy_score_event=0",
        ], interop.Paths);

        // BanchoBot said both #1s in #announce, with links.
        var said = System.Text.Encoding.UTF8.GetString(listener.DequeueAll());
        Assert.Contains($"[https://osu.tomyum.test/u/{alpha} Alpha] achieved rank #1 on [https://osu.tomyum.test/b/100 Artist - Song [Hard]] (osu!)", said, StringComparison.Ordinal);
        Assert.Contains("Bravo] achieved rank #1", said, StringComparison.Ordinal);
    }

    [MySqlFact]
    public async Task EarlierPlays_GetTheirEntries_Once()
    {
        await NewTomYumContextAsync();
        var alpha = await AddUserAsync("Alpha");
        var bravo = await AddUserAsync("Bravo");
        await new OsuWebBeatmapMirror(NewOsuWebContext()).WriteAsync(new Beatmap
        {
            Id = 100, SetId = 10, Md5 = "a", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "Hard", Artist = "Artist", Title = "Song", Creator = "M",
        });
        await InsertAsync(alpha, 800_000, "A", stable: true);
        await InsertAsync(bravo, 900_000, "S", stable: false);

        var interop = new RecordingInterop();
        var channels = new ChannelRegistry();
        var listener = new PlayerRegistry().Add(new BanchoPresence { UserId = 50, Username = "Listener" });
        channels.Get("#announce")!.Join(listener);
        var services = new ServiceCollection()
            .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
            .AddScoped(_ => new OsuWebInterop(new HttpClient(interop), new OsuWebInteropOptions { InteropUrl = "http://osu-web", InteropSecret = "s" }))
            .BuildServiceProvider();
        ScoreRankEvents Events() => new(services.GetRequiredService<IServiceScopeFactory>(), channels, new ConfigurationBuilder().Build(), NullLogger<ScoreRankEvents>.Instance);

        await Events().TickAsync();
        Assert.Equal(
        [
            $"/_lio/users/{alpha}/100/0/rank-achieved?position_after=1&rank=A&legacy_score_event=1",
            $"/_lio/users/{bravo}/100/0/rank-achieved?position_after=1&rank=S&legacy_score_event=0",
            $"/_lio/users/{alpha}/100/0/first-place-lost?legacy_score_event=0",
        ], interop.Paths);
        Assert.Empty(listener.DequeueAll()); // old plays aren't announced in-game

        // Marked done: another start writes nothing again.
        await Events().TickAsync();
        Assert.Equal(3, interop.Paths.Count);
    }

    private async Task<int> AddUserAsync(string name) =>
        (await new OsuWebUserRepository(NewOsuWebContext()).AddAsync(new User
        {
            Username = name, SafeName = User.MakeSafeName(name), Email = $"{name.ToLowerInvariant()}@x.dev", PasswordBcrypt = "x", Country = "nl",
        })).Id;

    private const string LegacyId = "(SELECT COALESCE(MAX(x.id), 0) + 1000 FROM scores x)";

    private const string Data = """{"mods":[],"statistics":{"great":300}}""";

    private Task InsertAsync(int userId, uint total, string grade, bool stable, bool passed = true) =>
        ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $$"""
            INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, max_combo, total_score, data, pp, legacy_score_id, legacy_total_score, ended_at)
            SELECT {{userId}}, 0, 100, {{(passed ? 1 : 0)}}, 1, '{{grade}}', {{(passed ? 1 : 0)}}, 0.97, 400, {{total}}, '{{Data}}', 100,
              {{(stable ? LegacyId : "NULL")}}, {{total}}, UTC_TIMESTAMP()
            """));

    private sealed class RecordingInterop : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Without the signing timestamp, which changes every call.
            var query = string.Join('&', request.RequestUri!.Query.TrimStart('?').Split('&').Where(p => !p.StartsWith("timestamp=", StringComparison.Ordinal)));
            Paths.Add(request.RequestUri.AbsolutePath + (query.Length > 0 ? "?" + query : string.Empty));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
