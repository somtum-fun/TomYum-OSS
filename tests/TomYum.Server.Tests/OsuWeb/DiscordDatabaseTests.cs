using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Bancho;
using TomYum.Server.Discord;
using TomYum.Server.Tests.Discord;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>The Discord bot against real databases: lookups, #1 detection and the announcements.</summary>
public sealed class DiscordDatabaseTests : OsuWebDatabaseTest
{
    private const int Map = 100;
    private const int OtherMap = 101;

    [MySqlFact]
    public async Task FirstPlace_ChangingHands_IsDetected_AndOnlyThen()
    {
        var (alpha, bravo, cheater) = await SeedAsync();
        var community = new OsuWebCommunity(NewOsuWebContext());

        async Task<bool> PlayAsync(int userId, uint total) =>
            await community.TookFirstPlaceAsync(await LatestAsync(community, await InsertScoreAsync(userId, Map, total)));

        Assert.True(await PlayAsync(alpha, 900_000));   // first ever
        Assert.True(await PlayAsync(bravo, 950_000));   // takes it
        Assert.False(await PlayAsync(alpha, 940_000));  // not enough
        Assert.True(await PlayAsync(alpha, 960_000));   // takes it back
        Assert.False(await PlayAsync(alpha, 970_000));  // improves their own #1: not news
        Assert.False(await PlayAsync(bravo, 970_000));  // a tie doesn't take it
        Assert.True(await PlayAsync(bravo, 999_999) is var bravoTop && bravoTop);
        await InsertScoreAsync(cheater, Map, 1_000_000); // restricted: invisible
        Assert.False(await PlayAsync(alpha, 990_000));
    }

    [MySqlFact]
    public async Task Commands_ShowProfiles_Rankings_AndLeaderboards()
    {
        var (alpha, bravo, cheater) = await SeedAsync();
        await InsertStatsAsync(alpha, pp: 4321.5, accuracy: 98.12, plays: 1234, country: "NL");
        await InsertStatsAsync(bravo, pp: 5000, accuracy: 97, plays: 99, country: "TH");
        await InsertStatsAsync(cheater, pp: 9999, accuracy: 100, plays: 5, country: "NL");
        await InsertScoreAsync(alpha, Map, 900_000, pp: 250, mods: "HD");
        await InsertScoreAsync(alpha, OtherMap, 800_000, pp: 310.4);
        await InsertScoreAsync(bravo, Map, 950_000, pp: 260);
        await InsertScoreAsync(alpha, Map, 400_000, passed: false);

        var links = new InMemoryDiscordLinkRepository();
        await links.LinkAsync(alpha, 555, DateTime.UtcNow);
        await links.LinkAsync(cheater, 666, DateTime.UtcNow);
        var commands = NewCommands(links);

        var profile = await commands.RunAsync("profile", new Dictionary<string, string>(), 555);
        Assert.False(profile.Private);
        Assert.Equal("Alpha: osu! profile", profile.Post.Embed!.AuthorName);
        Assert.Contains("#2 (:flag_nl: #1)", profile.Post.Embed.Description); // the restricted player doesn't count
        Assert.Contains("4,321.5pp", profile.Post.Embed.Description);
        Assert.Contains("98.12%", profile.Post.Embed.Description);

        var rank = await commands.RunAsync("rank", new Dictionary<string, string> { ["user"] = "bravo" }, 555);
        Assert.Equal("**Bravo** is #1 in osu! (:flag_th: #1) with 5,000pp.", rank.Post.Content);
        Assert.Equal("That member hasn't linked their account.", (await commands.RunAsync("rank", new Dictionary<string, string> { ["user"] = "<@777>" }, 555)).Post.Content);
        Assert.Equal("No such player.", (await commands.RunAsync("rank", new Dictionary<string, string> { ["user"] = "Cheater" }, 555)).Post.Content);
        // A restricted player sees their own profile, privately.
        var own = await commands.RunAsync("profile", new Dictionary<string, string>(), 666);
        Assert.True(own.Private);
        Assert.Contains("restricted", own.Post.Embed!.Description);

        var recent = await commands.RunAsync("recent", new Dictionary<string, string> { ["user"] = "Alpha" }, 1);
        Assert.Equal(DiscordFormat.Danger, recent.Post.Embed!.Color); // the fail
        Assert.StartsWith("Artist - Song [Hard] [", recent.Post.Embed.Title); // Bathbot-style: the mapper is in the footer
        Assert.Contains("• Mapset by Mapper", recent.Post.Embed.Footer, StringComparison.Ordinal);
        Assert.Equal("https://osu.test/b/100", recent.Post.Embed.Url);

        var best = await commands.RunAsync("best", new Dictionary<string, string> { ["user"] = "Alpha" }, 1);
        var description = best.Post.Embed!.Description!;
        Assert.True(description.IndexOf("310.40pp", StringComparison.Ordinal) < description.IndexOf("250.00pp", StringComparison.Ordinal));
        Assert.StartsWith("**1.** [", description, StringComparison.Ordinal);
        Assert.StartsWith("Page 1/1", best.Post.Embed.Footer, StringComparison.Ordinal);
        Assert.StartsWith("top:", best.Post.Buttons[2].CustomId, StringComparison.Ordinal);
        Assert.Contains("+HD", description);

        var top = await commands.RunAsync("top", new Dictionary<string, string>(), 1);
        var lines = top.Post.Embed!.Description!.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("Bravo", lines[0]);
        Assert.Contains("Alpha", lines[1]);
        Assert.Single((await commands.RunAsync("top", new Dictionary<string, string> { ["country"] = "nl" }, 1)).Post.Embed!.Description!.Split('\n'));

        var board = await commands.RunAsync("leaderboard", new Dictionary<string, string> { ["beatmap"] = $"https://osu.ppy.sh/beatmaps/{Map}" }, 1);
        var boardLines = board.Post.Embed!.Description!.Split('\n');
        Assert.Equal(2, boardLines.Length);
        Assert.Contains("Bravo", boardLines[0]);
        Assert.Equal("That beatmap isn't on the server.", (await commands.RunAsync("leaderboard", new Dictionary<string, string> { ["beatmap"] = "999" }, 1)).Post.Content);
    }

