using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Translation rules from TomYum beatmaps to osu-web's tables (no database needed).</summary>
public class OsuWebBeatmapsTests
{
    [Theory]
    [InlineData(-2, RankedStatus.Pending, -2)]   // graveyard survives TomYum's Pending fold
    [InlineData(-1, RankedStatus.Pending, -1)]   // WIP
    [InlineData(1, RankedStatus.Ranked, 1)]
    [InlineData(4, RankedStatus.Loved, 4)]
    public void ToApproved_KeepsTheOsuApiValue(int api, RankedStatus status, int expected)
    {
        var beatmap = new Beatmap { Status = status, OsuApiStatus = api };

        Assert.Equal((sbyte)expected, OsuWebBeatmaps.ToApproved(beatmap));
    }

    [Fact]
    public void ToApproved_FrozenStatusWinsOverTheApi()
    {
        var beatmap = new Beatmap { Status = RankedStatus.Loved, StatusFrozen = true, OsuApiStatus = 1 };

        Assert.Equal((sbyte)4, OsuWebBeatmaps.ToApproved(beatmap));
    }

    [Theory]
    [InlineData(RankedStatus.Pending, 0)]
    [InlineData(RankedStatus.UpdateAvailable, 0)]
    [InlineData(RankedStatus.Ranked, 1)]
    [InlineData(RankedStatus.Approved, 2)]
    [InlineData(RankedStatus.Qualified, 3)]
    [InlineData(RankedStatus.Loved, 4)]
    public void ToApproved_WithoutApiValue_UsesStatus(RankedStatus status, int expected)
    {
        Assert.Equal((sbyte)expected, OsuWebBeatmaps.ToApproved(new Beatmap { Status = status }));
    }

    [Fact]
    public void ToApproved_NotSubmitted_IsNotMirrored()
    {
        Assert.Null(OsuWebBeatmaps.ToApproved(new Beatmap { Status = RankedStatus.NotSubmitted }));
    }

    [Fact]
    public void Truncate_CutsToColumnLengthWithoutSplittingSurrogates()
    {
        Assert.Equal(80, OsuWebBeatmaps.Truncate(new string('a', 100)).Length);

        var emojiAtBoundary = new string('a', 79) + "😀" + "tail";
        var cut = OsuWebBeatmaps.Truncate(emojiAtBoundary);
        Assert.Equal(79, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public void ToTimestamp_UnknownDateUsesFallback()
    {
        var fallback = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(fallback, OsuWebBeatmaps.ToTimestamp(default, fallback));
        Assert.Equal(new DateTime(2020, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            OsuWebBeatmaps.ToTimestamp(new DateTimeOffset(2020, 5, 1, 14, 0, 0, TimeSpan.FromHours(2)), fallback));
    }
}
