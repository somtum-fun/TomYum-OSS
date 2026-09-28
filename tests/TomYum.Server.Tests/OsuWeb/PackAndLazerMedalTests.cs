using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Caching;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Achievements;
using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Pack medals (<see cref="PackMedalAwarder"/>) and lazer medals judged like stable's (<see cref="LazerMedalJudge"/>).</summary>
public sealed class PackAndLazerMedalTests : OsuWebDatabaseTest
{
    [Fact]
    public void LazerData_GivesStableMods_TheFullCombo_AndSpotsUnrankedMods()
    {
        const string data = """{"mods":[{"acronym":"HD"},{"acronym":"CL"},{"acronym":"TD"}],"statistics":{"great":300},"maximum_statistics":{"great":300,"slider_tail_hit":50,"large_bonus":4,"small_tick_hit":10}}""";
        Assert.Equal(new LazerMedalFacts(Mods.Hidden, false, true), LazerMedalJudge.Read(data, 350));
        Assert.False(LazerMedalJudge.Read(data, 349).FullCombo);

        Assert.True(LazerMedalJudge.Read("""{"mods":[{"acronym":"DA","settings":{"approach_rate":10}}]}""", 1).UnrankedMods);
        Assert.True(LazerMedalJudge.Read("""{"mods":[{"acronym":"DT","settings":{"speed_change":1.3}}]}""", 1).UnrankedMods);
        Assert.Equal(Mods.Nightcore | Mods.DoubleTime, LazerMedalJudge.Read("""{"mods":[{"acronym":"NC"}]}""", 1).Mods);
    }

    [Fact]
    public void LazerPlays_EarnSkillAndIntroMedals_ButNotComboOnes()
    {
        var facts = new LazerMedalFacts(Mods.Hidden, false, true);
        Assert.Equal([58, 66, 124], LazerMedalJudge.Earned(facts, 0, 4.3, RankedStatus.Ranked, 1).Select(m => m.Id).Order());
        Assert.Empty(LazerMedalJudge.Earned(facts with { UnrankedMods = true }, 0, 4.3, RankedStatus.Ranked, 1));
    }

    [Fact]
    public void TheCatalog_HasOsusPackMedals_WithTheirWholePacks()
    {
        var all = PackMedalCatalog.All;
        Assert.Equal(109, all.Count);
        Assert.Equal(all.Count, all.Select(m => m.Id).Distinct().Count());
        Assert.All(all, m => Assert.All(m.Packs, p => Assert.NotEmpty(p.Sets)));
        // Video Game Pack vol.2 is one theme pack; a Project Loved season is four, one per ruleset.
        Assert.Single(all.Single(m => m.Id == 11).Packs);
        Assert.Equal([0, 1, 2, 3], all.Single(m => m.Id == 311).Packs.Select(p => p.Ruleset ?? -1).Order());
        Assert.True(all.Single(m => m.Id == 247).NoReductionMods); // Camellia II is a challenge pack
    }

    [MySqlFact]
    public async Task APackMedal_NeedsEverySetOfThePack()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var (player, _) = await SeedAsync();
        var medal = new PackMedal(7, false, [new PackMedalPack("T1", null, [10, 20])]);
        var awarder = NewAwarder(tomyum, medal);