    [MySqlFact]
    public async Task Announcer_PostsEachKindOfNews_WhereItBelongs()
    {
        var (alpha, bravo, _) = await SeedAsync();
        await InsertScoreAsync(alpha, Map, 900_000, pp: 100); // before the bot started: not announced
        var gateway = new DiscordFakeGateway();
        var links = new InMemoryDiscordLinkRepository();
        await ExecuteAsync($"INSERT INTO phpbb_user_group (group_id, user_id, group_leader, user_pending) SELECT group_id, {bravo}, 0, 0 FROM phpbb_groups WHERE identifier IN ('gmt', 'bng')");
        await links.LinkAsync(bravo, 4242, DateTime.UtcNow);
        var (announcer, _) = NewAnnouncer(gateway, links);

        await announcer.TickAsync();
        Assert.Contains("is up", Assert.Single(gateway.In(DiscordFakeGateway.ServerLog)).Embed!.Description);
        var sync = Assert.Single(gateway.RoleSyncs);
        Assert.Equal(4242UL, sync.DiscordUserId);
        Assert.Equal([22UL, 24UL], sync.Wanted.Order());
        gateway.Clear();

        // A stable #1 and a big play, a lazer score the processor hasn't finished, a medal, a new player.
        await InsertScoreAsync(bravo, Map, 950_000, pp: 120);
        await InsertScoreAsync(alpha, OtherMap, 800_000, pp: 400);
        var lazer = await InsertScoreAsync(bravo, OtherMap, 900_000, pp: null, stable: false);
        await ExecuteAsync("INSERT INTO osu_achievements (achievement_id, name, slug, description, `grouping`, ordering, progression) VALUES (1, 'Rising Star', 'osu-skill-pass-1', 'Can''t go <b>forward</b> without the first steps.', 'Skill', 1, 1)");
        await ExecuteAsync($"INSERT INTO osu_user_achievements (user_id, achievement_id, `date`) VALUES ({alpha}, 1, UTC_TIMESTAMP())");
        var users = new OsuWebUserRepository(NewOsuWebContext());
        await users.AddAsync(new User { Username = "Newbie", SafeName = "newbie", Email = "n@x.dev", PasswordBcrypt = "x", Country = "de" });

        await announcer.TickAsync();
        var news = gateway.In(DiscordFakeGateway.PublicLog);
        Assert.Contains(news, p => p.Embed!.AuthorName == "Bravo" && p.Embed.Description!.StartsWith("🥇 **New #1!**", StringComparison.Ordinal));
        Assert.Contains(news, p => p.Embed!.AuthorName == "Alpha" && p.Embed.Description!.StartsWith("🥇 **New #1** and a **400pp** play!", StringComparison.Ordinal));
        Assert.Contains(news, p => p.Embed!.Description!.Contains("**Rising Star**", StringComparison.Ordinal) && p.Embed.Description.Contains("*Can't go forward", StringComparison.Ordinal));
        Assert.Contains(news, p => p.Embed!.Description == "👋 Welcome, **Newbie** :flag_de:!");
        Assert.Equal(4, news.Count);
        gateway.Clear();

        // The lazer score waited for the processor.
        await ExecuteAsync($"UPDATE scores SET pp = 500 WHERE id = {lazer}");
        await ExecuteAsync($"INSERT INTO score_process_history (score_id, processed_version) VALUES ({lazer}, 11)");
        await announcer.TickAsync();
        var lazerPost = Assert.Single(gateway.In(DiscordFakeGateway.PublicLog));
        Assert.StartsWith("🥇 **New #1** and a **500pp** play!", lazerPost.Embed!.Description, StringComparison.Ordinal);
        Assert.Equal("osu! ▸ lazer", lazerPost.Embed.Footer);
        gateway.Clear();

        // Staff actions: accounts to the user log, the rest to the server log, statuses to #beatmaps too.
        await using var tomyum = await NewTomYumContextAsync();
        var staffLog = new StaffLogRepository(tomyum);
        await staffLog.AddAsync(new StaffLogEntry { CreatedAt = DateTime.UtcNow, ActorId = bravo, Action = "user.silence", TargetId = alpha, Summary = "Silenced Alpha for 1 hour(s): spam" });
        await staffLog.AddAsync(new StaffLogEntry { CreatedAt = DateTime.UtcNow, ActorId = bravo, Action = "beatmapset.status", TargetId = 10, Summary = "Ranked Artist - Song", Result = "ranked" });
        await staffLog.AddAsync(new StaffLogEntry { CreatedAt = DateTime.UtcNow, ActorId = bravo, Action = "beatmapset.status", TargetId = 10, Summary = "Pending Artist - Song", Result = "pending" });
        await announcer.TickAsync();

        var userLog = Assert.Single(gateway.In(DiscordFakeGateway.UserLog));
        Assert.Equal(("Bravo", "Silenced Alpha for 1 hour\\(s\\): spam", "user.silence"), (userLog.Embed!.AuthorName, userLog.Embed.Description, userLog.Embed.Footer));
        Assert.Equal(2, gateway.In(DiscordFakeGateway.ServerLog).Count);
        var ranked = Assert.Single(gateway.In(DiscordFakeGateway.Beatmaps));
        Assert.Equal("Artist - Song", ranked.Embed!.Title);
        Assert.StartsWith("⭐ **Ranked** by Bravo", ranked.Embed.Description, StringComparison.Ordinal);
        Assert.Contains("Hard ▸ Insane", ranked.Embed.Description, StringComparison.Ordinal);

        // Nothing is posted twice.
        gateway.Clear();
        await announcer.TickAsync();
        Assert.Empty(gateway.Posts);
    }

