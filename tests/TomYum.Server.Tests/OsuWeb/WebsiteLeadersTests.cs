using System.Globalization;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>The website's first places (<see cref="OsuWebLeaders"/>) and its recent-activity medal entries.</summary>
public sealed class WebsiteLeadersTests : OsuWebDatabaseTest
{
    private const string Data = """{"mods":[{"acronym":"CL"}],"statistics":{"great":300}}""";

    [MySqlFact]
    public async Task FirstPlaces_FollowTheLeaderboards_ForBothViews()
    {
        await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var alpha = (await users.AddAsync(NewUser("Alpha"))).Id;
        var bravo = (await users.AddAsync(NewUser("Bravo"))).Id;
        var cheater = (await users.AddAsync(NewUser("Cheater"))).Id;
        await ExecuteAsync($"UPDATE phpbb_users SET user_warnings = 1 WHERE user_id = {cheater}");
        var mirror = new OsuWebBeatmapMirror(NewOsuWebContext());
        await mirror.WriteAsync(new Beatmap { Id = 100, SetId = 10, Md5 = "a", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "Hard", Artist = "A", Title = "T", Creator = "M" });
        await mirror.WriteAsync(new Beatmap { Id = 101, SetId = 11, Md5 = "b", Status = RankedStatus.Pending, OsuApiStatus = 0, Version = "WIP", Artist = "A", Title = "T", Creator = "M" });

        await InsertAsync(alpha, 100, total: 900_000, legacyTotal: 5_000_000, stable: true);
        var bravoLazer = await InsertAsync(bravo, 100, total: 950_000, legacyTotal: 0, stable: false); // best overall, but lazer
        await InsertAsync(cheater, 100, total: 999_999, legacyTotal: 9_999_999, stable: true); // restricted: never first
        await InsertAsync(alpha, 101, total: 999_999, legacyTotal: 1, stable: true); // no leaderboard: never first

        var leaders = new OsuWebLeaders(NewOsuWebContext());
        await leaders.RebuildAsync();
        Assert.Equal((long)bravo, await ScalarAsync<long>("SELECT user_id FROM beatmap_leaders WHERE beatmap_id = 100 AND ruleset_id = 0"));
        Assert.Equal(bravoLazer, await ScalarAsync<ulong>("SELECT score_id FROM beatmap_leaders WHERE beatmap_id = 100"));
        Assert.Equal((long)alpha, await ScalarAsync<long>("SELECT user_id FROM osu_leaders WHERE beatmap_id = 100"));
        Assert.Equal(await ScalarAsync<long>($"SELECT legacy_score_id FROM scores WHERE user_id = {alpha} AND beatmap_id = 100"),
            await ScalarAsync<long>("SELECT score_id FROM osu_leaders WHERE beatmap_id = 100"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM beatmap_leaders"));

        // A new best: seen from the scores since the last look.
        var mark = await leaders.NewestScoreIdAsync();
        await InsertAsync(alpha, 100, total: 990_000, legacyTotal: 6_000_000, stable: true);
        var (changed, newest) = await leaders.ChangedSinceAsync(mark);
        Assert.Equal([(100, 0)], changed);
        Assert.True(newest > mark);
        await leaders.SyncAsync(changed);
        Assert.Equal((long)alpha, await ScalarAsync<long>("SELECT user_id FROM beatmap_leaders WHERE beatmap_id = 100"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_leaders"));
    }

    [MySqlFact]
    public async Task MedalActivity_ForMedalsAPlayerDoesntHave_IsRemoved()
    {
        await NewTomYumContextAsync();
        var alpha = (await new OsuWebUserRepository(NewOsuWebContext()).AddAsync(NewUser("Alpha"))).Id;
        await ExecuteAsync("INSERT INTO osu_achievements (achievement_id, name, slug, `grouping`, ordering, progression) VALUES (1, 'Rising Star', 's1', 'Skill', 1, 1), (2, 'Don''t Stop', 's2', 'Skill', 2, 1), (3, 'Totality', 's3', 'Skill', 3, 1)");
        await ExecuteAsync($"INSERT INTO osu_user_achievements (user_id, achievement_id) VALUES ({alpha}, 1), ({alpha}, 2)");
        string Event(string medal) => $"('<b><a href=''/users/{alpha}''>Alpha</a></b> unlocked the \"<b>{medal}</b>\" medal!', {alpha})";
        await ExecuteAsync($"INSERT INTO osu_events (text, user_id) VALUES {Event("Rising Star")}, {Event("Don&#039;t Stop")}, {Event("Totality")}, ('<b>Alpha</b> achieved rank #1 on a map', {alpha})");

        var removed = await new OsuWebMedals(NewOsuWebContext(), NewTomYumContext()).RemoveStaleMedalEventsAsync();

        Assert.Equal(1, removed); // Totality: not theirs
        Assert.Equal(3L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_events"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_events WHERE text LIKE '%Totality%'"));
    }

    private async Task<ulong> InsertAsync(int userId, int beatmapId, uint total, uint legacyTotal, bool stable)
    {
        var legacy = stable ? "(SELECT COALESCE(MAX(x.id), 0) + 1000 FROM scores x)" : "NULL";
        await ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $$"""
            INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, max_combo, total_score, data, pp, legacy_score_id, legacy_total_score, ended_at)
            SELECT {{userId}}, 0, {{beatmapId}}, 1, 1, 'S', 1, 0.97, 400, {{total}}, '{{Data}}', 100, {{legacy}}, {{legacyTotal}}, UTC_TIMESTAMP()
            """));
        return await ScalarAsync<ulong>("SELECT MAX(id) FROM scores");
    }

    private static User NewUser(string name) => new()
    {
        Username = name,
        SafeName = User.MakeSafeName(name),
        Email = $"{name.ToLowerInvariant()}@x.dev",
        PasswordBcrypt = "x",
        Country = "nl",
    };
}
