using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>
/// <see cref="OsuWebMirroringBeatmapRepository"/> / <see cref="OsuWebBeatmapMirror"/> against a real
/// TomYum database and osu-web's real beatmap tables.
/// </summary>
public sealed class OsuWebBeatmapMirrorTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task Upsert_WritesTomYumAndOsuWeb()
    {
        await using var tomyum = await NewTomYumContextAsync();

        await NewRepository(tomyum).UpsertAsync(Map(75, 1, RankedStatus.Pending, api: -2, version: "Insane", mode: 2, maxCombo: 812));

        Assert.NotNull(await new BeatmapRepository(tomyum).GetByIdAsync(75));
        Assert.Equal("md5-75", await ScalarAsync<string>("SELECT checksum FROM osu_beatmaps WHERE beatmap_id = 75"));
        Assert.Equal("Insane", await ScalarAsync<string>("SELECT version FROM osu_beatmaps WHERE beatmap_id = 75"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT playmode FROM osu_beatmaps WHERE beatmap_id = 75"));
        Assert.Equal(812, await ScalarAsync<int>("SELECT max_combo FROM osu_beatmaps WHERE beatmap_id = 75"));
        Assert.Equal(-2, await ScalarAsync<int>("SELECT approved FROM osu_beatmaps WHERE beatmap_id = 75"));
        Assert.Equal(-2, await ScalarAsync<int>("SELECT approved FROM osu_beatmapsets WHERE beatmapset_id = 1"));
        Assert.Null(await ScalarAsync<DateTime?>("SELECT approved_date FROM osu_beatmapsets WHERE beatmapset_id = 1"));
        Assert.Equal("Artist 1", await ScalarAsync<string>("SELECT artist FROM osu_beatmapsets WHERE beatmapset_id = 1"));
    }

    [MySqlFact]
    public async Task RankedMaps_GetAnApprovedDate_AndShareTheirSet()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var repository = NewRepository(tomyum);

        await repository.UpsertAsync(Map(100, 10, RankedStatus.Ranked, api: 1, version: "Easy"));
        await repository.UpsertAsync(Map(101, 10, RankedStatus.Ranked, api: 1, version: "Hard"));

        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_beatmapsets"));
        Assert.Equal(2L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_beatmaps WHERE beatmapset_id = 10"));
        Assert.NotNull(await ScalarAsync<DateTime?>("SELECT approved_date FROM osu_beatmapsets WHERE beatmapset_id = 10"));
    }

    [MySqlFact]
    public async Task FrozenStatus_IsWhatTheWebsiteShows()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var frozen = Map(200, 20, RankedStatus.Loved, api: null);
        frozen.StatusFrozen = true;
        tomyum.Beatmaps.Add(frozen);
        await tomyum.SaveChangesAsync();
        tomyum.ChangeTracker.Clear();

        // The osu! API now says ranked; TomYum keeps the frozen Loved.
        await NewRepository(tomyum).UpsertAsync(Map(200, 20, RankedStatus.Ranked, api: 1));

        Assert.Equal(4, await ScalarAsync<int>("SELECT approved FROM osu_beatmaps WHERE beatmap_id = 200"));
    }

    [MySqlFact]
    public async Task LongNames_AreTruncatedToOsuWebColumns()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var map = Map(300, 30, RankedStatus.Ranked, api: 1, version: new string('v', 120));
        map.Artist = new string('a', 150);

        await NewRepository(tomyum).UpsertAsync(map);

        Assert.Equal(80L, await ScalarAsync<long>("SELECT CHAR_LENGTH(artist) FROM osu_beatmapsets WHERE beatmapset_id = 30"));
        Assert.Equal(80L, await ScalarAsync<long>("SELECT CHAR_LENGTH(version) FROM osu_beatmaps WHERE beatmap_id = 300"));
    }

    [MySqlFact]
    public async Task Backfill_CopiesOnlyMissingMaps()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var mirror = new OsuWebBeatmapMirror(NewOsuWebContext());
        await mirror.WriteAsync(Map(400, 40, RankedStatus.Ranked, api: 1));
        var known = new[]
        {
            Map(400, 40, RankedStatus.Ranked, api: null, version: "changed"),
            Map(401, 40, RankedStatus.Ranked, api: null),
            Map(402, 41, RankedStatus.Loved, api: null),
        };

        Assert.Equal(2, await mirror.BackfillAsync(known));
        Assert.Equal(0, await new OsuWebBeatmapMirror(NewOsuWebContext()).BackfillAsync(known));
        Assert.Equal(3L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_beatmaps"));
        Assert.Equal("Normal", await ScalarAsync<string>("SELECT version FROM osu_beatmaps WHERE beatmap_id = 400")); // untouched
    }

    [MySqlFact]
    public async Task ReserveCustomSetId_SkipsIdsTakenOnOsuWeb()
    {
        await using var tomyum = await NewTomYumContextAsync();
        await ExecuteAsync($"INSERT INTO osu_beatmapsets (beatmapset_id) VALUES ({Beatmap.CustomIdFloor + 5})");

        Assert.Equal(Beatmap.CustomIdFloor + 6, await NewRepository(tomyum).ReserveCustomSetIdAsync());
    }

    [MySqlFact]
    public async Task IdsBeyondOsuWebsRange_AreRejected()
    {
        var mirror = new OsuWebBeatmapMirror(NewOsuWebContext());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => mirror.WriteAsync(Map(OsuWebBeatmaps.MaxId + 1, 1, RankedStatus.Ranked, api: 1)));
    }

    private OsuWebMirroringBeatmapRepository NewRepository(TomYumDbContext tomyum)
    {
        var osuWeb = NewOsuWebContext();
        return new OsuWebMirroringBeatmapRepository(new BeatmapRepository(tomyum), new OsuWebBeatmapMirror(osuWeb), osuWeb);
    }

    private static Beatmap Map(int id, int setId, RankedStatus status, int? api, string version = "Normal", int mode = 0, int maxCombo = 500) => new()
    {
        Id = id,
        SetId = setId,
        Md5 = $"md5-{id}",
        Artist = $"Artist {setId}",
        Title = $"Title {setId}",
        Creator = "Mapper",
        Version = version,
        Status = status,
        OsuApiStatus = api,
        Mode = mode,
        StarRating = 4.2,
        TotalLength = 120,
        MaxCombo = maxCombo,
        LastUpdate = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero),
    };
}
