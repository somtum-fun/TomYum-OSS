using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using TomYum.Server.Discord;
using TomYum.Server.OsuWeb;
using TomYum.Server.Tests.Discord;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>
/// The bot's newer features against real databases: map cards and link cards, rank graphs, medals,
/// comparisons, the staff card, status buttons, reports, suspicious plays, milestones, the status
/// message, the map of the day, and <c>!report</c>.
/// </summary>
public sealed class DiscordFeaturesTests : OsuWebDatabaseTest
{
    private const int Map = 100;
    private const int OtherMap = 101;
    private static readonly DiscordFormat Format = new("https://osu.test", "https://a.test");

    [Fact]
    public void RankGraphs_AreDrawn_WhenThereAreTwoRankedDays()
    {
        var days = Enumerable.Range(0, 90).Select(i => i < 30 ? 0 : 5000 - i * 40).ToList();
        using var image = SKBitmap.Decode(RankGraph.Render(days));
        Assert.Equal((RankGraph.Width, RankGraph.Height), (image.Width, image.Height));

        Assert.Null(RankGraph.Render(Enumerable.Repeat(0, 89).Append(12).ToList()));
        Assert.Null(RankGraph.Render([]));
    }

    [Theory]
    [InlineData("HDDT", TomYum.Bancho.Mods.Hidden | TomYum.Bancho.Mods.DoubleTime)]
    [InlineData("+hd hr", TomYum.Bancho.Mods.Hidden | TomYum.Bancho.Mods.HardRock)]
    [InlineData("NC", TomYum.Bancho.Mods.Nightcore | TomYum.Bancho.Mods.DoubleTime)]
    [InlineData("", TomYum.Bancho.Mods.None)]
    public void ModAcronyms_AreRead(string text, TomYum.Bancho.Mods expected) => Assert.Equal(expected, NowPlaying.ParseAcronyms(text));

    [Theory]
    [InlineData("HDX")]
    [InlineData("QQ")]
    public void NonsenseMods_AreRefused(string text) => Assert.Null(NowPlaying.ParseAcronyms(text));

    [MySqlFact]
    public async Task Recent_ShowsTheTry_TheRanks_AndPagesBack()
    {
        var (alpha, _, _, _) = await SeedAsync();
        var links = new InMemoryDiscordLinkRepository();
        await links.LinkAsync(alpha, 1001, DateTime.UtcNow);
        var commands = NewCommands(links, out var services);

        await InsertScoreAsync(alpha, OtherMap, 700_000, pp: 90);   // older, another map
        await InsertScoreAsync(alpha, Map, 800_000, pp: 120);
        await InsertScoreAsync(alpha, Map, 850_000, pp: 130);        // latest: 2nd try in a row

        var reply = await commands.RunAsync("rs", new Dictionary<string, string>(), 1001);
        Assert.Equal("Try #2", reply.Post.Content);
        var card = reply.Post.Embed!;
        Assert.StartsWith("Alpha: 500.00pp (", card.AuthorName, StringComparison.Ordinal);
        Assert.Matches(@"\(#\d+ NL\d+\)$", card.AuthorName);
        Assert.Equal("https://flagcdn.com/w40/nl.png", card.AuthorIconUrl);
        Assert.Equal("Artist - Song [Hard] [4.50★]", card.Title);
        Assert.StartsWith("S +NM  850,000  97.00%  <t:", card.Description, StringComparison.Ordinal);
        Assert.Contains("**130.00**pp • **400x**", card.Description, StringComparison.Ordinal);
        Assert.Contains("`02:00` • `CS: 4 AR: 9 OD: 8 HP: 5` • 🎵 120", card.Description, StringComparison.Ordinal);
        Assert.StartsWith("Mapset by Mapper", card.Footer, StringComparison.Ordinal);
        Assert.Equal([true, true, false, false, false], reply.Post.Buttons.Select(b => b.Disabled));

        // Back through the plays: the first try on the map, then the other map.
        var interactions = new DiscordInteractions(services.GetRequiredService<IServiceScopeFactory>(), commands: commands);
        var older = (await interactions.PressAsync(reply.Post.Buttons[3].CustomId!, 9999)).Update!; // anyone may page
        Assert.Equal("Try #1", older.Content);
        Assert.Contains("800,000", older.Embed!.Description, StringComparison.Ordinal);
        var oldest = (await interactions.PressAsync(older.Buttons[4].CustomId!, 9999)).Update!;
        Assert.Equal("Artist - Song [Insane] [5.50★]", oldest.Embed!.Title);
        Assert.True(oldest.Buttons[4].Disabled);
    }