    [MySqlFact]
    public async Task TheOnlyRankedPlayer_IsFirst_WhateverTheirPp()
    {
        var (alpha, _, _) = await SeedAsync();
        await InsertStatsAsync(alpha, pp: 515.168, accuracy: 93.6, plays: 7, country: "NL"); // not exact as a float
        var links = new InMemoryDiscordLinkRepository();
        await links.LinkAsync(alpha, 555, DateTime.UtcNow);

        var rank = await NewCommands(links).RunAsync("rank", new Dictionary<string, string>(), 555);
        Assert.Equal("**Alpha** is #1 in osu! (:flag_nl: #1) with 515.17pp.", rank.Post.Content);
    }

    [MySqlFact]
    public async Task Online_CountsLazerAndWebsitePlayers_TheWebsitesWay()
    {
        var (alpha, bravo, cheater) = await SeedAsync();
        await ExecuteAsync($"UPDATE phpbb_users SET user_lastvisit = UNIX_TIMESTAMP() - 60 WHERE user_id IN ({alpha}, {bravo}, {cheater})");
        await ExecuteAsync($"UPDATE phpbb_users SET user_allow_viewonline = 0 WHERE user_id = {bravo}"); // hides their presence
        var players = new PlayerRegistry();
        players.Add(new TomYum.Bancho.BanchoPresence { UserId = 999, Username = "StablePlayer" });
        var services = new ServiceCollection()
            .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
            .AddSingleton<IDiscordLinkRepository>(new InMemoryDiscordLinkRepository())
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var commands = new DiscordCommands(scopes, players, new DiscordLinkCodes(), new DiscordFormat("https://osu.test", "https://a.test"),
            online: new TomYum.Server.OsuWeb.OnlinePlayers(players, scopes));

        var online = await commands.RunAsync("online", new Dictionary<string, string>(), 1);
        Assert.Equal("**2** online\n▸ **stable** (1): StablePlayer\n▸ **website** (1): Alpha", online.Post.Content);
    }

