using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Play counts on osu-web ("Most Played Beatmaps", beatmap/set play and pass counts).</summary>
public sealed class PlayCountTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task EachPlay_CountsForThePlayerTheMapAndTheSet()
    {
        await using var tomyum = await NewTomYumContextAsync();
        await AddMapsAsync();
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());
        await mirror.EnsureLinkTableAsync();

        Score[] plays =
        [
            Play(2, 100, passed: true),
            Play(2, 100, passed: false),
            Play(3, 101, passed: true),
            Play(2, 100, passed: true, mode: GameMode.RelaxOffset), // relax isn't mirrored
            Play(2, 999, passed: true),                             // map not on the website
        ];
        foreach (var play in plays)
        {
            // Stored first, then counted, as score submission does.
            tomyum.Scores.Add(play);
            await tomyum.SaveChangesAsync();
            await mirror.RecordPlayAsync(play);
        }

        Assert.Equal(2, await ScalarAsync<int>("SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 AND beatmap_id = 100"));
        Assert.Equal((2, 1), (await ScalarAsync<int>("SELECT playcount FROM osu_beatmaps WHERE beatmap_id = 100"), await ScalarAsync<int>("SELECT passcount FROM osu_beatmaps WHERE beatmap_id = 100")));
        Assert.Equal(3, await ScalarAsync<int>("SELECT play_count FROM osu_beatmapsets WHERE beatmapset_id = 10"));
        // "Play History" on the profile.
        Assert.Equal(2, await ScalarAsync<int>($"SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '{ThisMonth}'"));
        // A later rebuild finds nothing to add.
        Assert.Equal(0, await mirror.RebuildPlayCountsAsync());
        Assert.Equal(2, await ScalarAsync<int>("SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 AND beatmap_id = 100"));
    }

    [MySqlFact]
    public async Task Rebuild_KeepsLazerPlays_AndFollowsRemovedScores()
    {
        await using var tomyum = await NewTomYumContextAsync();
        await AddMapsAsync();
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());
        await mirror.EnsureLinkTableAsync();
        var removed = Play(2, 100, true);
        tomyum.Scores.AddRange(removed, Play(2, 100, false), Play(2, 101, true, at: new DateTimeOffset(2026, 8, 31, 23, 0, 0, TimeSpan.Zero)));
        await tomyum.SaveChangesAsync();
        Assert.Equal(2, await mirror.RebuildPlayCountsAsync());

        // Lazer plays, as ppy's score processor counts them.
        await ExecuteAsync("UPDATE osu_user_beatmap_playcount SET playcount = playcount + 3 WHERE user_id = 2 AND beatmap_id = 100");
        await ExecuteAsync($"UPDATE osu_user_month_playcount SET playcount = playcount + 3 WHERE user_id = 2 AND `year_month` = '{ThisMonth}'");
        await ExecuteAsync("UPDATE osu_beatmaps SET playcount = playcount + 3 WHERE beatmap_id = 100");
        await ExecuteAsync("UPDATE osu_beatmapsets SET play_count = play_count + 3 WHERE beatmapset_id = 10");

        tomyum.Scores.Remove(removed);
        await tomyum.SaveChangesAsync();
        Assert.Equal(1, await mirror.RebuildPlayCountsAsync());
        Assert.Equal(0, await mirror.RebuildPlayCountsAsync());

        Assert.Equal(4, await ScalarAsync<int>("SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 AND beatmap_id = 100"));
        Assert.Equal((4, 0), (await ScalarAsync<int>("SELECT playcount FROM osu_beatmaps WHERE beatmap_id = 100"), await ScalarAsync<int>("SELECT passcount FROM osu_beatmaps WHERE beatmap_id = 100")));
        Assert.Equal(5, await ScalarAsync<int>("SELECT play_count FROM osu_beatmapsets WHERE beatmapset_id = 10"));
        Assert.Equal(4, await ScalarAsync<int>($"SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '{ThisMonth}'"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '2608'"));
    }

    [MySqlFact]
    public async Task Upgrade_DoesntCountThePlaysTheOldRebuildWrote_Twice()
    {
        await using var tomyum = await NewTomYumContextAsync();
        await AddMapsAsync();
        tomyum.Scores.AddRange(Play(2, 100, true), Play(2, 100, true));
        await tomyum.SaveChangesAsync();
        // What the previous version left: the map counts rebuilt from TomYum's scores, no history.
        await ExecuteAsync("INSERT INTO osu_user_beatmap_playcount (user_id, beatmap_id, playcount) VALUES (2, 100, 2)");
        await ExecuteAsync("UPDATE osu_beatmaps SET playcount = 2, passcount = 2 WHERE beatmap_id = 100");

        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());
        await mirror.EnsureLinkTableAsync();
        Assert.Equal(0, await mirror.RebuildPlayCountsAsync());

        Assert.Equal(2, await ScalarAsync<int>("SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 AND beatmap_id = 100"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT playcount FROM osu_beatmaps WHERE beatmap_id = 100"));
        Assert.Equal(2, await ScalarAsync<int>($"SELECT playcount FROM osu_user_month_playcount WHERE user_id = 2 AND `year_month` = '{ThisMonth}'"));
    }

    [MySqlFact]
    public async Task Rebuild_CountsEveryStoredPlay_AndIsRepeatable()
    {
        await using var tomyum = await NewTomYumContextAsync();
        await AddMapsAsync();
        tomyum.Scores.AddRange(Play(2, 100, true), Play(2, 100, false), Play(2, 101, true), Play(3, 100, true),
            Play(2, 100, true, mode: GameMode.RelaxOffset), Play(2, 999, true));
        await tomyum.SaveChangesAsync();
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());

        Assert.Equal(3, await mirror.RebuildPlayCountsAsync());
        Assert.Equal(0, await mirror.RebuildPlayCountsAsync()); // rebuilding again doesn't double count

        Assert.Equal(2, await ScalarAsync<int>("SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 AND beatmap_id = 100"));
        Assert.Equal((3, 2), (await ScalarAsync<int>("SELECT playcount FROM osu_beatmaps WHERE beatmap_id = 100"), await ScalarAsync<int>("SELECT passcount FROM osu_beatmaps WHERE beatmap_id = 100")));
        Assert.Equal(4, await ScalarAsync<int>("SELECT play_count FROM osu_beatmapsets WHERE beatmapset_id = 10"));
    }

    [MySqlFact]
    public async Task Replays_AreMarked_AndMappedBackToTheGameScore()
    {
        await using var tomyum = await NewTomYumContextAsync();
        await AddMapsAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var player = await users.AddAsync(new User { Username = "Replayer", SafeName = "replayer", Email = "r@x.dev", PasswordBcrypt = "x", Country = "th" });
        var withReplay = Play(player.Id, 100, true);
        var without = Play(player.Id, 101, true);
        tomyum.Scores.AddRange(withReplay, without);
        await tomyum.SaveChangesAsync();
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());
        await mirror.EnsureLinkTableAsync();
        var webWith = await mirror.WriteScoreAsync(withReplay);
        var webWithout = await mirror.WriteScoreAsync(without);

        Assert.Equal(1, await mirror.MarkReplaysAsync(id => id == withReplay.Id));
        Assert.Equal(0, await mirror.MarkReplaysAsync(id => id == withReplay.Id)); // already marked

        Assert.True(await ScalarAsync<bool>($"SELECT has_replay FROM scores WHERE id = {webWith}"));
        Assert.False(await ScalarAsync<bool>($"SELECT has_replay FROM scores WHERE id = {webWithout}"));
        Assert.Equal(withReplay.Id, await mirror.GetTomYumScoreIdAsync(webWith!.Value));
        Assert.Null(await mirror.GetTomYumScoreIdAsync(999_999));
    }

    private async Task AddMapsAsync()
    {
        var beatmaps = new OsuWebBeatmapMirror(NewOsuWebContext());
        foreach (var id in new[] { 100, 101 })
            await beatmaps.WriteAsync(new Beatmap { Id = id, SetId = 10, Md5 = $"md5-{id}", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "v", Artist = "a", Title = "t", Creator = "c" });
    }

    private static string ThisMonth => OsuWebScoreMirror.YearMonth(DateTimeOffset.UtcNow);

    private static Score Play(int userId, int beatmapId, bool passed, int mode = 0, DateTimeOffset? at = null) => new()
    {
        UserId = userId,
        BeatmapId = beatmapId,
        BeatmapMd5 = $"md5-{beatmapId}",
        Mode = mode,
        ScoreValue = 1000,
        Grade = passed ? "A" : "F",
        Passed = passed,
        Status = passed ? ScoreStatus.Best : ScoreStatus.Failed,
        SubmittedAt = at ?? DateTimeOffset.UtcNow,
    };
}