    [Fact]
    public void MapNumbers_FollowTheMods()
    {
        var map = new MapCard(1, 1, "A", "T", "V", "M", 0, 1, 5, 120, 110, 120, 4, 9, 8, 5, 500, 0, 0);
        var (cs, ar, od, hp, rate) = DiscordCommands.WithMods(map, TomYum.Bancho.Mods.HardRock | TomYum.Bancho.Mods.DoubleTime, 0);
        // HR: CS ×1.3, the rest ×1.4 (at most 10); DT then speeds approach and hit windows up by 1.5.
        Assert.Equal((5.2, 11.0, 11.1, 7.0, 1.5), (Math.Round(cs, 1), Math.Round(ar, 1), Math.Round(od, 1), Math.Round(hp, 1), rate));
    }

    [MySqlFact]
    public async Task MapCards_Graphs_Medals_AndComparisons()
    {
        var (alpha, bravo, _, _) = await SeedAsync();
        var links = new InMemoryDiscordLinkRepository();
        await links.LinkAsync(alpha, 1001, DateTime.UtcNow);
        var commands = NewCommands(links, out _);

        var map = await commands.RunAsync("map", new Dictionary<string, string> { ["beatmap"] = "https://osu.test/beatmapsets/10#osu/100", ["mods"] = "HDDT" }, 1001);
        var card = map.Post.Embed!;
        Assert.Equal("Artist - Song [Hard]", card.Title);
        Assert.Contains("+HDDT", card.Description, StringComparison.Ordinal);
        Assert.Contains("♪ 180 BPM", card.Description, StringComparison.Ordinal); // 120 × 1.5
        Assert.Contains("⏱ 1:20", card.Description, StringComparison.Ordinal); // 120 s ÷ 1.5
        Assert.Contains("CS 4 ▸ AR 9 ▸ OD 8 ▸ HP 5", card.Description, StringComparison.Ordinal);
        Assert.True((await commands.RunAsync("map", new Dictionary<string, string> { ["beatmap"] = "999999" }, 1001)).Private);
        Assert.True((await commands.RunAsync("map", new Dictionary<string, string> { ["beatmap"] = "100", ["mods"] = "XYZ" }, 1001)).Private);

        // Rank history: 90 days, today in r5.
        await ExecuteAsync("INSERT INTO osu_counts (name, count) VALUES ('pp_rank_column_osu', 5)");
        await ExecuteAsync($"INSERT INTO osu_user_performance_rank (user_id, mode, r3, r4, r5) VALUES ({alpha}, 0, 30, 20, 10)");
        var history = await new OsuWebCommunity(NewOsuWebContext()).GetRankHistoryAsync(alpha, 0);
        Assert.Equal(90, history.Count);
        Assert.Equal([30, 20, 10], history.TakeLast(3));
        var graph = await commands.RunAsync("graph", new Dictionary<string, string>(), 1001);
        Assert.Equal("rank.png", graph.Post.File!.Name);
        Assert.Equal("attachment://rank.png", graph.Post.Embed!.ImageUrl);
        Assert.Contains("▲ 29", graph.Post.Embed.Description, StringComparison.Ordinal); // #30 → #1 now (the only ranked player)

        await ExecuteAsync("INSERT INTO osu_achievements (achievement_id, name, slug, `grouping`, ordering, progression) VALUES (1, 'Rising Star', 's1', 'Skill', 1, 1), (2, 'Constellation Prize', 's2', 'Skill', 2, 1), (3, 'Catch 20,000 fruits', 'h1', 'Hush-Hush', 1, 1)");
        await ExecuteAsync($"INSERT INTO osu_user_achievements (user_id, achievement_id, `date`) VALUES ({alpha}, 1, UTC_TIMESTAMP())");
        var medals = (await commands.RunAsync("medals", new Dictionary<string, string>(), 1001)).Post.Embed!;
        Assert.Contains("**1** of 3", medals.Description, StringComparison.Ordinal);
        Assert.Contains(medals.Fields, f => f.Name == "Skill" && f.Value == "1/2");
        Assert.Contains(medals.Fields, f => f.Name == "Still to get (some)" && f.Value.StartsWith("Constellation Prize", StringComparison.Ordinal));

        var compare = (await commands.RunAsync("compare", new Dictionary<string, string> { ["player"] = "Bravo" }, 1001)).Post.Embed!;
        Assert.Equal("Alpha vs Bravo ▸ osu!", compare.Title);
        Assert.Contains("**Alpha** is ahead by 200pp", compare.Description, StringComparison.Ordinal);
        await InsertScoreAsync(bravo, Map, 950_000, pp: 120);
        var onMap = (await commands.RunAsync("compare", new Dictionary<string, string> { ["player"] = "Bravo", ["beatmap"] = "100" }, 1001)).Post.Embed!;
        Assert.Contains("**Bravo** has it, **Alpha** doesn't.", onMap.Description, StringComparison.Ordinal);
    }

