using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
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
        var (services, repository, log) = Build(new FakeApi { Sets = { [7] = [Map(70, 7), Map(71, 7), Map(72, 7)] } });

        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<BeatmapSetImporter>().ImportAsync(7, actorId: 2);

        Assert.True(result.Found);
        Assert.Equal(3, result.Difficulties);
        Assert.Equal("Title 7", result.Title);
        Assert.Equal([70, 71, 72], repository.Stored.Select(b => b.Id).Order());
        var entry = Assert.Single(log.Entries);
        Assert.Equal(("beatmapset.import", 7L, "imported", 2), (entry.Action, entry.TargetId, entry.Result, entry.ActorId));
        Assert.Equal("Artist 7 - Title 7 (3 difficulties)", entry.Summary);
    }

    [Fact]
    public async Task AnImportAClientAskedFor_SaysWhoAndWhichClient()
    {
        var (services, _, log) = Build(new FakeApi { Sets = { [7] = [Map(70, 7)] } });

        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BeatmapSetImporter>().ImportAsync(7, actorId: 2, via: "lazer");

        var entry = Assert.Single(log.Entries);
        Assert.Equal((2, "lazer"), (entry.ActorId, entry.Via));
    }

    [Fact]
    public async Task Import_ReportsSetsTheApiDoesntKnow()
    {
        var (services, repository, log) = Build(new FakeApi());

        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<BeatmapSetImporter>().ImportAsync(404, actorId: 2);

        Assert.False(result.Found);
        Assert.Empty(repository.Stored);
        Assert.Equal("not_found", Assert.Single(log.Entries).Result);
    }

    [Fact]
    public async Task StatusChange_WholeSet_FreezesAppliesInGameAndLogs()
    {
        var (services, repository, log) = Build(new FakeApi { Sets = { [7] = [Map(70, 7), Map(71, 7)] } });
        using var scope = services.CreateScope();
        var beatmaps = services.GetRequiredService<BeatmapService>();

        // Not stored yet: the change imports the set first.
        var result = await scope.ServiceProvider.GetRequiredService<BeatmapStatusChanger>()
            .ChangeAsync(7, RankedStatus.Loved, null, actorId: 2);

        Assert.NotNull(result);
        Assert.Equal("loved", result!.Status);
        Assert.Equal(2, result.Changed.Count);
        Assert.All(repository.Stored, b => Assert.Equal((RankedStatus.Loved, true), (b.Status, b.StatusFrozen)));
        Assert.Equal(RankedStatus.Loved, beatmaps.GetStatusOverride(70));
        var entry = Assert.Single(log.Entries, e => e.Action == BeatmapStatusChanger.LogAction);
        Assert.Equal(("Artist 7 - Title 7: all difficulties → loved", 7L, 2), (entry.Summary, entry.TargetId, entry.ActorId));
    }

    [Fact]
    public async Task StatusChange_SomeDifficulties_OnlyChangesThose()
    {
        var (services, repository, log) = Build(new FakeApi { Sets = { [7] = [Map(70, 7), Map(71, 7), Map(72, 7)] } });
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BeatmapSetImporter>().ImportAsync(7, actorId: 2);

        var result = await scope.ServiceProvider.GetRequiredService<BeatmapStatusChanger>()
            .ChangeAsync(7, RankedStatus.Ranked, [71], actorId: 2);

        Assert.Equal(["Normal 71"], result!.Changed);
        Assert.Equal([false, true, false], repository.Stored.OrderBy(b => b.Id).Select(b => b.StatusFrozen));
        Assert.Contains("[Normal 71] → ranked", log.Entries.Single(e => e.Action == BeatmapStatusChanger.LogAction).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unlock_GivesBanchosStatusBack_ToStaffSetDifficultiesOnly()
    {
        var api = new FakeApi { Sets = { [7] = [Map(70, 7), Map(71, 7)] } };
        var (services, repository, log) = Build(api);
        using var scope = services.CreateScope();
        var changer = scope.ServiceProvider.GetRequiredService<BeatmapStatusChanger>();
        var beatmaps = services.GetRequiredService<BeatmapService>();
        await changer.ChangeAsync(7, RankedStatus.Loved, [70], actorId: 2);
        // What osu! says now (fresh objects, like a real API response).
        api.Sets[7] = [Map(70, 7, status: RankedStatus.Ranked), Map(71, 7, status: RankedStatus.Ranked)];

        var result = await changer.UnlockAsync(7, null, actorId: 2);

        Assert.Equal(BeatmapStatusChanger.UnlockName, result!.Status);
        Assert.Equal(["Normal 70"], result.Changed);
        var unlocked = repository.Stored.Single(b => b.Id == 70);
        Assert.Equal((RankedStatus.Ranked, false), (unlocked.Status, unlocked.StatusFrozen));
        Assert.Null(beatmaps.GetStatusOverride(70));
        Assert.Contains("[Normal 70] → bancho's status", log.Entries.Last(e => e.Action == BeatmapStatusChanger.LogAction).Summary, StringComparison.Ordinal);

        // Nothing staff-set is left: nothing to do.
        Assert.Empty((await changer.UnlockAsync(7, null, actorId: 2))!.Changed);
        Assert.Null(await changer.UnlockAsync(404, null, actorId: 2));
    }

    [Fact]
    public async Task StatusChange_UnknownSet_ReturnsNull()
    {
        var (services, _, log) = Build(new FakeApi());
        using var scope = services.CreateScope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<BeatmapStatusChanger>().ChangeAsync(404, RankedStatus.Ranked, null, 2));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task BulkImport_ByIds_CountsImportedAndMissingSets()
    {
        var (services, repository, log) = Build(new FakeApi { Sets = { [1] = [Map(10, 1)], [2] = [Map(20, 2), Map(21, 2)] } });
        var runner = NewRunner(services);
        var job = runner.Enqueue(new BulkImportRequest([1, 2, 3, 2, 0], null, null), 2);

        await runner.RunAsync(job, CancellationToken.None);

        Assert.Equal(3, job.Total);        // duplicates and invalid ids dropped
        Assert.Equal(2, job.Imported);
        Assert.Equal(1, job.NotFound);
        Assert.Equal(3, repository.Stored.Count);
        // Every set is in the staff log under the job.
        Assert.Equal(3, log.Entries.Count(e => e.JobId == job.Id));
        Assert.Equal(1, log.Entries.Count(e => e.Result == "not_found"));
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
        var (services, _, _) = Build(api);
        var runner = NewRunner(services);
        // Up to the 550th difficulty: sets 1..275.
        var job = runner.Enqueue(new BulkImportRequest(null, jan, jan.AddMinutes(549)), 2);

        await runner.RunAsync(job, CancellationToken.None);

        Assert.Equal(275, job.Total);
        Assert.Equal(275, job.Imported);
        Assert.True(api.SinceCalls >= 2);
    }

    [Fact]
    public async Task BulkImport_ByDate_NeedsAnApiKey()
    {
        var (services, _, _) = Build(new FakeApi { SupportsSince = false });
        var runner = NewRunner(services);
        var job = runner.Enqueue(new BulkImportRequest(null, DateTimeOffset.UtcNow.AddDays(-1), null), 2);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(job, CancellationToken.None));
        Assert.Contains("API key", error.Message, StringComparison.Ordinal);
    }

    private static (ServiceProvider Services, RecordingBeatmapRepository Repository, RecordingStaffLog Log) Build(FakeApi api)
    {
        var repository = new RecordingBeatmapRepository();
        var log = new RecordingStaffLog();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IOsuApiClient>(api)
            .AddSingleton<BeatmapService>()
            .AddSingleton<IBeatmapRepository>(repository)
            .AddSingleton<IStaffLogRepository>(log)
            .AddSingleton(TimeProvider.System)
            .AddScoped<BeatmapSetImporter>()
            .AddScoped<BeatmapStatusChanger>()
            .BuildServiceProvider();
        return (services, repository, log);
    }

    private static BeatmapImportRunner NewRunner(ServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<BeatmapImportRunner>.Instance);

    private static Beatmap Map(int id, int setId, DateTimeOffset? rankedAt = null, RankedStatus status = default) => new()
    {
        Id = id,
        SetId = setId,
        Md5 = $"md5-{id}",
        Artist = $"Artist {setId}",
        Version = $"Normal {id}",
        Title = $"Title {setId}",
        Status = status,
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

    private sealed class RecordingStaffLog : IStaffLogRepository
    {
        private readonly List<StaffLogEntry> _entries = [];

        public IReadOnlyList<StaffLogEntry> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToList();
            }
        }

        public Task AddAsync(StaffLogEntry entry, CancellationToken cancellationToken = default)
        {
            lock (_entries)
                _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StaffLogEntry>> ListAsync(StaffLogQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StaffLogEntry>>(Entries);
    }

    private sealed class RecordingBeatmapRepository : IBeatmapRepository
    {
        public List<Beatmap> Stored { get; } = [];

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored.FirstOrDefault(b => b.Md5 == md5));

        public Task<Beatmap?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored.FirstOrDefault(b => b.Id == id));

        public Task<IReadOnlyList<Beatmap>> GetBySetAsync(int setId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Beatmap>>(Stored.Where(b => b.SetId == setId).OrderBy(b => b.Id).ToList());

        public Task<bool> SetStatusAsync(int beatmapId, RankedStatus status, CancellationToken cancellationToken = default)
        {
            var beatmap = Stored.FirstOrDefault(b => b.Id == beatmapId);
            if (beatmap is null)
                return Task.FromResult(false);
            beatmap.Status = status;
            beatmap.StatusFrozen = true;
            return Task.FromResult(true);
        }

        public Task<bool> UnfreezeStatusAsync(int beatmapId, CancellationToken cancellationToken = default)
        {
            var beatmap = Stored.FirstOrDefault(b => b.Id == beatmapId);
            if (beatmap is not null)
                beatmap.StatusFrozen = false;
            return Task.FromResult(beatmap is not null);
        }

        public Task UpsertAsync(Beatmap beatmap, CancellationToken cancellationToken = default)
        {
            // Like the real repository: a frozen status survives.
            if (Stored.FirstOrDefault(b => b.Id == beatmap.Id) is { StatusFrozen: true } frozen)
            {
                beatmap.Status = frozen.Status;
                beatmap.StatusFrozen = true;
            }
            Stored.RemoveAll(b => b.Id == beatmap.Id);
            Stored.Add(beatmap);
            return Task.CompletedTask;
        }

        public Task<int> ReserveCustomSetIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Beatmap.CustomIdFloor);

        public Task<int> ReserveCustomBeatmapIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Beatmap.CustomIdFloor);
    }
}
