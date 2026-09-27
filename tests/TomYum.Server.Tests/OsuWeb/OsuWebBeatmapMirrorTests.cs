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
        // Without it the site treats the set as having no cover.
        Assert.NotNull(await ScalarAsync<DateTime?>("SELECT cover_updated_at FROM osu_beatmapsets WHERE beatmapset_id = 1"));
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
    public async Task Details_FillTheWebsitesStatColumns()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var map = Map(500, 50, RankedStatus.Ranked, api: 1);
        map.Details = new BeatmapDetails
        {
            CircleSize = 4, ApproachRate = 9.3f, OverallDifficulty = 8, DrainRate = 5, Bpm = 185, HitLength = 87,
            CountCircles = 120, CountSliders = 80, CountSpinners = 2,
            GenreId = 3, LanguageId = 3, Source = "Black Clover", Tags = "opening pop", Video = true,
        };

        await NewRepository(tomyum).UpsertAsync(map);

        Assert.Equal(9.3f, await ScalarAsync<float>("SELECT diff_approach FROM osu_beatmaps WHERE beatmap_id = 500"));
        Assert.Equal(4f, await ScalarAsync<float>("SELECT diff_size FROM osu_beatmaps WHERE beatmap_id = 500"));
        Assert.Equal(87, await ScalarAsync<int>("SELECT hit_length FROM osu_beatmaps WHERE beatmap_id = 500"));
        Assert.Equal(202, await ScalarAsync<int>("SELECT countTotal FROM osu_beatmaps WHERE beatmap_id = 500"));
        Assert.Equal(185f, await ScalarAsync<float>("SELECT bpm FROM osu_beatmapsets WHERE beatmapset_id = 50"));
        Assert.Equal(3, await ScalarAsync<int>("SELECT genre_id FROM osu_beatmapsets WHERE beatmapset_id = 50"));
        Assert.Equal("Black Clover", await ScalarAsync<string>("SELECT source FROM osu_beatmapsets WHERE beatmapset_id = 50"));
        Assert.True(await ScalarAsync<bool>("SELECT video FROM osu_beatmapsets WHERE beatmapset_id = 50"));
    }

    [MySqlFact]
    public async Task MissingDetails_NeverBlankKnownValues()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var repository = NewRepository(tomyum);
        var full = Map(600, 60, RankedStatus.Ranked, api: 1);
        full.Details = new BeatmapDetails { CountCircles = 120, CountSliders = 80, CountSpinners = 2, GenreId = 3 };
        await repository.UpsertAsync(full);

        // A later lookup from the keyless mirror: no object counts, no genre.
        var sparse = Map(600, 60, RankedStatus.Ranked, api: 1);
        sparse.Details = new BeatmapDetails { CircleSize = 4 };
        await repository.UpsertAsync(sparse);
        // A map TomYum stored without any API details.
        await repository.UpsertAsync(Map(600, 60, RankedStatus.Ranked, api: 1));

        Assert.Equal(202, await ScalarAsync<int>("SELECT countTotal FROM osu_beatmaps WHERE beatmap_id = 600"));
        Assert.Equal(3, await ScalarAsync<int>("SELECT genre_id FROM osu_beatmapsets WHERE beatmapset_id = 60"));
        Assert.Equal(4f, await ScalarAsync<float>("SELECT diff_size FROM osu_beatmaps WHERE beatmap_id = 600"));
    }

    [MySqlFact]
    public async Task StaffStatus_IsFrozen_AndTheSetTakesTheBestDifficultyStatus()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var repository = NewRepository(tomyum);
        await repository.UpsertAsync(Map(700, 70, RankedStatus.Pending, api: -2, version: "Easy"));
        await repository.UpsertAsync(Map(701, 70, RankedStatus.Pending, api: -2, version: "Hard"));

        Assert.True(await repository.SetStatusAsync(701, RankedStatus.Loved));

        Assert.Equal(4, await ScalarAsync<int>("SELECT approved FROM osu_beatmaps WHERE beatmap_id = 701"));
        Assert.Equal(-2, await ScalarAsync<int>("SELECT approved FROM osu_beatmaps WHERE beatmap_id = 700"));
        Assert.Equal(4, await ScalarAsync<int>("SELECT approved FROM osu_beatmapsets WHERE beatmapset_id = 70"));
        Assert.NotNull(await ScalarAsync<DateTime?>("SELECT approved_date FROM osu_beatmapsets WHERE beatmapset_id = 70"));

        // A later refresh from the osu! API keeps the staff status.
        await repository.UpsertAsync(Map(701, 70, RankedStatus.Pending, api: -2, version: "Hard"));
        var stored = await new BeatmapRepository(tomyum).GetByIdAsync(701);
        Assert.Equal((RankedStatus.Loved, true), (stored!.Status, stored.StatusFrozen));
        Assert.Equal(4, await ScalarAsync<int>("SELECT approved FROM osu_beatmaps WHERE beatmap_id = 701"));

        Assert.False(await repository.SetStatusAsync(999, RankedStatus.Ranked));
        Assert.Equal([700, 701], (await repository.GetBySetAsync(70)).Select(b => b.Id).Order());
    }

    [Theory]
    [InlineData(new sbyte[] { -2, 4, 0 }, 4)]
    [InlineData(new sbyte[] { 3, 1 }, 1)]
    [InlineData(new sbyte[] { 3, 4 }, 4)]
    [InlineData(new sbyte[] { -2, -1 }, -1)]
    public void SetStatus_FollowsOsuPrecedence(sbyte[] difficulties, int expected)
    {
        Assert.Equal((sbyte)expected, OsuWebBeatmaps.SetStatusFor(difficulties));
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
