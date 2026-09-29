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
        Assert.Contains("\"frames_only\":true,\"ruleset\":0,\"mods\":8,\"username\":\"Cheater\",\"max_combo\":400,\"count_300\":300", sent, StringComparison.Ordinal);
        Assert.Equal(2, service.Requests.Count); // the top play was checked too, and was clean

        Assert.Equal(1L, await ScalarAsync<long>($"SELECT user_warnings FROM phpbb_users WHERE user_id = {cheater}"));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT user_warnings FROM phpbb_users WHERE user_id = {top}"));
        Assert.Equal(CircleguardWatch.PlayerReason, await ScalarAsync<string>($"SELECT reason FROM osu_user_banhistory WHERE user_id = {cheater}"));
        var log = await new StaffLogRepository(NewTomYumContext()).ListAsync(new StaffLogQuery(Action: "user.restrict"));
        Assert.StartsWith("circleguard restricted Cheater (risk 80/100", Assert.Single(log).Summary, StringComparison.Ordinal);

        var report = Assert.Single(gateway.In(DiscordFakeGateway.UserLog), p => p.Embed?.AuthorName == "circleguard ▸ Cheater");
        Assert.StartsWith("**⛔ Restricted automatically** ▸ risk **80**/100\n`████████████████░░░░`", report.Embed!.Description, StringComparison.Ordinal);
        Assert.Contains("• Un-human play detected", report.Embed.Description, StringComparison.Ordinal);
        Assert.Equal($"mod:unrestrict:{cheater}:circleguard false alarm", report.Buttons[0].CustomId);
        Assert.Equal($"https://osu.test/scores/{played}", report.Embed.Url);
        Assert.Equal("1 here ▸ 0 on osu!", report.Embed.Fields.Single(f => f.Name == "Compared with").Value);
        Assert.Equal("circleguard ▸ HD", report.Embed.Footer);
    }

    [MySqlFact]
    public async Task Staff_CanScanAnyScore()
    {
        await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var suspect = (await users.AddAsync(NewUser("Suspect"))).Id;
        var staff = (await users.AddAsync(new User
        {
            Username = "Gmt", SafeName = "gmt", Email = "gmt@x.dev", PasswordBcrypt = "x", Country = "nl",
            Privileges = UserPrivileges.Normal | UserPrivileges.Moderator,
        })).Id;
        await new OsuWebBeatmapMirror(NewOsuWebContext()).WriteAsync(new Beatmap { Id = 100, SetId = 10, Md5 = "a", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "Hard", Artist = "A", Title = "T", Creator = "M" });
        var replays = Path.Combine(_directory, "replays");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Replays:Path"] = replays }).Build();
        Directory.CreateDirectory(Path.Combine(_directory, "osu"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "osu", "100.osu"), "osu file format v14\n[HitObjects]\n");
        var others = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var other = (await users.AddAsync(NewUser("Other" + i.ToString(CultureInfo.InvariantCulture)))).Id;
            await InsertStableAsync(other, (uint)(990_000 - i), replays, "top play");
        }
        var played = await InsertStableAsync(suspect, 900_000, replays, "cheater's play");

        var service = new FakeCircleguard(risk: 45);
        var links = new InMemoryDiscordLinkRepository();
        await links.LinkAsync(staff, 1002, DateTime.UtcNow);
        await links.LinkAsync(suspect, 1001, DateTime.UtcNow);
        var services = Services(configuration, service, links);
        var commands = new DiscordCommands(services.GetRequiredService<IServiceScopeFactory>(), new PlayerRegistry(), new DiscordLinkCodes(), new DiscordFormat("https://osu.test", "https://a.test"));

        Assert.Equal("That command is staff-only.", (await commands.RunAsync("scan", new Dictionary<string, string> { ["score"] = played.ToString(CultureInfo.InvariantCulture) }, 1001)).Post.Content);
        Assert.Contains("no such score", (await commands.RunAsync("scan", new Dictionary<string, string> { ["score"] = "999999" }, 1002)).Post.Content, StringComparison.Ordinal);

        var updates = new List<DiscordPost>();
        var reply = await commands.RunAsync("scan", new Dictionary<string, string> { ["score"] = $"https://osu.test/scores/{played}" }, 1002, updates.Add);
        Assert.False(reply.Private); // in the channel, updated live
        Assert.StartsWith("🔍 **Scanning…**", updates[0].Embed!.Description, StringComparison.Ordinal);
        Assert.Contains("⏳ Top plays here", updates[1].Embed!.Description, StringComparison.Ordinal);
        var last = updates[^1].Embed!.Description!;
        Assert.Contains("✅ Top plays here ▸ 3/3", last, StringComparison.Ordinal);
        Assert.Contains("➖ osu!'s top plays (not compared)", last, StringComparison.Ordinal);
        Assert.Contains("⏳ circleguard's analysis", last, StringComparison.Ordinal);
        var embed = reply.Post.Embed!;
        Assert.StartsWith("**🟠 Suspicious** ▸ risk **45**/100", embed.Description, StringComparison.Ordinal);
        Assert.Equal("3 here ▸ 0 on osu!", embed.Fields.Single(f => f.Name == "Compared with").Value);
        Assert.EndsWith("scanned by Gmt", embed.Footer, StringComparison.Ordinal);
        Assert.Equal("Restrict", reply.Post.Buttons[0].Label);
        // All three top plays went along, not only five... or one.
        Assert.Equal(3, service.Requests.Single(r => FakeCircleguard.Judged(r) == "cheater's play").Split("top play").Length - 1);
    }

    private CircleguardWatch NewWatch(IConfiguration configuration, FakeCircleguard service, DiscordFakeGateway gateway)
    {
        var services = Services(configuration, service, new InMemoryDiscordLinkRepository());
        var options = services.GetRequiredService<CircleguardOptions>();
        var discord = new DiscordWatch(DiscordFakeGateway.Options(), gateway, new DiscordFormat("https://osu.test", "https://a.test"), new DiscordState(), TimeProvider.System);
        return new CircleguardWatch(
            options, services.GetRequiredService<CircleguardScanner>(),
            services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<CircleguardWatch>.Instance, discord);
    }

    private ServiceProvider Services(IConfiguration configuration, FakeCircleguard service, IDiscordLinkRepository links)
    {
        var options = new CircleguardOptions { Url = "http://circleguard.test", Key = "k", CompareOfficial = false };
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(configuration)
            .AddSingleton(TimeProvider.System)
            .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
            .AddScoped(_ => NewTomYumContext())
            .AddScoped(_ => new OsuWebModeration(NewOsuWebContext()))
            .AddScoped<IUserRepository>(_ => new OsuWebUserRepository(NewOsuWebContext()))
            .AddScoped<IStaffLogRepository>(_ => new StaffLogRepository(NewTomYumContext()))
            .AddSingleton(new PlayerRegistry())
            .AddSingleton(new TomYum.Domain.Services.RestrictedUsers())
            .AddScoped<UserModerationSync>()
            .AddSingleton(new OsuFileCache(Path.Combine(_directory, "osu"), new HttpClient(), NullLogger<OsuFileCache>.Instance))
            .AddSingleton(options)
            .AddSingleton(links)
            .AddSingleton(new CircleguardScanner(
                options, new CircleguardClient(new HttpClient(service), options),
                new OfficialReplays(new HttpClient(), new OsuApiOptions(), configuration, TimeProvider.System, NullLogger<OfficialReplays>.Instance),
                configuration, NullLogger<CircleguardScanner>.Instance))
            .BuildServiceProvider();
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
        // TomYum's own row: the stable client uploads only frames, so the details come from here.
        await using (var tomyum = NewTomYumContext())
        {
            tomyum.Scores.Add(new Score { Id = legacy, UserId = userId, BeatmapId = 100, BeatmapMd5 = "a", Mode = 0, MaxCombo = 400, Count300 = 300, Mods = 8, Passed = true, Grade = "S", SubmittedAt = DateTimeOffset.UtcNow });
            await tomyum.SaveChangesAsync();
        }
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
