using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Tests beatmap status handling: the osu! v1 API status mapping (bancho.py's
/// <c>from_osuapi</c>), the leaderboard/pp predicates, and the <c>BeatmapService</c> cache
/// fronting the API client (including the negative-result cache).
/// </summary>
public class BeatmapStatusTests
{
    [Theory]
    [InlineData(-2, RankedStatus.Pending)]   // graveyard
    [InlineData(-1, RankedStatus.Pending)]   // WIP
    [InlineData(0, RankedStatus.Pending)]    // pending
    [InlineData(1, RankedStatus.Ranked)]
    [InlineData(2, RankedStatus.Approved)]
    [InlineData(3, RankedStatus.Qualified)]
    [InlineData(4, RankedStatus.Loved)]
    [InlineData(999, RankedStatus.UpdateAvailable)] // unknown → fallback
    public void FromOsuApi_MapsApiStatusToInternal(int apiStatus, RankedStatus expected)
    {
        Assert.Equal(expected, RankedStatusExtensions.FromOsuApi(apiStatus));
    }

    [Theory]
    [InlineData(RankedStatus.Ranked, true)]
    [InlineData(RankedStatus.Approved, true)]
    [InlineData(RankedStatus.Qualified, true)]
    [InlineData(RankedStatus.Loved, true)]
    [InlineData(RankedStatus.Pending, false)]
    [InlineData(RankedStatus.NotSubmitted, false)]
    public void HasLeaderboard_ReflectsRankedTiers(RankedStatus status, bool expected)
    {
        Assert.Equal(expected, status.HasLeaderboard());
    }

    [Theory]
    [InlineData(RankedStatus.Ranked, true)]
    [InlineData(RankedStatus.Approved, true)]
    [InlineData(RankedStatus.Qualified, false)] // qualified has a board but no ranked pp
    [InlineData(RankedStatus.Loved, false)]
    public void AwardsRankedPp_OnlyRankedAndApproved(RankedStatus status, bool expected)
    {
        Assert.Equal(expected, status.AwardsRankedPp());
    }

    [Fact]
    public async Task BeatmapService_CachesResult_AndHitsApiOnce()
    {
        var api = new CountingApiClient(new Beatmap { Md5 = "abc", Id = 42, Status = RankedStatus.Ranked });
        var service = new BeatmapService(api);

        var first = await service.GetByMd5Async("abc");
        var second = await service.GetByMd5Async("abc");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task BeatmapService_CachesNotFound_AndHitsApiOnce()
    {
        var api = new CountingApiClient(null);
        var service = new BeatmapService(api);

        var first = await service.GetByMd5Async("missing");
        var second = await service.GetByMd5Async("missing");

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, api.Calls);
    }

    /// <summary>An <see cref="IOsuApiClient"/> stub that counts calls and returns a fixed result.</summary>
    private sealed class CountingApiClient : IOsuApiClient
    {
        private readonly Beatmap? _result;

        public CountingApiClient(Beatmap? result) => _result = result;

        public int Calls { get; private set; }

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }
}
