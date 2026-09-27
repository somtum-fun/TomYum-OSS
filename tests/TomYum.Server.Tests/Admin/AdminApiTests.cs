using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Server.Admin;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Admin;

/// <summary>The admin API's request signing and the BN panel's beatmap imports.</summary>
public sealed class AdminApiTests
{
    private const string Secret = "shared-secret";
    private const string Path = "/_admin/beatmapsets/1/import?timestamp=1790000000";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"setIds":[1,2]}""");

    [Fact]
    public void Signature_IsValid_ForTheSignedRequest()
    {
        var signature = AdminSignature.Compute(Secret, "POST", Path, "2", Body);

        Assert.True(AdminSignature.IsValid(Secret, "post", Path, "2", Body, signature.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("GET", Path, "2", """{"setIds":[1,2]}""")]                                        // method
    [InlineData("POST", "/_admin/beatmapsets/9/import?timestamp=1790000000", "2", """{"setIds":[1,2]}""")] // path
    [InlineData("POST", Path, "3", """{"setIds":[1,2]}""")]                                        // actor
    [InlineData("POST", Path, "2", """{"setIds":[1,3]}""")]                                        // body
    public void Signature_IsInvalid_WhenAnythingChanges(string method, string path, string actor, string body)
    {
        var signature = AdminSignature.Compute(Secret, "POST", Path, "2", Body);

        Assert.False(AdminSignature.IsValid(Secret, method, path, actor, Encoding.UTF8.GetBytes(body), signature));
    }

    [Fact]
    public void Signature_IsInvalid_WithAnotherSecretOrNone()
    {
        var signature = AdminSignature.Compute("other-secret", "POST", Path, "2", Body);

        Assert.False(AdminSignature.IsValid(Secret, "POST", Path, "2", Body, signature));
        Assert.False(AdminSignature.IsValid(Secret, "POST", Path, "2", Body, null));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(299, true)]
    [InlineData(-299, true)]
    [InlineData(301, false)]
    [InlineData(-301, false)]
    public void Timestamps_OnlyWorkForFiveMinutes(int offsetSeconds, bool fresh)
    {
        var now = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(fresh, AdminSignature.IsFresh(now.ToUnixTimeSeconds() + offsetSeconds, now));
    }

    [Fact]
    public async Task Import_StoresEveryDifficultyOfTheSet()
    {
        var (services, repository) = Build(new FakeApi { Sets = { [7] = [Map(70, 7), Map(71, 7), Map(72, 7)] } });

        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<BeatmapSetImporter>().ImportAsync(7);

        Assert.True(result.Found);
        Assert.Equal(3, result.Difficulties);
        Assert.Equal("Title 7", result.Title);
        Assert.Equal([70, 71, 72], repository.Stored.Select(b => b.Id).Order());
    }

    [Fact]
    public async Task Import_ReportsSetsTheApiDoesntKnow()
    {
        var (services, repository) = Build(new FakeApi());

        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<BeatmapSetImporter>().ImportAsync(404);

        Assert.False(result.Found);
        Assert.Empty(repository.Stored);
    }

    [Fact]
    public async Task BulkImport_ByIds_CountsImportedAndMissingSets()
    {
        var (services, repository) = Build(new FakeApi { Sets = { [1] = [Map(10, 1)], [2] = [Map(20, 2), Map(21, 2)] } });
        var runner = NewRunner(services);
        var job = runner.Enqueue(new BulkImportRequest([1, 2, 3, 2, 0], null, null), "2");

        await runner.RunAsync(job, CancellationToken.None);

        Assert.Equal(3, job.Total);        // duplicates and invalid ids dropped
        Assert.Equal(2, job.Imported);
        Assert.Equal(1, job.NotFound);
        Assert.Equal(3, repository.Stored.Count);
    }

    [Fact]
    public async Task BulkImport_ByDate_PagesThroughTheRangeAndStopsAtTheEnd()
    {
        var jan = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var api = new FakeApi { SupportsSince = true };
        // 600 difficulties ranked a minute apart across 300 sets: needs two pages of 500.
        for (var i = 0; i < 600; i++)
            api.Ranked.Add(Map(1000 + i, 1 + (i / 2), rankedAt: jan.AddMinutes(i)));
        foreach (var group in api.Ranked.GroupBy(b => b.SetId))
            api.Sets[group.Key] = group.ToList();
        var (services, _) = Build(api);
        var runner = NewRunner(services);
        // Up to the 550th difficulty: sets 1..275.
        var job = runner.Enqueue(new BulkImportRequest(null, jan, jan.AddMinutes(549)), "2");

        await runner.RunAsync(job, CancellationToken.None);

        Assert.Equal(275, job.Total);
        Assert.Equal(275, job.Imported);
        Assert.True(api.SinceCalls >= 2);
    }

    [Fact]
    public async Task BulkImport_ByDate_NeedsAnApiKey()
    {
        var (services, _) = Build(new FakeApi { SupportsSince = false });
        var runner = NewRunner(services);
        var job = runner.Enqueue(new BulkImportRequest(null, DateTimeOffset.UtcNow.AddDays(-1), null), "2");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(job, CancellationToken.None));
        Assert.Contains("API key", error.Message, StringComparison.Ordinal);
    }

    private static (ServiceProvider Services, RecordingBeatmapRepository Repository) Build(FakeApi api)
    {
        var repository = new RecordingBeatmapRepository();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IOsuApiClient>(api)
            .AddSingleton<BeatmapService>()
            .AddSingleton<IBeatmapRepository>(repository)
            .AddScoped<BeatmapSetImporter>()
            .BuildServiceProvider();
        return (services, repository);
    }

    private static BeatmapImportRunner NewRunner(ServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<BeatmapImportRunner>.Instance);

    private static Beatmap Map(int id, int setId, DateTimeOffset? rankedAt = null) => new()
    {
        Id = id,
        SetId = setId,
        Md5 = $"md5-{id}",
        Title = $"Title {setId}",
        Details = new BeatmapDetails { ApprovedDate = rankedAt },
    };

    private sealed class FakeApi : IOsuApiClient
    {
        public Dictionary<int, IReadOnlyList<Beatmap>> Sets { get; } = [];

        /// <summary>Everything "ranked", oldest first, for <see cref="GetSinceAsync"/>.</summary>
        public List<Beatmap> Ranked { get; } = [];

        public bool SupportsSince { get; init; }

        public int SinceCalls { get; private set; }

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) =>
            Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Sets.GetValueOrDefault(setId) ?? []);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
        {
            SinceCalls++;
            return Task.FromResult<IReadOnlyList<Beatmap>>(
                Ranked.Where(b => b.Details!.ApprovedDate >= since).Take(500).ToList());
        }
    }

    private sealed class RecordingBeatmapRepository : IBeatmapRepository
    {
        public List<Beatmap> Stored { get; } = [];

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored.FirstOrDefault(b => b.Md5 == md5));

        public Task<Beatmap?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored.FirstOrDefault(b => b.Id == id));

        public Task UpsertAsync(Beatmap beatmap, CancellationToken cancellationToken = default)
        {
            Stored.RemoveAll(b => b.Id == beatmap.Id);
            Stored.Add(beatmap);
            return Task.CompletedTask;
        }

        public Task<int> ReserveCustomSetIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Beatmap.CustomIdFloor);
    }
}