    [MySqlFact]
    public async Task LinksToTheSite_GetCards()
    {
        var (alpha, _, cheater, _) = await SeedAsync();
        var commands = NewCommands(new InMemoryDiscordLinkRepository(), out _);

        var cards = await commands.PreviewAsync(
            "look https://osu.test/b/100 and https://osu.test/users/Alpha and <https://osu.test/b/101> and https://osu.ppy.sh/b/101 and https://osu.test/u/" + cheater.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(2, cards.Count);
        Assert.Equal("Artist - Song [Hard]", cards[0].Title);
        Assert.Equal($"https://osu.test/users/{alpha}/osu", cards[1].AuthorUrl);
        Assert.Equal("Artist - Song [Insane]", Assert.Single(await commands.PreviewAsync("the set: https://osu.test/beatmapsets/10")).Title);
        Assert.Empty(await commands.PreviewAsync("nothing here"));
    }

    [MySqlFact]
    public async Task StaffCommands_AreForStaff_AndStatusChangesAskFirst()
    {
        var (alpha, _, cheater, staff) = await SeedAsync();
        var links = new InMemoryDiscordLinkRepository();
        await links.LinkAsync(alpha, 1001, DateTime.UtcNow);
        await links.LinkAsync(staff, 1002, DateTime.UtcNow);
        var commands = NewCommands(links, out var services);

        Assert.Equal("That command is staff-only.", (await commands.RunAsync("user", new Dictionary<string, string> { ["player"] = "Cheater" }, 1001)).Post.Content);
        var card = await commands.RunAsync("user", new Dictionary<string, string> { ["player"] = "Cheater" }, 1002);
        Assert.True(card.Private);
        Assert.Contains(card.Post.Embed!.Fields, f => f.Name == "Status" && f.Value.Contains("Restricted", StringComparison.Ordinal));
        Assert.Contains(card.Post.Embed.Fields, f => f.Name == "IP addresses" && f.Value.Contains("(registration)", StringComparison.Ordinal));

        Assert.Equal("That command is staff-only.", (await commands.RunAsync("wipe", new Dictionary<string, string> { ["player"] = "Cheater", ["mode"] = "all", ["reason"] = "x" }, 1001)).Post.Content);

        // Ranking asks first (BN and admins only), then the button does it.
        Assert.Contains("Beatmap Nominators", (await commands.RunAsync("rankmap", new Dictionary<string, string> { ["beatmap"] = "10" }, 1001)).Post.Content, StringComparison.Ordinal);
        await ExecuteAsync($"INSERT INTO phpbb_user_group (group_id, user_id, group_leader, user_pending) SELECT group_id, {alpha}, 0, 0 FROM phpbb_groups WHERE identifier = 'bng'");
        var ask = await commands.RunAsync("lovemap", new Dictionary<string, string> { ["beatmap"] = "https://osu.test/b/101", ["scope"] = "map" }, 1001);
        Assert.True(ask.Private);
        Assert.Contains("Love **[Insane] only**?", ask.Post.Embed!.Description, StringComparison.Ordinal);
        Assert.Equal(["status:loved:10:101", "cancel"], ask.Post.Buttons.Select(b => b.CustomId));

        var interactions = new DiscordInteractions(services.GetRequiredService<IServiceScopeFactory>());
        Assert.Equal("Cancelled.", (await interactions.PressAsync("cancel", 1001)).Update!.Content);
        Assert.Contains("Link your account", (await interactions.PressAsync("status:loved:10:101", 9999)).Reply, StringComparison.Ordinal);
        var done = await interactions.PressAsync("status:loved:10:101", 1001);
        Assert.StartsWith("✅ 1 difficulty(ies) → **loved**", done.Update!.Content, StringComparison.Ordinal);
        Assert.Equal(4L, await ScalarAsync<long>("SELECT approved FROM osu_beatmaps WHERE beatmap_id = 101"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT approved FROM osu_beatmaps WHERE beatmap_id = 100"));

        // Report buttons are staff-only too.
        Assert.Equal("That's staff-only.", (await interactions.PressAsync($"mod:dismiss:{cheater}:-", 1001)).Reply);
        Assert.StartsWith("Dismissed by Staff", (await interactions.PressAsync($"mod:dismiss:{cheater}:-", 1002)).Note, StringComparison.Ordinal);
    }

    [MySqlFact]
    public async Task Reports_SuspiciousPlays_AndMilestones_ArePosted()
    {
        var (alpha, bravo, _, _) = await SeedAsync();
        var gateway = new DiscordFakeGateway();
        var state = new DiscordState();
        var watch = new DiscordWatch(DiscordFakeGateway.Options(), gateway, Format, state, TimeProvider.System);
        var community = new OsuWebCommunity(NewOsuWebContext());
        await watch.StartAsync(community, default);

        // A report (as the website files it, and !report).
        await community.AddReportAsync(bravo, alpha, "Cheating", "look at his cursor");
        await watch.TickAsync(community, default);
        var report = Assert.Single(gateway.In(DiscordFakeGateway.UserLog));
        Assert.Contains("**Cheating**, about the player", report.Embed!.Description, StringComparison.Ordinal);
        Assert.Equal(["Restrict", "Silence 1 day", "Dismiss", "Profile"], report.Buttons.Select(b => b.Label));
        Assert.StartsWith($"mod:restrict:{alpha}:Reported for Cheating (report ", report.Buttons[0].CustomId, StringComparison.Ordinal);
        gateway.Clear();

        // Far above their best, and faster than the map is long (Map is 120 s long).
        await InsertScoreAsync(bravo, OtherMap, 800_000, pp: 90);
        var jump = await InsertScoreAsync(bravo, Map, 900_000, pp: 400);
        foreach (var id in new[] { jump - 1, jump })
            await watch.OnScoreAsync(community, (await community.GetScoresAfterAsync(id - 1, 1)).Single(), tookFirst: false, default);
        var suspicious = Assert.Single(gateway.In(DiscordFakeGateway.UserLog)).Embed!;
        Assert.Contains("far above their best so far (90pp)", suspicious.Description, StringComparison.Ordinal);
        Assert.Matches("only [01]s after their previous pass", suspicious.Description);
        Assert.Contains("from an account made", suspicious.Description, StringComparison.Ordinal); // a big play from a new account
        gateway.Clear();

        // Milestones: the first sighting is only remembered; later gains are announced once.
        await state.SetFieldAsync("milestones", $"{bravo}:0", "1000|0|0");
        await InsertStatsAsync(bravo, 300, 98, 1200, "th");
        var noon = new MovableTime(DateTimeOffset.UtcNow);
        watch = new DiscordWatch(DiscordFakeGateway.Options(), gateway, Format, state, noon);
        await watch.OnScoreAsync(community, (await community.GetScoresAfterAsync(jump - 1, 1)).Single(), false, default);
        gateway.Clear();
        noon.Now += TimeSpan.FromMinutes(1);
        await watch.TickAsync(community, default);
        var milestone = Assert.Single(gateway.In(DiscordFakeGateway.PublicLog)).Embed!.Description;
        Assert.Contains("📈 **Bravo** reached the **top 10** in osu! (#2)!", milestone, StringComparison.Ordinal); // behind Alpha
        Assert.Contains(":flag_th: **Bravo** is now **#1 in their country** in osu!", milestone, StringComparison.Ordinal);
        Assert.Contains("played their **1,000th** osu! map", milestone, StringComparison.Ordinal);
        Assert.Equal("10|1|1000", await state.GetFieldAsync("milestones", $"{bravo}:0"));

        // Nothing new: nothing announced.
        gateway.Clear();
        await watch.OnScoreAsync(community, (await community.GetScoresAfterAsync(jump - 1, 1)).Single(), false, default);
        noon.Now += TimeSpan.FromMinutes(1);
        await watch.TickAsync(community, default);
        Assert.Empty(gateway.In(DiscordFakeGateway.PublicLog));
    }

    [MySqlFact]
    public async Task TheStatusMessage_IsEditedInPlace_AndTheMapOfTheDayIsWon()
    {
        var (alpha, _, _, _) = await SeedAsync();
        var gateway = new DiscordFakeGateway();
        var state = new DiscordState();
        var links = new InMemoryDiscordLinkRepository();
        await links.LinkAsync(alpha, 1001, DateTime.UtcNow);
        var commands = NewCommands(links, out var services);
        var options = DiscordFakeGateway.Options();
        options.StatusChannelId = 99;
        var time = new MovableTime(new DateTimeOffset(DateTime.UtcNow.Date.AddHours(12), TimeSpan.Zero));
        var daily = new DiscordDaily(options, gateway, Format, state, services.GetRequiredService<IServiceScopeFactory>(), new MatchRegistry(),
            commands, new TomYumVersion(), time, NullLogger<DiscordDaily>.Instance);

        await daily.TickAsync();
        var status = Assert.Single(gateway.Posts, p => p.ChannelId == 99);
        Assert.Contains("osu.test is online", status.Post.Embed!.Title, StringComparison.Ordinal);
        Assert.Single(gateway.Pinned);
        Assert.Equal("0 players on osu.test", gateway.Activity);
        // Today's map, from the ranked ones.
        var motd = Assert.Single(gateway.In(DiscordFakeGateway.PublicLog));
        Assert.StartsWith("🗺️ **Map of the day!**", motd.Content, StringComparison.Ordinal);
        var today = JsonDocument.Parse((await state.GetAsync("motd"))!).RootElement;
        var beatmapId = today.GetProperty("BeatmapId").GetInt32();

        gateway.Clear();
        time.Now += TimeSpan.FromMinutes(1);
        await daily.TickAsync();
        Assert.DoesNotContain(gateway.Posts, p => p.ChannelId == 99); // edited, not posted again
        Assert.Single(gateway.Pinned);

        // Alpha passes it today; tomorrow they win the role for a week, and lose it after.
        await InsertScoreAsync(alpha, beatmapId, 900_000, pp: 150, endedAt: time.Now.UtcDateTime.AddHours(-1));
        gateway.Clear();
        time.Now += TimeSpan.FromDays(1);
        await daily.TickAsync();
        var won = gateway.In(DiscordFakeGateway.PublicLog);
        Assert.Contains(won, p => p.Embed?.Description?.StartsWith("🏆 <@1001> won yesterday's map of the day!", StringComparison.Ordinal) == true);
        Assert.Contains(won, p => p.Content?.StartsWith("🗺️ **Map of the day!**", StringComparison.Ordinal) == true);
        Assert.Equal((1001UL, 777UL, true), Assert.Single(gateway.RoleChanges));

        time.Now += DiscordDaily.HoldFor;
        await daily.TickAsync();
        Assert.Equal((1001UL, 777UL, false), gateway.RoleChanges[^1]);
    }

    [MySqlFact]
    public async Task TheDailyChallenge_IsQueued_Announced_AndSettled()
    {
        var (alpha, bravo, _, _) = await SeedAsync();
        var commands = NewCommands(new InMemoryDiscordLinkRepository(), out var services);
        var scopes = services.GetRequiredService<IServiceScopeFactory>();

        // One ranked set here: one day can be queued, never two from the same set.
        var job = new DailyChallengeQueueJob(new DailyChallengeOptions { DaysAhead = 3 }, scopes, TimeProvider.System, NullLogger<DailyChallengeQueueJob>.Instance);
        Assert.Equal(1, await job.FillAsync());
        Assert.Equal(0, await job.FillAsync());
        var queued = await ScalarAsync<long>("SELECT beatmap_id FROM daily_challenge_queue");
        Assert.Contains(queued, new long[] { Map, OtherMap });
        Assert.Equal(["EZ", "NF", "HD", "HR", "SD", "PF", "FL"], OsuWebDailyChallenge.Acronyms(await ScalarAsync<string>("SELECT CAST(allowed_mods AS CHAR) FROM daily_challenge_queue")));
        Assert.Equal(2, new DailyChallengeOptions { Rulesets = "osu, catch" }.RulesetFor(DateOnly.FromDayNumber(1)));

        // osu-web opens the room (as daily-challenge:create-next does); two players take part.
        await ExecuteAsync($"""
            INSERT INTO multiplayer_rooms (id, user_id, name, starts_at, ends_at, type, category)
            VALUES (7, 1, 'Daily Challenge: Sep 29, 2026', UTC_TIMESTAMP() - INTERVAL 1 HOUR, UTC_TIMESTAMP() + INTERVAL 1 HOUR, 'playlists', 'daily_challenge');
            INSERT INTO multiplayer_playlist_items (id, room_id, beatmap_id, ruleset_id, allowed_mods, required_mods)
            SELECT 70, 7, beatmap_id, ruleset_id, allowed_mods, required_mods FROM daily_challenge_queue;
            UPDATE daily_challenge_queue SET multiplayer_room_id = 7;
            """);
        foreach (var (user, total) in new[] { (alpha, 700_000u), (bravo, 950_000u), (alpha, 800_000u) })
        {
            var score = await InsertScoreAsync(user, (int)queued, total);
            await ExecuteAsync($"INSERT INTO multiplayer_score_links (user_id, playlist_item_id, score_id) VALUES ({user}, 70, {score})");
        }

        var gateway = new DiscordFakeGateway();
        var daily = new DiscordDaily(DiscordFakeGateway.Options(), gateway, Format, new DiscordState(), scopes, new MatchRegistry(),
            commands, new TomYumVersion(), TimeProvider.System, NullLogger<DiscordDaily>.Instance);
        await daily.TickAsync();
        var open = Assert.Single(gateway.In(DiscordFakeGateway.PublicLog), p => p.Content?.StartsWith("📅", StringComparison.Ordinal) == true);
        Assert.Contains("**Daily Challenge: Sep 29, 2026** is open in osu!lazer", open.Content, StringComparison.Ordinal);
        Assert.Contains("Allowed: EZ NF HD HR SD PF FL.", open.Content, StringComparison.Ordinal);
        Assert.NotNull(open.Embed);

        gateway.Clear();
        await daily.TickAsync();
        Assert.DoesNotContain(gateway.In(DiscordFakeGateway.PublicLog), p => p.Content?.StartsWith("📅", StringComparison.Ordinal) == true);

        // It closes: the best per player, best first.
        await ExecuteAsync("UPDATE multiplayer_rooms SET ends_at = UTC_TIMESTAMP() - INTERVAL 1 MINUTE WHERE id = 7");
        await daily.TickAsync();
        var results = Assert.Single(gateway.In(DiscordFakeGateway.PublicLog), p => p.Embed?.Title?.EndsWith(": results", StringComparison.Ordinal) == true);
        var lines = results.Embed!.Description!.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("🥇 [Bravo]", lines[0], StringComparison.Ordinal);
        Assert.Contains("800,000", lines[1], StringComparison.Ordinal);
        Assert.Equal("2 player(s) took part", results.Embed.Footer);
    }

    [MySqlFact]
    public async Task Players_CanReportInGame()
    {
        var (alpha, bravo, _, _) = await SeedAsync();
        var services = new ServiceCollection()
            .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
            .AddScoped<IUserRepository>(_ => new OsuWebUserRepository(NewOsuWebContext()))
            .BuildServiceProvider();
        var bot = BanchoBotTestFactory.Commands(services, null);
        var caller = new OnlinePlayer("token", new TomYum.Bancho.BanchoPresence { UserId = alpha, Username = "Alpha" });

        Assert.StartsWith("Usage", await bot.TryHandleAsync(caller, "!report Bravo", default), StringComparison.Ordinal);
        Assert.Equal("You can't report that account.", await bot.TryHandleAsync(caller, "!report Alpha cheating", default));
        Assert.Equal("Thanks, your report on Bravo went to the staff.", await bot.TryHandleAsync(caller, "!report Bravo multi same pc as Cheater", default));
        Assert.Equal("MultipleAccounts", await ScalarAsync<string>($"SELECT reason FROM osu_user_reports WHERE user_id = {bravo}"));
        Assert.Equal("same pc as Cheater (sent in-game)", await ScalarAsync<string>($"SELECT comments FROM osu_user_reports WHERE user_id = {bravo}"));
        Assert.Equal("Thanks, your report on Bravo went to the staff.", await bot.TryHandleAsync(caller, "!report Bravo he is rude", default));
        Assert.Equal("Other", await ScalarAsync<string>($"SELECT reason FROM osu_user_reports WHERE user_id = {bravo} ORDER BY report_id DESC LIMIT 1"));
        for (var i = 0; i < 3; i++)
            await bot.TryHandleAsync(caller, "!report Bravo spam", default);
        Assert.Contains("wait a bit", await bot.TryHandleAsync(caller, "!report Bravo spam", default), StringComparison.Ordinal);
    }

    // ── set-up ─────────────────────────────────────────────────────────────────────────────

    [MySqlFact]
    public async Task Badges_AreForAdmins_AndStandingCanBeCleared()
    {
        var (alpha, bravo, cheater, staff) = await SeedAsync();
        var admin = (await new OsuWebUserRepository(NewOsuWebContext()).AddAsync(new User
        {
            Username = "Boss", SafeName = "boss", Email = "boss@x.dev", PasswordBcrypt = "x", Country = "nl",
            Privileges = UserPrivileges.Normal | UserPrivileges.Moderator | UserPrivileges.Administrator,
        })).Id;
        var links = new InMemoryDiscordLinkRepository();
        await links.LinkAsync(alpha, 1001, DateTime.UtcNow);
        await links.LinkAsync(staff, 1002, DateTime.UtcNow);
        await links.LinkAsync(admin, 1003, DateTime.UtcNow);

        // An osu! badge already in the asset cache (so no download).
        var assets = Path.Combine(Path.GetTempPath(), "tomyum-badges-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(assets, AssetsEndpoints.BadgeFolder));
        await File.WriteAllBytesAsync(Path.Combine(assets, AssetsEndpoints.BadgeFolder, "contributor.png"), [1, 2, 3]);
        var commands = NewCommands(links, out _, assets);

        var give = new Dictionary<string, string> { ["player"] = "Alpha", ["image"] = "contributor", ["description"] = "Helped out", ["url"] = "https://osu.test/wiki" };
        Assert.Equal("That command is staff-only.", (await commands.RunAsync("addbadge", give, 1001)).Post.Content);
        Assert.Equal("Only administrators can give or take badges.", (await commands.RunAsync("addbadge", give, 1002)).Post.Content);
        Assert.StartsWith("Gave **Alpha** the badge **Helped out**", (await commands.RunAsync("addbadge", give, 1003)).Post.Content, StringComparison.Ordinal);
        Assert.Contains("already has", (await commands.RunAsync("addbadge", give, 1003)).Post.Content, StringComparison.Ordinal);
        Assert.Contains("isn't an osu! badge", (await commands.RunAsync("addbadge", new Dictionary<string, string>(give) { ["image"] = "../../etc/passwd" }, 1003)).Post.Content, StringComparison.Ordinal);
        Assert.Equal("https://osu.test/wiki", await ScalarAsync<string>($"SELECT url FROM osu_badges WHERE user_id = {alpha} AND image = 'contributor.png'"));

        var list = await commands.RunAsync("removebadge", new Dictionary<string, string> { ["player"] = "Alpha" }, 1003);
        Assert.Contains("**1.** Helped out (`contributor.png`)", list.Post.Content, StringComparison.Ordinal);
        Assert.StartsWith("Took **Helped out**", (await commands.RunAsync("removebadge", new Dictionary<string, string> { ["player"] = "Alpha", ["badge"] = "1" }, 1003)).Post.Content, StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT COUNT(*) FROM osu_badges WHERE user_id = {alpha}"));

        // Standing: a silence and a note go; a restricted player keeps their restriction until it's lifted.
        var moderation = new OsuWebModeration(NewOsuWebContext());
        await moderation.SilenceAsync(bravo, staff, TimeSpan.FromDays(1), "spam");
        await ExecuteAsync($"INSERT INTO osu_user_banhistory (user_id, ban_status, period, reason, permanent, `timestamp`) VALUES ({cheater}, 1, 0, 'cheating', 1, UTC_TIMESTAMP())"); // already flagged as restricted
        Assert.NotNull(await moderation.SilenceEndAsync(bravo));
        var clear = new Dictionary<string, string> { ["player"] = "Bravo", ["reason"] = "appeal accepted" };
        Assert.Equal("That command is staff-only.", (await commands.RunAsync("clearstanding", clear, 1001)).Post.Content);
        Assert.StartsWith("Cleared 1 record(s) (everything) from **Bravo**", (await commands.RunAsync("clearstanding", clear, 1002)).Post.Content, StringComparison.Ordinal);
        Assert.Null(await moderation.SilenceEndAsync(bravo));
        Assert.Contains("nothing like that", (await commands.RunAsync("clearstanding", clear, 1002)).Post.Content, StringComparison.Ordinal);

        Assert.Contains("lift it with /unrestrict first", (await commands.RunAsync("clearstanding", new Dictionary<string, string> { ["player"] = "Cheater", ["reason"] = "x" }, 1002)).Post.Content, StringComparison.Ordinal);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT COUNT(*) FROM osu_user_banhistory WHERE user_id = {cheater}"));
        Assert.Equal("user.clear_standing", (await new StaffLogRepository(NewTomYumContext()).ListAsync(new StaffLogQuery(Limit: 1))).Single().Action);
        Directory.Delete(assets, recursive: true);
    }

    [Fact]
    public void BadgeImages_AreRedrawnToBadgeSize_AndOnlyPublicAddressesAreFetched()
    {
        using var wide = new SKBitmap(300, 100);
        wide.Erase(SKColors.Red);
        using var encoded = wide.Encode(SKEncodedImageFormat.Png, 100);
        using var badge = SKBitmap.Decode(TomYum.Server.Admin.BadgeImages.Fit(encoded.ToArray(), 172, 80));
        Assert.Equal((172, 80), (badge.Width, badge.Height));
        Assert.Equal(SKColors.Transparent.Alpha, badge.GetPixel(86, 2).Alpha); // letterboxed: 3:1 into 43:20
        Assert.Equal(SKColors.Red, badge.GetPixel(86, 40));
        Assert.Null(TomYum.Server.Admin.BadgeImages.Fit([1, 2, 3], 86, 40));

        foreach (var address in new[] { "127.0.0.1", "10.1.2.3", "172.20.0.5", "192.168.39.74", "169.254.1.1", "::1", "fd00::1", "::ffff:192.168.1.1" })
            Assert.False(TomYum.Server.Admin.BadgeImages.IsPublic(System.Net.IPAddress.Parse(address)), address);
        Assert.True(TomYum.Server.Admin.BadgeImages.IsPublic(System.Net.IPAddress.Parse("162.159.130.233")));

        IReadOnlyList<OsuWebBadge> badges = [new() { Image = "a.png", Description = "Mapping contest winner" }, new() { Image = "b.png", Description = "Tournament winner" }];
        Assert.Equal("b.png", DiscordCommands.FindBadge(badges, "2")!.Image);
        Assert.Equal("a.png", DiscordCommands.FindBadge(badges, "a")!.Image);
        Assert.Equal("b.png", DiscordCommands.FindBadge(badges, "tournament winner")!.Image);
        Assert.Null(DiscordCommands.FindBadge(badges, "winner")); // two match: ask
        Assert.Null(DiscordCommands.FindBadge(badges, "3"));
    }

    private DiscordCommands NewCommands(IDiscordLinkRepository links, out ServiceProvider services, string? assets = null)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Assets:CachePath"] = assets ?? Path.GetTempPath() })
            .Build();
        services = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(configuration)
            .AddSingleton<TomYum.Server.Admin.BadgeImages>()
            .AddScoped(_ => new OsuWebBadges(NewOsuWebContext()))
            .AddScoped(_ => new OsuWebDailyChallenge(NewOsuWebContext()))
            .AddSingleton(TimeProvider.System)
            .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
            .AddScoped(_ => new OsuWebModeration(NewOsuWebContext()))
            .AddScoped<IUserRepository>(_ => new OsuWebUserRepository(NewOsuWebContext()))
            .AddScoped<IStaffLogRepository>(_ => new StaffLogRepository(NewTomYumContext()))
            .AddScoped<IBeatmapRepository>(_ =>
            {
                var osuWeb = NewOsuWebContext();
                return new OsuWebMirroringBeatmapRepository(new BeatmapRepository(NewTomYumContext()), new OsuWebBeatmapMirror(osuWeb), osuWeb);
            })
            .AddSingleton(new BeatmapService(new NoMaps()))
            .AddScoped<TomYum.Server.Admin.BeatmapStatusChanger>()
            .AddSingleton(links)
            .BuildServiceProvider();
        return new DiscordCommands(services.GetRequiredService<IServiceScopeFactory>(), new PlayerRegistry(), new DiscordLinkCodes(), Format);
    }

    /// <summary>Two players with stats (Alpha ahead), a restricted one, a staff member, and a ranked set with two 2-minute maps.</summary>
    private async Task<(int Alpha, int Bravo, int Cheater, int Staff)> SeedAsync()
    {
        await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var alpha = await users.AddAsync(new User { Username = "Alpha", SafeName = "alpha", Email = "a@x.dev", PasswordBcrypt = "x", Country = "nl" });
        var bravo = await users.AddAsync(new User { Username = "Bravo", SafeName = "bravo", Email = "b@x.dev", PasswordBcrypt = "x", Country = "th" });
        var cheater = await users.AddAsync(new User { Username = "Cheater", SafeName = "cheater", Email = "c@x.dev", PasswordBcrypt = "x", Country = "nl" });
        var staff = await users.AddAsync(new User { Username = "Staff", SafeName = "staff", Email = "s@x.dev", PasswordBcrypt = "x", Country = "nl", Privileges = UserPrivileges.Normal | UserPrivileges.Moderator });
        await ExecuteAsync($"UPDATE phpbb_users SET user_warnings = 1, user_ip = '203.0.113.9' WHERE user_id = {cheater.Id}");
        await ExecuteAsync($"UPDATE phpbb_users SET user_regdate = UNIX_TIMESTAMP() - 86400 * 400 WHERE user_id = {alpha.Id}");

        // Through the mirroring repository: TomYum's copy (status changes read it) and the website's.
        var osuWeb = NewOsuWebContext();
        var beatmaps = new OsuWebMirroringBeatmapRepository(new BeatmapRepository(NewTomYumContext()), new OsuWebBeatmapMirror(osuWeb), osuWeb);
        foreach (var (id, version, stars) in new[] { (Map, "Hard", 4.5), (OtherMap, "Insane", 5.5) })
        {
            await beatmaps.UpsertAsync(new Beatmap
            {
                Id = id, SetId = 10, Md5 = $"md5-{id}", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = version, Artist = "Artist",
                Title = "Song", Creator = "Mapper", StarRating = stars, TotalLength = 120,
                Details = new BeatmapDetails { CircleSize = 4, ApproachRate = 9, OverallDifficulty = 8, DrainRate = 5, Bpm = 120, HitLength = 110 },
            });
        }
        await InsertStatsAsync(alpha.Id, 500, 98.5, 3000, "nl");
        await InsertStatsAsync(bravo.Id, 300, 97, 800, "th");
        return (alpha.Id, bravo.Id, cheater.Id, staff.Id);
    }

    private async Task<ulong> InsertScoreAsync(int userId, int beatmapId, uint total, double? pp = null, DateTime? endedAt = null)
    {
        var ppValue = pp is { } value ? value.ToString(CultureInfo.InvariantCulture) : "NULL";
        var ended = endedAt is { } at ? $"'{at:yyyy-MM-dd HH:mm:ss}'" : "UTC_TIMESTAMP()";
        await ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $$"""
            INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, max_combo, total_score, data, pp, legacy_score_id, legacy_total_score, ended_at)
            SELECT {{userId}}, 0, {{beatmapId}}, 1, 1, 'S', 1, 0.97, 400, {{total}}, '{{Data}}', {{ppValue}},
              (SELECT COALESCE(MAX(x.id), 0) + 1000 FROM scores x), {{total}}, {{ended}}
            """));
        return await ScalarAsync<ulong>("SELECT MAX(id) FROM scores");
    }

    private const string Data = """{"mods":[{"acronym":"CL"}],"statistics":{"great":300}}""";

    private Task InsertStatsAsync(int userId, double pp, double accuracy, int plays, string country) =>
        ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO osu_user_stats (user_id, accuracy_total, accuracy_count, accuracy, playcount, ranked_score, total_score,
              x_rank_count, s_rank_count, a_rank_count, `rank`, level, country_acronym, rank_score, rank_score_index, accuracy_new, total_seconds_played)
            VALUES ({userId}, 0, 0, 0, {plays}, 1000, 2000, 1, 2, 3, 0, 50.5, '{country}', {pp}, 0, {accuracy}, 3700)
            ON DUPLICATE KEY UPDATE rank_score = VALUES(rank_score), playcount = VALUES(playcount)
            """));

    private sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoMaps : IOsuApiClient
    {
        public bool SupportsSince => false;

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);
    }
}
