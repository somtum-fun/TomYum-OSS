using Microsoft.Extensions.DependencyInjection;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary><see cref="BeatmapService.GetByMd5ForReadAsync"/>: a leaderboard doesn't wait on the osu! API for a stored map.</summary>
public class BeatmapReadFirstTests
{
    private const string Md5 = "0123456789abcdef0123456789abcdef";

    private sealed class SlowApi : IOsuApiClient
    {
        public TaskCompletionSource<Beatmap?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Answer.Task;
        }

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public bool SupportsSince => false;
    }

    private sealed class Stored(Beatmap? beatmap) : IBeatmapRepository
    {
        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) =>
            Task.FromResult(beatmap?.Md5 == md5 ? beatmap : null);
        public Task<Beatmap?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);
        public Task<IReadOnlyList<Beatmap>> GetBySetAsync(int setId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>([]);
        public Task<bool> SetStatusAsync(int beatmapId, RankedStatus status, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> UnfreezeStatusAsync(int beatmapId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task UpsertAsync(Beatmap beatmap, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> ReserveCustomSetIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ReserveCustomBeatmapIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private static BeatmapService Service(SlowApi api, Beatmap? stored) =>
        new(api, new ServiceCollection().AddScoped<IBeatmapRepository>(_ => new Stored(stored)).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>());

    private static Beatmap Map(string version) => new()
    {
        Id = 75, SetId = 1, Md5 = Md5, Artist = "a", Title = "t", Version = version, Creator = "c", Status = RankedStatus.Ranked,
    };

    private static async Task WaitForCallAsync(SlowApi api)
    {
        for (var i = 0; i < 100 && Volatile.Read(ref api.Calls) == 0; i++)
            await Task.Delay(10);
        Assert.Equal(1, Volatile.Read(ref api.Calls));
    }

    [Fact]
    public async Task StoredMap_IsAnsweredWithoutWaiting_ThenTheApisIsCached()
    {
        var api = new SlowApi();
        using var service = Service(api, Map("stored"));

        var first = await service.GetByMd5ForReadAsync(Md5).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("stored", first!.Version);
        await WaitForCallAsync(api); // the full lookup started in the background

        api.Answer.SetResult(Map("from api"));
        for (var i = 0; i < 50 && service.CachedCount == 0; i++)
            await Task.Delay(20);
        Assert.Equal("from api", (await service.GetByMd5ForReadAsync(Md5))!.Version);
        Assert.Equal("from api", (await service.GetByMd5Async(Md5))!.Version);
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task AStoredPendingMap_WaitsForTheApi_ItMayHaveBeenRanked()
    {
        var api = new SlowApi();
        var pending = Map("stored");
        pending.Status = RankedStatus.Pending;
        using var service = Service(api, pending);

        var lookup = service.GetByMd5ForReadAsync(Md5);
        Assert.False(lookup.IsCompleted);
        api.Answer.SetResult(Map("from api"));
        Assert.Equal(RankedStatus.Ranked, (await lookup)!.Status);
    }

    [Fact]
    public async Task UnstoredMap_WaitsForTheApi()
    {
        var api = new SlowApi();
        using var service = Service(api, null);

        var lookup = service.GetByMd5ForReadAsync(Md5);
        Assert.False(lookup.IsCompleted);
        api.Answer.SetResult(Map("from api"));
        Assert.Equal("from api", (await lookup)!.Version);
    }

    [Fact]
    public async Task ManyReads_StartOneBackgroundLookup()
    {
        var api = new SlowApi();
        using var service = Service(api, Map("stored"));

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.GetByMd5ForReadAsync(Md5)));
        await WaitForCallAsync(api);
        await Task.Delay(100);

        Assert.Equal(1, api.Calls);
    }
}
