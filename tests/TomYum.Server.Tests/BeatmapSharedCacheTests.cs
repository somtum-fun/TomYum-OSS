using StackExchange.Redis;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Server.Beatmaps;
using TomYum.Server.Tests.Cluster;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// <see cref="BeatmapService"/> sharing what it found online through Redis: a restart (a new service) or another node
/// doesn't ask the mirror again. Needs Redis in <c>TOMYUM_TEST_REDIS</c>; skipped otherwise.
/// </summary>
[Collection(RedisFactAttribute.Collection)]
public sealed class BeatmapSharedCacheTests
{
    private static string Md5() => Guid.NewGuid().ToString("N");

    private static Beatmap Map(string md5, int id, int setId) => new()
    {
        Md5 = md5, Id = id, SetId = setId, Artist = "A", Title = "T", Version = "V", Status = RankedStatus.Ranked, OsuApiStatus = 1,
        MaxCombo = 1234, Details = new BeatmapDetails { CountCircles = 500, CountSliders = 200, CountSpinners = 1, CircleSize = 4 },
    };

    private static async Task<IConnectionMultiplexer> RedisAsync() =>
        await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable(RedisFactAttribute.EnvVar)!);

    [RedisFact]
    public async Task A_map_found_online_is_found_again_after_a_restart_without_asking()
    {
        using var redis = await RedisAsync();
        var md5 = Md5();
        var api = new Api { Map = Map(md5, 4_000_001, 2_000_001) };

        var before = await new BeatmapService(api, redis: redis).GetByMd5Async(md5);
        await Task.Delay(100); // the write is fire-and-forget
        var restarted = new Api();
        var after = await new BeatmapService(restarted, redis: redis).GetByMd5Async(md5);

        Assert.NotNull(before);
        Assert.Equal(0, restarted.Calls);
        Assert.Equal(4_000_001, after?.Id);
        Assert.Equal(RankedStatus.Ranked, after?.Status);
        Assert.Equal(1, after?.OsuApiStatus);
        Assert.Equal(500, after?.Details?.CountCircles);
        Assert.Equal(1234, after?.MaxCombo);
        await redis.GetDatabase().KeyDeleteAsync("tomyum:beatmaps:md5:" + md5);
    }

    [RedisFact]
    public async Task A_staff_override_is_not_shared()
    {
        using var redis = await RedisAsync();
        var md5 = Md5();
        var first = new BeatmapService(new Api { Map = Map(md5, 4_000_002, 2_000_002) }, redis: redis);
        first.SetStatusOverride(4_000_002, RankedStatus.Loved);
        Assert.Equal(RankedStatus.Loved, (await first.GetByMd5Async(md5))?.Status);
        await Task.Delay(100);

        var other = await new BeatmapService(new Api(), redis: redis).GetByMd5Async(md5);

        Assert.Equal(RankedStatus.Ranked, other?.Status);
        Assert.False(other?.StatusFrozen);
        await redis.GetDatabase().KeyDeleteAsync("tomyum:beatmaps:md5:" + md5);
    }

    [RedisFact]
    public async Task A_fetched_set_shares_its_ranked_maps()
    {
        using var redis = await RedisAsync();
        const int setId = 2_000_003;
        var (a, b) = (Md5(), Md5());
        var api = new Api { Set = [Map(a, 4_000_003, setId), Map(b, 4_000_004, setId)] };

        Assert.Equal(2, (await new BeatmapService(api, redis: redis).FetchSetOnceAsync(setId)).Count);
        await Task.Delay(100);
        var restarted = new Api();

        Assert.Equal(4_000_004, (await new BeatmapService(restarted, redis: redis).GetByMd5Async(b))?.Id);
        Assert.Equal(0, restarted.Calls);
        await redis.GetDatabase().KeyDeleteAsync(["tomyum:beatmaps:md5:" + a, "tomyum:beatmaps:md5:" + b]);
    }

    [RedisFact]
    public async Task A_pending_map_is_not_shared_it_may_be_ranked_any_time()
    {
        using var redis = await RedisAsync();
        var md5 = Md5();
        var pending = Map(md5, 4_000_006, 2_000_006);
        pending.Status = RankedStatus.Pending;

        await new BeatmapService(new Api { Map = pending }, redis: redis).GetByMd5Async(md5);
        await Task.Delay(100);
        var restarted = new Api { Map = pending };
        await new BeatmapService(restarted, redis: redis).GetByMd5Async(md5);

        Assert.Equal(1, restarted.Calls);
        Assert.False(await redis.GetDatabase().KeyExistsAsync("tomyum:beatmaps:md5:" + md5));
    }

    private sealed class Api : IOsuApiClient
    {
        public Beatmap? Map { get; init; }

        public IReadOnlyList<Beatmap> Set { get; init; } = [];

        public int Calls { get; private set; }

        public int SetCalls { get; private set; }

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Map is { } map && map.Md5 == md5 ? Clone(map) : null);
        }

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default)
        {
            SetCalls++;
            return Task.FromResult<IReadOnlyList<Beatmap>>([.. Set.Select(Clone)]);
        }

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public bool SupportsSince => false;

        // A fresh object per answer, as the real clients parse one.
        private static Beatmap Clone(Beatmap b) => System.Text.Json.JsonSerializer.Deserialize<Beatmap>(System.Text.Json.JsonSerializer.Serialize(b))!;
    }
}
