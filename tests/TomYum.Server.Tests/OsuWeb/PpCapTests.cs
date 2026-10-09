using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>The pp cap: over the mode's cap and not whitelisted is restricted.</summary>
public sealed class PpCapTests : OsuWebDatabaseTest
{
    [Fact]
    public void Settings_HaveDefaults_AndPickTheCapByModeAndMods()
    {
        var defaults = PpCapSettings.From(new Dictionary<string, string?>());
        Assert.True(defaults.Enabled);
        Assert.Equal(1500, defaults.CapFor(0, []));
        Assert.Equal(2500, defaults.CapFor(0, ["HD", "RX"]));
        Assert.Equal(2500, defaults.CapFor(2, ["AP"]));

        var set = PpCapSettings.From(new Dictionary<string, string?>
        {
            ["ppcap_enabled"] = "0", ["ppcap_mania"] = "900", ["ppcap_relax"] = "3000", ["ppcap_taiko"] = "nonsense",
        });
        Assert.False(set.Enabled);
        Assert.Equal(900, set.CapFor(3, []));
        Assert.Equal(3000, set.CapFor(3, ["RX"]));
        Assert.Equal(1500, set.CapFor(1, []));
    }

    [MySqlFact]
    public async Task OverTheCap_IsRestricted_UnlessWhitelisted()
    {
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var cheater = await users.AddAsync(NewUser("Cheater"));
        var clean = await users.AddAsync(NewUser("Clean"));
        var trusted = await users.AddAsync(NewUser("Trusted"));
        await new OsuWebBeatmapMirror(NewOsuWebContext()).WriteAsync(new Beatmap { Id = 100, SetId = 10, Md5 = "a", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "v", Artist = "a", Title = "t", Creator = "c" });
        await ExecuteAsync($"INSERT INTO tomyum_account_flags (user_id, whitelisted_at) VALUES ({trusted.Id}, UTC_TIMESTAMP())");
        await ExecuteAsync("INSERT INTO tomyum_site_settings (name, value) VALUES ('ppcap_osu', '1000'), ('ppcap_relax', '2000')");

        var tomyum = await NewTomYumContextAsync();
        var players = new PlayerRegistry();
        var services = new ServiceCollection()
            .AddScoped(_ => new OsuWebCommunity(NewOsuWebContext()))
            .AddScoped(_ => new OsuWebPpCaps(NewOsuWebContext()))
            .AddScoped(_ => new OsuWebAccountFlags(NewOsuWebContext()))
            .AddScoped(_ => new OsuWebModeration(NewOsuWebContext()))
            .AddSingleton<IStaffLogRepository>(new StaffLogRepository(tomyum))
            .AddSingleton(new UserModerationSync(users, players, new TomYum.Domain.Services.RestrictedUsers(), TimeProvider.System, NullLogger<UserModerationSync>.Instance))
            .BuildServiceProvider();
        var watch = new PpCapWatch(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<PpCapWatch>.Instance);
        await watch.TickAsync(); // notes where to start

        await InsertScoreAsync(clean.Id, "[]", 999);              // under the osu! cap
        await InsertScoreAsync(clean.Id, "[{\"acronym\":\"RX\"}]", 1500); // over osu!'s, under Relax's
        await InsertScoreAsync(cheater.Id, "[]", 1200);
        await InsertScoreAsync(trusted.Id, "[]", 5000);
        await watch.TickAsync();

        Assert.True((await users.GetByIdAsync(cheater.Id))!.IsRestricted);
        Assert.False((await users.GetByIdAsync(clean.Id))!.IsRestricted);
        Assert.False((await users.GetByIdAsync(trusted.Id))!.IsRestricted);
        var log = await new StaffLogRepository(tomyum).ListAsync(new StaffLogQuery(TargetId: cheater.Id));
        Assert.Contains(log, e => e.Action == "user.restrict" && e.ActorId == BanchoBot.UserId && e.Summary.Contains("the cap is 1000pp", StringComparison.Ordinal));
    }

    private async Task InsertScoreAsync(int userId, string mods, double pp)
    {
        var data = "{\"mods\":" + mods + ",\"statistics\":{\"great\":100},\"maximum_statistics\":{\"great\":100}}";
        await ExecuteAsync(
            "INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, max_combo, total_score, data, pp, legacy_score_id, ended_at) " +
            $"SELECT {userId}, 0, 100, 1, 1, 'S', 1, 0.97, 100, 900000, '{data}', {pp.ToString(System.Globalization.CultureInfo.InvariantCulture)}, (SELECT COALESCE(MAX(x.id), 0) + 1000 FROM scores x), UTC_TIMESTAMP()");
    }

    private static User NewUser(string name) => new()
    {
        Username = name,
        SafeName = User.MakeSafeName(name),
        Email = $"{User.MakeSafeName(name)}@example.com",
        PasswordBcrypt = "x",
        Country = "nl",
        Privileges = UserPrivileges.Normal,
    };
}