        var first = await InsertScoreAsync(player, 100, "[]");      // set 10
        Assert.Equal(0, await awarder.CheckAsync([first]));
        var second = await InsertScoreAsync(player, 200, "[]");     // set 20
        Assert.Equal(1, await awarder.CheckAsync([second]));
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT COUNT(*) FROM osu_user_achievements WHERE user_id = {player} AND achievement_id = 7"));
        Assert.Equal(0, await awarder.CheckAsync([second])); // already has it
    }

    [MySqlFact]
    public async Task ChallengePacks_DontCountEasierMods_AndAPackWithAMissingSetIsNeverDone()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var (player, _) = await SeedAsync();
        var challenge = new PackMedal(247, true, [new PackMedalPack("A74", null, [10])]);
        var incomplete = new PackMedal(11, false, [new PackMedalPack("T5", null, [10, 99999])]); // 99999 isn't on the server
        var awarder = NewAwarder(tomyum, challenge, incomplete);

        var easy = await InsertScoreAsync(player, 100, """[{"acronym":"EZ"}]""", pp: 50);
        Assert.Equal(0, await awarder.CheckAsync([easy]));
        var clean = await InsertScoreAsync(player, 100, """[{"acronym":"HD"}]""", pp: 60);
        Assert.Equal(1, await awarder.CheckAsync([clean]));
        Assert.Equal([(player, 247)], (await awarder.EarnedAsync()).Select(e => (e.UserId, e.MedalId)));
    }

    [MySqlFact]
    public async Task LocalPacks_MoveClearOfOsusNumbers()
    {
        await NewTomYumContextAsync();
        await ExecuteAsync("INSERT INTO osu_beatmappacks (pack_id, url, name, author, tag) VALUES (48, '', 'Local', '', 'S1')");
        await ExecuteAsync("INSERT INTO osu_beatmappacks_items (pack_id, beatmapset_id) VALUES (48, 10)");
        var completion = new OsuWebPackCompletion(NewOsuWebContext());

        Assert.Equal(1, await completion.MovePacksOutOfOsusNumbersAsync(PackMedalAwarder.LocalPackFloor));
        Assert.Equal(0, await completion.MovePacksOutOfOsusNumbersAsync(PackMedalAwarder.LocalPackFloor));
        Assert.Equal(20048, await ScalarAsync<int>("SELECT pack_id FROM osu_beatmappacks WHERE tag = 'S1'"));
        Assert.Equal(20048, await ScalarAsync<int>("SELECT pack_id FROM osu_beatmappacks_items WHERE beatmapset_id = 10"));
        await ExecuteAsync("INSERT INTO osu_beatmappacks (url, name, author, tag) VALUES ('', 'New', '', 'S2')");
        Assert.True(await ScalarAsync<int>("SELECT pack_id FROM osu_beatmappacks WHERE tag = 'S2'") > 20048);
    }

    [MySqlFact]
    public async Task TheLazerRebuild_ReplacesMedalsFromOtherRulesets_AndClearsOldNotifications()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var (player, users) = await SeedAsync();
        // A lazer HD pass of a ranked osu! map (4.3* with the calculator), full combo.
        await InsertScoreAsync(player, 100, """[{"acronym":"HD"},{"acronym":"CL"}]""", lazer: true);
        // What the processor gave with the old catalog: taiko 4* and catch 4* for that osu! play.
        await ExecuteAsync($"INSERT INTO osu_user_achievements (user_id, achievement_id, `date`) VALUES ({player}, 74, UTC_TIMESTAMP()), ({player}, 82, UTC_TIMESTAMP())");
        await ExecuteAsync($"INSERT INTO notifications (notifiable_type, notifiable_id, name, details, created_at) VALUES ('user', {player}, 'user_achievement_unlock', '{{}}', UTC_TIMESTAMP() - INTERVAL 1 HOUR)");
        await ExecuteAsync($"INSERT INTO user_notifications (notification_id, user_id) SELECT MAX(id), {player} FROM notifications");

        var rebuild = new MedalRebuild(tomyum, NewOsuWebContext(), new OsuWebMedals(NewOsuWebContext(), tomyum),
            new TomYum.Persistence.Repositories.BeatmapRepository(tomyum), users, new FixedStars(4.3), NewAwarder(tomyum));
        var result = await rebuild.RunLazerIfPendingAsync();

        Assert.Equal(new LazerMedalRebuildResult(1, 3, 2, 0, 1), result);
        Assert.Equal([58L, 66L, 124L], await IdsAsync($"SELECT achievement_id FROM osu_user_achievements WHERE user_id = {player} ORDER BY achievement_id"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM notifications"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM user_notifications"));
        Assert.Null(await rebuild.RunLazerIfPendingAsync());
    }

    private PackMedalAwarder NewAwarder(TomYumDbContext tomyum, params PackMedal[] medals) => new(
        new OsuWebPackCompletion(NewOsuWebContext()),
        new OsuWebSync(
            new OsuWebScoreMirror(tomyum, NewOsuWebContext()),
            new OsuWebScoreIndexing(null!, string.Empty),
            new OsuWebMedals(NewOsuWebContext(), tomyum),
            new OsuWebInterop(new HttpClient(), new OsuWebInteropOptions()),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new OsuWebPerformanceSync(new OsuWebScoreMirror(tomyum, NewOsuWebContext()), new TomYum.Persistence.Repositories.UserStatsRepository(tomyum, new TomYum.Domain.Services.RestrictedUsers()), new TomYum.Server.Bancho.PlayerRegistry()),
            NullLogger<OsuWebSync>.Instance),
        medals);

    /// <summary>A player, and two ranked osu! maps in sets 10 and 20.</summary>
    private async Task<(int Player, OsuWebUserRepository Users)> SeedAsync()
    {
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var player = await users.AddAsync(new User { Username = "Player", SafeName = "player", Email = "p@x.dev", PasswordBcrypt = "x", Country = "nl" });
        var beatmaps = new OsuWebBeatmapMirror(NewOsuWebContext());
        await beatmaps.WriteAsync(new Beatmap { Id = 100, SetId = 10, Md5 = "a", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "v", Artist = "a", Title = "t", Creator = "c" });
        await beatmaps.WriteAsync(new Beatmap { Id = 200, SetId = 20, Md5 = "b", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "v", Artist = "a", Title = "t", Creator = "c" });
        return (player.Id, users);
    }

    private async Task<ulong> InsertScoreAsync(int userId, int beatmapId, string mods, double? pp = 1, bool lazer = false)
    {
        var data = "{\"mods\":" + mods + ",\"statistics\":{\"great\":100},\"maximum_statistics\":{\"great\":100}}";
        var legacy = lazer ? "NULL" : "(SELECT COALESCE(MAX(x.id), 0) + 1000 FROM scores x)";
        var ppText = pp is { } value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";
        await ExecuteAsync(
            "INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, max_combo, total_score, data, pp, legacy_score_id, ended_at) " +
            $"SELECT {userId}, 0, {beatmapId}, 1, 1, 'S', 1, 0.97, 100, 900000, '{data}', {ppText}, {legacy}, UTC_TIMESTAMP()");
        return await ScalarAsync<ulong>("SELECT MAX(id) FROM scores");
    }

    private async Task<List<long>> IdsAsync(string sql)
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

    private sealed class FixedStars(double stars) : TomYum.Performance.IPerformanceCalculator, TomYum.Performance.IAccuracyPerformance
    {
        public Task<double> CalculateAsync(TomYum.Performance.PerformanceRequest request, CancellationToken cancellationToken = default) => Task.FromResult(0.0);

        public Task<IReadOnlyList<double>?> ForAccuraciesAsync(int beatmapId, int rulesetId, int mods, IReadOnlyList<double> accuracies, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<double>?>(null);

        public Task<double?> StarRatingAsync(int beatmapId, int rulesetId, int mods, CancellationToken cancellationToken = default) => Task.FromResult<double?>(stars);
    }
}