    [MySqlFact]
    public async Task Announcer_WaitsWhileDiscordIsDown_ThenCatchesUp()
    {
        var (alpha, _, _) = await SeedAsync();
        var gateway = new DiscordFakeGateway();
        var (announcer, _) = NewAnnouncer(gateway, new InMemoryDiscordLinkRepository());
        await announcer.TickAsync();
        gateway.Clear();

        gateway.IsConnected = false;
        await InsertScoreAsync(alpha, Map, 900_000, pp: 100);
        await announcer.TickAsync();
        Assert.Empty(gateway.Posts);

        gateway.IsConnected = true;
        await announcer.TickAsync();
        Assert.Single(gateway.In(DiscordFakeGateway.PublicLog));
    }

    [MySqlFact]
    public async Task RestrictedPlayers_LoseTheirRoles()
    {
        var (_, _, cheater) = await SeedAsync();
        await ExecuteAsync($"INSERT INTO phpbb_user_group (group_id, user_id, group_leader, user_pending) SELECT group_id, {cheater}, 0, 0 FROM phpbb_groups WHERE identifier = 'nat'");
        var (announcer, services) = NewAnnouncer(new DiscordFakeGateway(), new InMemoryDiscordLinkRepository());

        using var scope = services.CreateScope();
        Assert.Empty(await announcer.WantedRolesAsync(scope.ServiceProvider, cheater, CancellationToken.None));
        await ExecuteAsync($"UPDATE phpbb_users SET user_warnings = 0 WHERE user_id = {cheater}");
        Assert.Equal([23UL], await announcer.WantedRolesAsync(scope.ServiceProvider, cheater, CancellationToken.None));
    }

