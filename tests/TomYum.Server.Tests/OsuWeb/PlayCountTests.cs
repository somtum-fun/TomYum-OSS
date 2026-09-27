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

        await mirror.RecordPlayAsync(Play(2, 100, passed: true));
        await mirror.RecordPlayAsync(Play(2, 100, passed: false));
        await mirror.RecordPlayAsync(Play(3, 101, passed: true));
        await mirror.RecordPlayAsync(Play(2, 100, passed: true, mode: GameMode.RelaxOffset)); // relax isn't mirrored
        await mirror.RecordPlayAsync(Play(2, 999, passed: true));                            // map not on the website

        Assert.Equal(2, await ScalarAsync<int>("SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 AND beatmap_id = 100"));
        Assert.Equal((2, 1), (await ScalarAsync<int>("SELECT playcount FROM osu_beatmaps WHERE beatmap_id = 100"), await ScalarAsync<int>("SELECT passcount FROM osu_beatmaps WHERE beatmap_id = 100")));
        Assert.Equal(3, await ScalarAsync<int>("SELECT play_count FROM osu_beatmapsets WHERE beatmapset_id = 10"));
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
        Assert.Equal(3, await mirror.RebuildPlayCountsAsync()); // rebuilding again doesn't double count

        Assert.Equal(2, await ScalarAsync<int>("SELECT playcount FROM osu_user_beatmap_playcount WHERE user_id = 2 AND beatmap_id = 100"));
        Assert.Equal((3, 2), (await ScalarAsync<int>("SELECT playcount FROM osu_beatmaps WHERE beatmap_id = 100"), await ScalarAsync<int>("SELECT passcount FROM osu_beatmaps WHERE beatmap_id = 100")));
        Assert.Equal(4, await ScalarAsync<int>("SELECT play_count FROM osu_beatmapsets WHERE beatmapset_id = 10"));
    }

    private async Task AddMapsAsync()
    {
        var beatmaps = new OsuWebBeatmapMirror(NewOsuWebContext());
        foreach (var id in new[] { 100, 101 })
            await beatmaps.WriteAsync(new Beatmap { Id = id, SetId = 10, Md5 = $"md5-{id}", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "v", Artist = "a", Title = "t", Creator = "c" });
    }

    private static Score Play(int userId, int beatmapId, bool passed, int mode = 0) => new()
    {
        UserId = userId,
        BeatmapId = beatmapId,
        BeatmapMd5 = $"md5-{beatmapId}",
        Mode = mode,
        ScoreValue = 1000,
        Grade = passed ? "A" : "F",
        Passed = passed,
        Status = passed ? ScoreStatus.Best : ScoreStatus.Failed,
        SubmittedAt = DateTimeOffset.UtcNow,
    };
}
