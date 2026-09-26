using System.Text.Json;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Locks in the osu! v1 <c>get_beatmaps</c> JSON parsing across both API flavours: <c>old.ppy.sh</c>
/// quotes every field as a string, while the keyless <c>osu.direct</c> mirror returns ids, lengths,
/// and ratings as bare JSON numbers. <see cref="FlexibleStringConverter"/> must accept both so the
/// mirror does not throw on the lookup hot path (the bug this test guards against).
/// </summary>
public class OsuApiBeatmapParsingTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new FlexibleStringConverter() },
    };

    [Fact]
    public void Parses_OldPpySh_AllStringFields()
    {
        // old.ppy.sh: every value is a quoted string.
        const string json = """
        [{"file_md5":"abc","beatmapset_id":"123","beatmap_id":"456","approved":"1",
          "total_length":"200","mode":"0","difficultyrating":"5.3","max_combo":"900"}]
        """;

        var result = JsonSerializer.Deserialize<List<OsuApiBeatmap>>(json, Options);
        var map = Assert.Single(result!);

        Assert.Equal("123", map.BeatmapsetId);
        Assert.Equal("456", map.BeatmapId);
        Assert.Equal("1", map.Approved);
        Assert.Equal("5.3", map.DifficultyRating);
    }

    [Fact]
    public void Parses_OsuDirectMirror_NumericFields()
    {
        // osu.direct: ids/lengths/ratings come back as bare JSON numbers.
        const string json = """
        [{"file_md5":"abc","beatmapset_id":123,"beatmap_id":456,"approved":1,
          "total_length":200,"mode":0,"difficultyrating":5.3,"max_combo":900}]
        """;

        var result = JsonSerializer.Deserialize<List<OsuApiBeatmap>>(json, Options);
        var map = Assert.Single(result!);

        // Numbers are normalised to their invariant string form.
        Assert.Equal("123", map.BeatmapsetId);
        Assert.Equal("456", map.BeatmapId);
        Assert.Equal("1", map.Approved);
        Assert.Equal("5.3", map.DifficultyRating);
        Assert.Equal("900", map.MaxCombo);
    }

    [Fact]
    public void Parses_NullNumericFields_AsNull()
    {
        // max_combo is frequently null on the mirror for some modes.
        const string json = """[{"file_md5":"abc","beatmapset_id":123,"max_combo":null}]""";

        var result = JsonSerializer.Deserialize<List<OsuApiBeatmap>>(json, Options);
        var map = Assert.Single(result!);

        Assert.Null(map.MaxCombo);
        Assert.Equal("123", map.BeatmapsetId);
    }
}
