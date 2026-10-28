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
    private const string Abc = "abc00000000000000000000000000abc";
    private const string Missing = "0000000000000000000000000000dead";
    private const string Easy = "eeee0000000000000000000000000001";
    private const string Hard = "dddd0000000000000000000000000002";

    [Fact]
    public async Task BeatmapService_OnlyAsksAboutAndKeepsRealChecksums()
    {
        var api = new CountingApiClient(new Beatmap { Md5 = Abc, Id = 42, Status = RankedStatus.Ranked });
        using var service = new BeatmapService(api);

        // What osu-getscores' c= can hold: anything.
        foreach (var junk in new[] { "", "abc", new string('x', 32), Abc + "0", "../../etc/passwd", new string('a', 5000) })
            Assert.Null(await service.GetByMd5Async(junk));
        Assert.Equal(0, api.Calls);
        Assert.Equal(0, service.CachedCount);

        // Either case is the same checksum.
        Assert.Equal(42, (await service.GetByMd5Async(Abc.ToUpperInvariant()))!.Id);
        Assert.Equal(42, (await service.GetByMd5Async(Abc))!.Id);
        Assert.Equal((1, 1), (api.Calls, service.CachedCount));
    }
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
        var api = new CountingApiClient(new Beatmap { Md5 = Abc, Id = 42, Status = RankedStatus.Ranked });
        var service = new BeatmapService(api);

        var first = await service.GetByMd5Async(Abc);
        var second = await service.GetByMd5Async(Abc);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task BeatmapService_CachesNotFound_AndHitsApiOnce()
    {
        var api = new CountingApiClient(null);
        var service = new BeatmapService(api);

        var first = await service.GetByMd5Async(Missing);
        var second = await service.GetByMd5Async(Missing);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task BeatmapService_DoesNotCacheAFailedLookup()
    {
        // osu! not answering isn't osu! not knowing the map: a slow moment mustn't leave a ranked
        // map unsubmitted until the server restarts.
        var api = new CountingApiClient(null) { Fails = true };
        var service = new BeatmapService(api);

        Assert.Null(await service.GetByMd5Async("abc"));
        Assert.Equal(0, service.CachedCount);

        api.Fails = false;
        Assert.Null(await service.GetByMd5Async("abc"));
        Assert.Equal(2, api.Calls);
        Assert.Equal(1, service.CachedCount);
    }

    [Fact]
    public async Task StatusOverride_WinsOverTheApi_ForCachedAndNewLookups()
    {
        var api = new CountingApiClient(new Beatmap { Md5 = Abc, Id = 42, Status = RankedStatus.Pending, OsuApiStatus = -2 });
        var service = new BeatmapService(api);
        var cached = await service.GetByMd5Async(Abc);

        service.SetStatusOverride(42, RankedStatus.Loved);

        Assert.Equal(RankedStatus.Loved, cached!.Status);                            // already cached
        Assert.True(cached.StatusFrozen);
        Assert.Null(cached.OsuApiStatus);                                             // graveyard no longer applies
        var fresh = new BeatmapService(api);
        fresh.SetStatusOverride(42, RankedStatus.Ranked);
        Assert.Equal(RankedStatus.Ranked, (await fresh.GetByMd5Async(Abc))!.Status); // looked up afterwards
    }

    [Fact]
    public async Task FetchSetOnce_ReturnsTheSetOnlyTheFirstTime_AndCachesItsMaps()
    {
        var api = new CountingApiClient(null, set:
        [
            new Beatmap { Md5 = Easy, Id = 1, SetId = 7 },
            new Beatmap { Md5 = Hard, Id = 2, SetId = 7 },
        ]);
        var service = new BeatmapService(api);

        Assert.Equal(2, (await service.FetchSetOnceAsync(7)).Count);
        Assert.Empty(await service.FetchSetOnceAsync(7));
        Assert.Equal(1, api.SetCalls);

        // Its difficulties resolve without another API call.
        Assert.Equal(2, (await service.GetByMd5Async(Hard))!.Id);
        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public async Task FetchSetOnce_RetriesAFailedFetch()
    {
        var api = new CountingApiClient(null, set: []);
        var service = new BeatmapService(api);

        await service.FetchSetOnceAsync(7);
        await service.FetchSetOnceAsync(7);

        Assert.Equal(2, api.SetCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Beatmap.CustomIdFloor)] // custom sets aren't on the osu! API
    public async Task FetchSetOnce_SkipsSetsTheApiCantKnow(int setId)
    {
        var api = new CountingApiClient(null, set: [new Beatmap { Md5 = "x", Id = 1, SetId = setId }]);

        Assert.Empty(await new BeatmapService(api).FetchSetOnceAsync(setId));
        Assert.Equal(0, api.SetCalls);
    }

    /// <summary>An <see cref="IOsuApiClient"/> stub that counts calls and returns fixed results.</summary>
    private sealed class CountingApiClient : IOsuApiClient
    {
        private readonly Beatmap? _result;
        private readonly IReadOnlyList<Beatmap> _set;

        public CountingApiClient(Beatmap? result, IReadOnlyList<Beatmap>? set = null)
        {
            _result = result;
            _set = set ?? [];
        }

        public int Calls { get; private set; }

        public int SetCalls { get; private set; }

        /// <summary>Lookups fail (osu! doesn't answer), as <see cref="OsuApiClient"/> reports them.</summary>
        public bool Fails { get; set; }

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Fails)
                OsuApiFailures.Report();
            return Task.FromResult(_result);
        }

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default)
        {
            SetCalls++;
            return Task.FromResult(_set);
        }

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public bool SupportsSince => false;
    }
}
