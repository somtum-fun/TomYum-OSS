using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Admin;
using TomYum.Server.Anticheat;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using TomYum.Server.Discord;
using TomYum.Server.Tests.Discord;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Plays sent to circleguard, and what a verdict does.</summary>
public sealed class CircleguardWatchTests : OsuWebDatabaseTest, IDisposable
{
    private const string Data = """{"mods":[{"acronym":"HD"},{"acronym":"CL"}],"statistics":{"great":300}}""";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tomyum-cg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(true, 1, 400, "HD", 0, true)]
    [InlineData(false, 1, 400, "HD", 0, false)] // failed
    [InlineData(true, 0, 400, "HD", 0, false)] // pending map
    [InlineData(true, 3, 400, "HD", 0, false)] // qualified
    [InlineData(true, 4, 400, "HD", 0, true)] // loved
    [InlineData(true, 1, 4, "HD", 0, false)] // combo under 5
    [InlineData(true, 1, 400, "AP", 0, false)] // autopilot
    [InlineData(true, 1, 400, "RX", 0, true)] // relax in osu!
    [InlineData(true, 1, 400, "RX", 1, false)] // relax elsewhere
    public void OnlyRankedPasses_AreChecked(bool passed, int status, int combo, string mod, int ruleset, bool expected)
    {
        var score = new ScoreCard(1, 2, "P", ruleset, 3, 4, "A", "T", "V", "C", 5, status, [mod], 0.98, combo, 500, "S", passed, 100, 1, 1, true, 1, 0, 0, 0, DateTime.UtcNow);
        Assert.Equal(expected, CircleguardWatch.Eligible(score));
    }

    [MySqlFact]
    public async Task AHighRisk_Restricts_AndTellsStaff()
    {
        await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var cheater = (await users.AddAsync(NewUser("Cheater"))).Id;
        var top = (await users.AddAsync(NewUser("Top"))).Id;
        await new OsuWebBeatmapMirror(NewOsuWebContext()).WriteAsync(new Beatmap { Id = 100, SetId = 10, Md5 = "a", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "Hard", Artist = "A", Title = "T", Creator = "M" });

        var replays = Path.Combine(_directory, "replays");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Replays:Path"] = replays }).Build();
        Directory.CreateDirectory(Path.Combine(_directory, "osu"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "osu", "100.osu"), "osu file format v14\n[HitObjects]\n");

        var service = new FakeCircleguard(risk: 80);
        var gateway = new DiscordFakeGateway();
        var watch = NewWatch(configuration, service, gateway);

        await watch.TickAsync(); // starts at the newest score
        await InsertStableAsync(top, 999_000, replays, "top play");
        var played = await InsertStableAsync(cheater, 900_000, replays, "cheater's play");
        await watch.TickAsync();

        // The cheater's replay went with the map and the top play to compare against.
        var sent = Assert.Single(service.Requests, r => FakeCircleguard.Judged(r) == "cheater's play");
        Assert.Contains("osu file format v14", sent, StringComparison.Ordinal);
        Assert.Contains("top play", sent, StringComparison.Ordinal);
        Assert.Contains("\"username\":\"Top\"", sent, StringComparison.Ordinal);
        Assert.Equal(2, service.Requests.Count); // the top play was checked too, and was clean

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT user_warnings FROM phpbb_users WHERE user_id = {cheater}"));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT user_warnings FROM phpbb_users WHERE user_id = {top}"));
        Assert.Equal(CircleguardWatch.PlayerReason, await ScalarAsync<string>($"SELECT reason FROM osu_user_banhistory WHERE user_id = {cheater}"));
        var log = await new StaffLogRepository(NewTomYumContext()).ListAsync(new StaffLogQuery(Action: "user.restrict"));
        Assert.StartsWith("circleguard restricted Cheater (risk 80/100", Assert.Single(log).Summary, StringComparison.Ordinal);

        var report = Assert.Single(gateway.In(DiscordFakeGateway.UserLog), p => p.Embed?.AuthorName == "circleguard: Cheater");
        Assert.Contains("Restricted automatically", report.Embed!.Description, StringComparison.Ordinal);
        Assert.Contains("• Un-human play detected", report.Embed.Description, StringComparison.Ordinal);
        Assert.Equal($"mod:unrestrict:{cheater}:circleguard false alarm", report.Buttons[0].CustomId);
        Assert.Equal($"https://osu.test/scores/{played}", report.Embed.Url);
    }

    private CircleguardWatch NewWatch(IConfiguration configuration, FakeCircleguard service, DiscordFakeGateway gateway)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(configuration)
            .AddSingleton(TimeProvider.System)
            .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
            .AddScoped(_ => new OsuWebModeration(NewOsuWebContext()))
            .AddScoped<IUserRepository>(_ => new OsuWebUserRepository(NewOsuWebContext()))
            .AddScoped<IStaffLogRepository>(_ => new StaffLogRepository(NewTomYumContext()))
            .AddSingleton(new PlayerRegistry())
            .AddSingleton(new TomYum.Domain.Services.RestrictedUsers())
            .AddScoped<UserModerationSync>()
            .AddSingleton(new OsuFileCache(Path.Combine(_directory, "osu"), new HttpClient(), NullLogger<OsuFileCache>.Instance))
            .BuildServiceProvider();
        var options = new CircleguardOptions { Url = "http://circleguard.test", Key = "k", CompareOfficial = false };
        var discord = new DiscordWatch(DiscordFakeGateway.Options(), gateway, new DiscordFormat("https://osu.test", "https://a.test"), new DiscordState(), TimeProvider.System);
        return new CircleguardWatch(
            options, new CircleguardClient(new HttpClient(service), options),
            new OfficialReplays(new HttpClient(), new OsuApiOptions(), configuration, TimeProvider.System, NullLogger<OfficialReplays>.Instance),
            services.GetRequiredService<IServiceScopeFactory>(), configuration, TimeProvider.System, NullLogger<CircleguardWatch>.Instance, discord);
    }

    /// <summary>A stable pass with its replay file (the text stands in for the replay).</summary>
    private async Task<ulong> InsertStableAsync(int userId, uint total, string replays, string replay)
    {
        await ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $$"""
            INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, max_combo, total_score, data, pp, legacy_score_id, legacy_total_score, ended_at)
            SELECT {{userId}}, 0, 100, 1, 1, 'S', 1, 0.97, 400, {{total}}, '{{Data}}', 100, (SELECT COALESCE(MAX(x.id), 0) + 1000 FROM scores x), {{total}}, UTC_TIMESTAMP()
            """));
        var id = await ScalarAsync<ulong>("SELECT MAX(id) FROM scores");
        var legacy = await ScalarAsync<long>($"SELECT legacy_score_id FROM scores WHERE id = {id}");
        Directory.CreateDirectory(replays);
        await File.WriteAllTextAsync(Path.Combine(replays, legacy.ToString(CultureInfo.InvariantCulture) + ".osr"), replay);
        return id;
    }

    private static User NewUser(string name) => new()
    {
        Username = name,
        SafeName = User.MakeSafeName(name),
        Email = $"{name.ToLowerInvariant()}@x.dev",
        PasswordBcrypt = "x",
        Country = "nl",
    };

    /// <summary>The service: flags the cheater's play, passes everything else.</summary>
    private sealed class FakeCircleguard(int risk) : HttpMessageHandler
    {
        /// <summary>The replay being judged (the form's "replay" part), not the ones it's compared with.</summary>
        public static string Judged(string body)
        {
            var start = body.IndexOf("name=replay", StringComparison.Ordinal);
            var content = body.IndexOf("\r\n\r\n", start, StringComparison.Ordinal) + 4;
            return body[content..body.IndexOf("\r\n--", content, StringComparison.Ordinal)];
        }

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add(body);
            var flagged = Judged(body) == "cheater's play" && request.Headers.GetValues("X-Circleguard-Key").Single() == "k";
            var json = flagged
                ? $$$"""{"risk_score":{{{risk}}},"reasons":["Un-human play detected (UR 12.00 < 35.5, raw UR 12.00)"],"mods":"HD","unstable_rate":12,"converted_unstable_rate":12,"frametime":16.6,"snap_count":0,"similarity":{"comparisons":[{"score_id":1,"username":"Top","distance":40}],"threshold":17}}"""
                : """{"risk_score":0,"reasons":[],"mods":"HD","snap_count":0}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