    private (DiscordAnnouncer, IServiceProvider) NewAnnouncer(DiscordFakeGateway gateway, InMemoryDiscordLinkRepository links)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
            .AddScoped<IUserRepository>(_ => new OsuWebUserRepository(NewOsuWebContext()))
            .AddScoped<IStaffLogRepository>(_ => new StaffLogRepository(NewTomYumContext()))
            .AddSingleton<IDiscordLinkRepository>(links)
            .BuildServiceProvider();
        var announcer = new DiscordAnnouncer(
            DiscordFakeGateway.Options(), gateway, new DiscordFormat("https://osu.test", "https://a.test"), new DiscordLogSink(),
            services.GetRequiredService<IServiceScopeFactory>(), new DiscordLinkCodes(), new TomYumVersion(), TimeProvider.System,
            NullLogger<DiscordAnnouncer>.Instance);
        return (announcer, services);
    }

    private DiscordCommands NewCommands(IDiscordLinkRepository links)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
            .AddSingleton(links)
            .BuildServiceProvider();
        return new DiscordCommands(services.GetRequiredService<IServiceScopeFactory>(), new PlayerRegistry(), new DiscordLinkCodes(), new DiscordFormat("https://osu.test", "https://a.test"));
    }

    /// <summary>Two players, a restricted one, and a ranked set with two maps.</summary>
    private async Task<(int Alpha, int Bravo, int Cheater)> SeedAsync()
    {
        await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var alpha = await users.AddAsync(new User { Username = "Alpha", SafeName = "alpha", Email = "a@x.dev", PasswordBcrypt = "x", Country = "nl" });
        var bravo = await users.AddAsync(new User { Username = "Bravo", SafeName = "bravo", Email = "b@x.dev", PasswordBcrypt = "x", Country = "th" });
        var cheater = await users.AddAsync(new User { Username = "Cheater", SafeName = "cheater", Email = "c@x.dev", PasswordBcrypt = "x", Country = "nl" });
        await ExecuteAsync($"UPDATE phpbb_users SET user_warnings = 1 WHERE user_id = {cheater.Id}");

        var beatmaps = new OsuWebBeatmapMirror(NewOsuWebContext());
        await beatmaps.WriteAsync(new Beatmap { Id = Map, SetId = 10, Md5 = "md5-100", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "Hard", Artist = "Artist", Title = "Song", Creator = "Mapper", StarRating = 4.5 });
        await beatmaps.WriteAsync(new Beatmap { Id = OtherMap, SetId = 10, Md5 = "md5-101", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "Insane", Artist = "Artist", Title = "Song", Creator = "Mapper", StarRating = 5.5 });
        return (alpha.Id, bravo.Id, cheater.Id);
    }

    private static async Task<ScoreCard> LatestAsync(OsuWebCommunity community, ulong id) =>
        (await community.GetScoresAfterAsync(id - 1, 1)).Single();

    private async Task<ulong> InsertScoreAsync(int userId, int beatmapId, uint total, double? pp = null, bool passed = true, bool stable = true, string? mods = null)
    {
        var data = mods is null
            ? """{"mods":[{"acronym":"CL"}],"statistics":{"great":300,"ok":5,"meh":1,"miss":2}}"""
            : $$$"""{"mods":[{"acronym":"{{{mods}}}"}],"statistics":{"great":300,"ok":5,"meh":1,"miss":2}}""";
        var ppValue = pp is { } value ? value.ToString(CultureInfo.InvariantCulture) : "NULL";
        var legacy = stable ? "(SELECT COALESCE(MAX(x.id), 0) + 1000 FROM scores x)" : "NULL";
        await ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, max_combo, total_score, data, pp, legacy_score_id, legacy_total_score, ended_at)
            SELECT {userId}, 0, {beatmapId}, {(passed ? 1 : 0)}, {(passed ? 1 : 0)}, '{(passed ? "S" : "F")}', {(passed ? 1 : 0)}, 0.97, 400, {total}, '{data}', {ppValue}, {legacy}, {total}, UTC_TIMESTAMP()
            """));
        return await ScalarAsync<ulong>("SELECT MAX(id) FROM scores");
    }

    private Task InsertStatsAsync(int userId, double pp, double accuracy, int plays, string country) =>
        ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO osu_user_stats (user_id, accuracy_total, accuracy_count, accuracy, playcount, ranked_score, total_score,
              x_rank_count, s_rank_count, a_rank_count, `rank`, level, country_acronym, rank_score, rank_score_index, accuracy_new, total_seconds_played)
            VALUES ({userId}, 0, 0, 0, {plays}, 1000, 2000, 1, 2, 3, 0, 50.5, '{country}', {pp}, 0, {accuracy}, 3700)
            """));
}
