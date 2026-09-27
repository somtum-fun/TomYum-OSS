using TomYum.Domain.Entities;
using Xunit;

namespace TomYum.Domain.Tests;

public class BeatmapTests
{
    // osu-web's osu_beatmaps.beatmap_id / osu_beatmapsets.beatmapset_id are MEDIUMINT UNSIGNED.
    private const int OsuWebMaxId = 16_777_215;

    [Fact]
    public void CustomIdFloor_FitsOsuWebIdColumns()
    {
        Assert.InRange(Beatmap.CustomIdFloor, 1, OsuWebMaxId);
    }

    [Theory]
    [InlineData(Beatmap.CustomIdFloor - 1, false)]
    [InlineData(Beatmap.CustomIdFloor, true)]
    [InlineData(OsuWebMaxId, true)]
    public void IsCustom_StartsAtFloor(int id, bool expected)
    {
        Assert.Equal(expected, new Beatmap { Id = id }.IsCustom);
    }
}
