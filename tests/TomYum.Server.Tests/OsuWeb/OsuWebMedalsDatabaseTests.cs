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
    public async Task WriteCatalog_AddsMissingMedalsOnly()
    {
        await using var tomyum = await NewTomYumContextAsync();
        await ExecuteAsync(
            "INSERT INTO osu_achievements (achievement_id, name, slug, `grouping`, ordering, progression) " +
            "VALUES (21, 'Imported name', 'osu-combo-500', 'Official', 0, 0)");
        var medals = new OsuWebMedals(NewOsuWebContext(), tomyum);

        var added = await medals.WriteCatalogAsync();
        var again = await medals.WriteCatalogAsync();

        Assert.Equal(MedalCatalog.All.Count - 1, added);
        Assert.Equal(0, again);
        Assert.Equal("Imported name", await ScalarAsync<string>("SELECT name FROM osu_achievements WHERE achievement_id = 21"));
        Assert.Equal("taiko-skill-fc-3", await ScalarAsync<string>("SELECT slug FROM osu_achievements WHERE achievement_id = 35"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT mode FROM osu_achievements WHERE achievement_id = 35"));
        Assert.Null(await ScalarAsync<int?>("SELECT mode FROM osu_achievements WHERE slug = 'all-intro-hidden'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT enabled FROM osu_achievements WHERE achievement_id = 35"));
        Assert.Equal(4, await ScalarAsync<int>("SELECT progression FROM osu_achievements WHERE achievement_id = 24")); // 2000 combo
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

        await sync.MedalsUnlockedAsync(3, [Medal(21), Medal(74)], beatmapId: 100);
        await sync.MedalsUnlockedAsync(3, [Medal(21)], beatmapId: 100); // repeat is harmless

        Assert.Equal(2L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_user_achievements WHERE user_id = 3"));
        Assert.Equal(100, await ScalarAsync<int>("SELECT beatmap_id FROM osu_user_achievements WHERE achievement_id = 21"));
    }

    [MySqlFact]
    public async Task MedalsUnlocked_UsesInterop_AndFallsBackWhenItFails()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var options = new OsuWebInteropOptions { InteropUrl = "http://nginx", InteropSecret = "s" };

        // Interop succeeds: osu-web does the insert, TomYum writes nothing itself.
        await NewSync(tomyum, options, new StatusHandler(HttpStatusCode.OK)).MedalsUnlockedAsync(3, [Medal(21)], 100);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_user_achievements"));

        // Interop down: the unlock is written directly so the profile still shows it.
        await NewSync(tomyum, options, new StatusHandler(HttpStatusCode.BadGateway)).MedalsUnlockedAsync(3, [Medal(21)], 100);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_user_achievements"));
    }

    private OsuWebSync NewSync(TomYumDbContext tomyum, OsuWebInteropOptions options, HttpMessageHandler? handler) => new(
        new OsuWebScoreMirror(tomyum, NewOsuWebContext()),
        new OsuWebScoreIndexing(null!, string.Empty), // not used by the medal path
        new OsuWebMedals(NewOsuWebContext(), tomyum),
        new OsuWebInterop(handler is null ? new HttpClient() : new HttpClient(handler), options),
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
        NullLogger<OsuWebSync>.Instance);

    private static Medal Medal(int id) => MedalCatalog.All.Single(m => m.Id == id);

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
