using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Caching;
using TomYum.Domain.Entities;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Achievements;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary><see cref="OsuWebMedals"/> and the medal path of <see cref="OsuWebSync"/> against osu-web's medal tables.</summary>
public sealed class OsuWebMedalsDatabaseTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task WriteCatalog_AddsAndUpdates_AndWithTheFullList_RemovesOthers()
    {
        await using var tomyum = await NewTomYumContextAsync();
        // What the earlier numbering left: id 21 was "500 Combo" and id 2 isn't an osu! medal.
        await ExecuteAsync(
            "INSERT INTO osu_achievements (achievement_id, name, slug, `grouping`, ordering, progression) " +
            "VALUES (21, '500 Combo', 'osu-combo-500', 'Skill', 0, 1), (2, 'Constellation Prize', 'osu-skill-pass-2', 'Skill', 1, 2)");
        var medals = new OsuWebMedals(NewOsuWebContext(), tomyum);

        // Offline: TomYum's own medals under osu!'s ids; nothing else removed.
        Assert.Equal(MedalCatalog.All.Count, await medals.WriteCatalogAsync(OsuWebMedals.CatalogRows(), complete: false));
        Assert.Equal("taiko-skill-fc-3", await ScalarAsync<string>("SELECT slug FROM osu_achievements WHERE achievement_id = 97"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT mode FROM osu_achievements WHERE achievement_id = 97"));
        Assert.Null(await ScalarAsync<int?>("SELECT mode FROM osu_achievements WHERE slug = 'all-intro-hidden'"));
        Assert.Equal(2L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_achievements WHERE achievement_id IN (2, 21)"));

        // osu!'s full list: rows brought up to date, the rest removed.
        OsuWebMedalRow[] official =
        [
            new(1, "500 Combo", "osu-combo-500", "d", "Skill & Dedication", 0, 1, 0),
            new(55, "Rising Star", "osu-skill-pass-1", "d", "Skill & Dedication", 1, 1, 0, "<i>Pass a 1* map</i>"),
            new(265, "Mappers' Guild Pack VII", "all-packs-mappersguild-07", "d", "Beatmap Challenge Packs", 6, 1, null),
        ];
        await medals.WriteCatalogAsync(official, complete: true);
        Assert.Equal([1L, 55L, 265L], await ListAsync("SELECT achievement_id FROM osu_achievements ORDER BY achievement_id"));
        Assert.Equal("<i>Pass a 1* map</i>", await ScalarAsync<string>("SELECT quest_instructions FROM osu_achievements WHERE achievement_id = 55"));
        Assert.Equal("Skill & Dedication", await ScalarAsync<string>("SELECT `grouping` FROM osu_achievements WHERE achievement_id = 1"));
    }

    [MySqlFact]
    public async Task Rebuild_JudgesStablePlaysAgain_AndClearsTheOldNumbers_Once()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var player = (await users.AddAsync(new User { Username = "Player", SafeName = "player", Email = "p@x.dev", PasswordBcrypt = "x", Country = "nl" })).Id;
        var earlier = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var beatmaps = new TomYum.Persistence.Repositories.BeatmapRepository(tomyum);
        // A 3.2* osu! map; played as catch it's 2.4* (the old rules took 3.2 either way).
        await beatmaps.UpsertAsync(new Beatmap { Id = 100, SetId = 10, Md5 = "md5-100", Mode = 0, StarRating = 3.2, Status = TomYum.Domain.Enums.RankedStatus.Ranked, Version = "v", Artist = "a", Title = "t", Creator = "c" });
        tomyum.Scores.Add(new Score { UserId = player, BeatmapId = 100, BeatmapMd5 = "md5-100", Mode = 2, Passed = true, Grade = "A", MaxCombo = 300, Status = ScoreStatus.Best, SubmittedAt = earlier });
        // What the old rules gave: catch pass 3* (now id 81), copied to the website as bancho.py's 43.
        tomyum.UserAchievements.Add(new UserAchievement { UserId = player, AchievementId = 81, UnlockedAt = earlier });
        await tomyum.SaveChangesAsync();
        tomyum.ChangeTracker.Clear();
        await ExecuteAsync($"INSERT INTO osu_user_achievements (user_id, achievement_id, `date`) VALUES ({player}, 43, '2026-09-01 10:00:01')");
        // A lazer medal from ppy's processor that happens to share an old number (another time): kept.
        await ExecuteAsync($"INSERT INTO osu_user_achievements (user_id, achievement_id, `date`) VALUES ({player}, 57, '2026-09-20 18:00:00')");

        var rebuild = new MedalRebuild(tomyum, NewOsuWebContext(), new OsuWebMedals(NewOsuWebContext(), tomyum), beatmaps, users, new CatchStars());
        var result = await rebuild.RunIfPendingAsync();

        Assert.Equal(new MedalRebuildResult(1, 1, 1, 1), result);
        Assert.Equal([80], tomyum.UserAchievements.AsEnumerable().Select(u => u.AchievementId)); // catch pass 2*
        Assert.Equal([57L, 80L], await ListAsync($"SELECT achievement_id FROM osu_user_achievements WHERE user_id = {player} ORDER BY achievement_id"));
        Assert.Equal(earlier, await ScalarAsync<DateTime>($"SELECT `date` FROM osu_user_achievements WHERE achievement_id = 80"));
        Assert.Null(await rebuild.RunIfPendingAsync()); // done once
    }

    /// <summary>Star ratings for catch as the calculator would give them (2.4* for the test map).</summary>
    private sealed class CatchStars : TomYum.Performance.IPerformanceCalculator, TomYum.Performance.IAccuracyPerformance
    {
        public Task<double> CalculateAsync(TomYum.Performance.PerformanceRequest request, CancellationToken cancellationToken = default) => Task.FromResult(0.0);

        public Task<IReadOnlyList<double>?> ForAccuraciesAsync(int beatmapId, int rulesetId, int mods, IReadOnlyList<double> accuracies, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<double>?>(null);

        public Task<double?> StarRatingAsync(int beatmapId, int rulesetId, int mods, CancellationToken cancellationToken = default) =>
            Task.FromResult<double?>(rulesetId == 2 ? 2.4 : 3.2);
    }

    [MySqlFact]
    public async Task CopyUnlocks_OnlyForOsuWebAccounts_AndOnce()
    {
        await using var tomyum = await NewTomYumContextAsync();
        await ExecuteAsync(
            "INSERT INTO phpbb_users (user_id, username, username_clean, user_sig, user_occ, user_interests) VALUES (3, 'P', 'p', '', '', '')");
        tomyum.UserAchievements.AddRange(
            new UserAchievement { UserId = 3, AchievementId = 21, UnlockedAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
            new UserAchievement { UserId = 77, AchievementId = 21, UnlockedAt = DateTime.UtcNow }); // no osu-web account
        await tomyum.SaveChangesAsync();
        var medals = new OsuWebMedals(NewOsuWebContext(), tomyum);

        Assert.Equal(1, await medals.CopyUnlocksAsync());
        Assert.Equal(0, await medals.CopyUnlocksAsync());
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5), await ScalarAsync<DateTime>("SELECT `date` FROM osu_user_achievements WHERE user_id = 3"));
    }

    [MySqlFact]
    public async Task MedalsUnlocked_WithoutInterop_WritesTheUnlockDirectly()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var sync = NewSync(tomyum, new OsuWebInteropOptions(), handler: null);

        await sync.MedalsUnlockedAsync(3, [Medal(1), Medal(124)], beatmapId: 100);
        await sync.MedalsUnlockedAsync(3, [Medal(1)], beatmapId: 100); // repeat is harmless

        Assert.Equal(2L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_user_achievements WHERE user_id = 3"));
        Assert.Equal(100, await ScalarAsync<int>("SELECT beatmap_id FROM osu_user_achievements WHERE achievement_id = 1"));
    }

    [MySqlFact]
    public async Task MedalsUnlocked_UsesInterop_AndFallsBackWhenItFails()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var options = new OsuWebInteropOptions { InteropUrl = "http://nginx", InteropSecret = "s" };

        // Interop succeeds: osu-web does the insert, TomYum writes nothing itself.
        await NewSync(tomyum, options, new StatusHandler(HttpStatusCode.OK)).MedalsUnlockedAsync(3, [Medal(1)], 100);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_user_achievements"));

        // Interop down: the unlock is written directly so the profile still shows it.
        await NewSync(tomyum, options, new StatusHandler(HttpStatusCode.BadGateway)).MedalsUnlockedAsync(3, [Medal(1)], 100);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_user_achievements"));
    }

    private OsuWebSync NewSync(TomYumDbContext tomyum, OsuWebInteropOptions options, HttpMessageHandler? handler) => new(
        new OsuWebScoreMirror(tomyum, NewOsuWebContext()),
        new OsuWebScoreIndexing(null!, string.Empty), // not used by the medal path
        new OsuWebMedals(NewOsuWebContext(), tomyum),
        new OsuWebInterop(handler is null ? new HttpClient() : new HttpClient(handler), options),
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
        new OsuWebPerformanceSync(new OsuWebScoreMirror(tomyum, NewOsuWebContext()), new TomYum.Persistence.Repositories.UserStatsRepository(tomyum, new TomYum.Domain.Services.RestrictedUsers()), new TomYum.Server.Bancho.PlayerRegistry()),
        NullLogger<OsuWebSync>.Instance);

    private async Task<List<long>> ListAsync(string sql)
    {
        var rows = new List<long>();
        await using var connection = new MySqlConnector.MySqlConnection(OsuWebConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlConnector.MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        return rows;
    }

    private static Medal Medal(int id) => MedalCatalog.All.Single(m => m.Id == id);

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
