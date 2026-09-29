using System.Collections.Concurrent;
using System.Threading.Channels;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.Admin;

/// <summary>A bulk import request: explicit set ids, or everything ranked between two dates.</summary>
public sealed record BulkImportRequest(IReadOnlyList<int>? SetIds, DateTimeOffset? RankedFrom, DateTimeOffset? RankedTo);

/// <summary>Progress of one bulk import, as the BN panel shows it.</summary>
public sealed class BeatmapImportJob
{
    private readonly ConcurrentQueue<string> _errors = new();

    public required string Id { get; init; }

    public required BulkImportRequest Request { get; init; }


    public required int StartedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary><c>queued</c>, <c>resolving</c> (listing a date range), <c>running</c>, <c>done</c> or <c>failed</c>.</summary>
    public string Status { get; set; } = "queued";

    public int Total { get; set; }

    public int Imported { get; set; }

    /// <summary>Sets the osu! API didn't know.</summary>
    public int NotFound { get; set; }

    public int Failed { get; set; }

    /// <summary>The most recent problems (capped).</summary>
    public IReadOnlyCollection<string> Errors => _errors.ToArray();

    public DateTimeOffset? FinishedAt { get; set; }

    internal void AddError(string message)
    {
        _errors.Enqueue(message);
        while (_errors.Count > 20 && _errors.TryDequeue(out _))
        {
        }
    }
}

/// <summary>
/// Runs bulk beatmap imports (BN panel) one job at a time in the background, each set through
/// <see cref="BeatmapSetImporter"/>. Keeps the most recent jobs in memory for progress polling.
/// </summary>
public sealed class BeatmapImportRunner : BackgroundService
{
    /// <summary>Upper bound on sets per job, so a huge date range can't run for days.</summary>
    public const int MaxSetsPerJob = 10_000;

    private const int KeptJobs = 50;

    private readonly Channel<BeatmapImportJob> _pending = Channel.CreateUnbounded<BeatmapImportJob>();
    private readonly ConcurrentDictionary<string, BeatmapImportJob> _jobs = new(StringComparer.Ordinal);
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<BeatmapImportRunner> _logger;

    public BeatmapImportRunner(IServiceScopeFactory scopes, TimeProvider time, ILogger<BeatmapImportRunner> logger)
    {
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    /// <summary>Queues a job and returns it (its <see cref="BeatmapImportJob.Id"/> is for polling).</summary>
    public BeatmapImportJob Enqueue(BulkImportRequest request, int startedBy)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = new BeatmapImportJob
        {
            Id = Guid.NewGuid().ToString("N"),
            Request = request,
            StartedBy = startedBy,
            CreatedAt = _time.GetUtcNow(),
        };
        _jobs[job.Id] = job;
        Prune();
        _pending.Writer.TryWrite(job);
        return job;
    }

    public BeatmapImportJob? Get(string id) => _jobs.GetValueOrDefault(id);

    /// <summary>Recent jobs, newest first.</summary>
    public IReadOnlyList<BeatmapImportJob> Recent => _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _pending.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunAsync(job, stoppingToken).ConfigureAwait(false);
                job.Status = "done";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.Status = "failed";
                job.AddError("The server shut down before the import finished.");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk beatmap import {JobId} failed.", job.Id);
                job.Status = "failed";
                job.AddError(ex.Message);
            }
            finally
            {
                job.FinishedAt = _time.GetUtcNow();
            }
        }
    }

    /// <summary>Runs one job to completion (public for tests).</summary>
    public async Task RunAsync(BeatmapImportJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        IReadOnlyList<int> setIds;
        if (job.Request.RankedFrom is { } from)
        {
            job.Status = "resolving";
            setIds = await ResolveRangeAsync(from, job.Request.RankedTo ?? _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            setIds = (job.Request.SetIds ?? []).Where(id => id > 0).Distinct().Take(MaxSetsPerJob).ToList();
        }

        job.Total = setIds.Count;
        job.Status = "running";
        // Many sets at once: the beatmap mirror counts that as crawling, so this asks osu! (paced by the
        // shared budget, so a big job takes a while).
        using var _ = Beatmaps.BeatmapMirror.Bypass();
        foreach (var setId in setIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var scope = _scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<BeatmapSetImporter>()
                    .ImportAsync(setId, job.StartedBy, job.Id, cancellationToken).ConfigureAwait(false);
                if (result.Found)
                    job.Imported++;
                else
                    job.NotFound++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Bulk import {JobId}: set {SetId} failed.", job.Id, setId);
                job.Failed++;
                job.AddError($"Set {setId}: {ex.Message}");
            }
        }
    }

    /// <summary>Every set with a difficulty ranked/loved in [from, to], paging the API by approved date.</summary>
    private async Task<IReadOnlyList<int>> ResolveRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<IOsuApiClient>();
        if (!api.SupportsSince)
            throw new InvalidOperationException("Importing by ranked date needs an osu! API key (TOMYUM_OSU__APIKEY).");

        var sets = new List<int>();
        var seen = new HashSet<int>();
        var since = from;
        while (sets.Count < MaxSetsPerJob)
        {
            var page = await api.GetSinceAsync(since, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
                break;

            foreach (var beatmap in page)
            {
                if (beatmap.Details?.ApprovedDate is { } approved && approved > to)
                    return sets;
                if (seen.Add(beatmap.SetId))
                    sets.Add(beatmap.SetId);
            }

            // Next page starts at the newest date seen; always move forward so a page of maps sharing
            // one timestamp can't loop forever.
            var newest = page.Max(b => b.Details?.ApprovedDate ?? since);
            since = newest > since ? newest : since.AddSeconds(1);
            if (page.Count < 500)
                break;
        }
        return sets.Take(MaxSetsPerJob).ToList();
    }

    private void Prune()
    {
        foreach (var old in _jobs.Values.OrderByDescending(j => j.CreatedAt).Skip(KeptJobs))
            _jobs.TryRemove(old.Id, out _);
    }
}
