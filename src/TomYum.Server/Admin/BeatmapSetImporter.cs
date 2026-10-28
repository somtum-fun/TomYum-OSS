using System.Globalization;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Server.Beatmaps;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Admin;

/// <summary>What <see cref="BeatmapSetImporter.ImportAsync"/> stored.</summary>
/// <param name="SetId">The beatmap set id.</param>
/// <param name="Artist">Track artist ("" when not found).</param>
/// <param name="Title">Track title ("" when not found).</param>
/// <param name="Difficulties">How many difficulties were stored; 0 when the osu! API doesn't know the set.</param>
public sealed record BeatmapSetImportResult(int SetId, string Artist, string Title, int Difficulties)
{
    public bool Found => Difficulties > 0;
}

/// <summary>
/// Imports a whole beatmap set from the osu! API on staff request (BN panel): every difficulty goes
/// through <see cref="IBeatmapRepository.UpsertAsync"/>, which in osu-web mode also mirrors it (with
/// stats and cover) to the website, and the set is re-indexed so the beatmap listing shows it. Every
/// attempt is recorded in the staff log.
/// </summary>
public sealed class BeatmapSetImporter
{
    /// <summary>Staff log action for imports.</summary>
    public const string LogAction = "beatmapset.import";

    // One import of a set at a time (lazer asks for a map it doesn't have again before the first import
    // is done): the next waits, then finds the maps stored. Striped by set id, so different sets don't wait.
    private static readonly SemaphoreSlim[] SetLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    // Sets imported in the last minute, and what came of it: lazer asks for a new set once per difficulty
    // (and again for the set), all at once, and each waiting request found the lock free and imported (and
    // posted) it again. An automatic import of a set that was just imported is answered from here instead.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTime At, BeatmapSetImportResult Result)> Recent = new();
    private static readonly TimeSpan RecentFor = TimeSpan.FromMinutes(1);

    private readonly BeatmapService _beatmaps;
    private readonly IBeatmapRepository _repository;
    private readonly IStaffLogRepository _log;
    private readonly TimeProvider _time;
    private readonly OsuWebInterop? _interop;
    private readonly OsuApiV2Client? _apiV2;
    private readonly ILogger<BeatmapSetImporter> _logger;

    public BeatmapSetImporter(
        BeatmapService beatmaps,
        IBeatmapRepository repository,
        IStaffLogRepository log,
        TimeProvider time,
        ILogger<BeatmapSetImporter> logger,
        OsuWebInterop? interop = null,
        OsuApiV2Client? apiV2 = null)
    {
        _beatmaps = beatmaps;
        _repository = repository;
        _log = log;
        _time = time;
        _logger = logger;
        _interop = interop;
        _apiV2 = apiV2;
    }

    /// <summary>
    /// Imports set <paramref name="setId"/> for staff member <paramref name="actorId"/> (optionally as
    /// part of bulk job <paramref name="jobId"/>, or on its own for the client <paramref name="via"/>); a
    /// result with no difficulties when the API doesn't know it.
    /// </summary>
    public async Task<BeatmapSetImportResult> ImportAsync(int setId, int actorId, string? jobId = null, string? via = null, CancellationToken cancellationToken = default)
    {
        BeatmapSetImportResult result;
        var setLock = SetLocks[(uint)setId % (uint)SetLocks.Length];
        await setLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Staff asking (no via) always import again; lazer's and the website's own lookups don't repeat one.
            var now = _time.GetUtcNow().UtcDateTime;
            if (via is not null && Recent.TryGetValue(setId, out var recent) && now - recent.At < RecentFor && recent.Result.Found)
                return recent.Result;

            result = await ImportCoreAsync(setId, cancellationToken).ConfigureAwait(false);
            Recent[setId] = (now, result);
            foreach (var (id, entry) in Recent)
            {
                if (now - entry.At >= RecentFor)
                    Recent.TryRemove(id, out _);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await LogAsync(setId, actorId, jobId, via, "failed", Invariant($"Set {setId}: {ex.Message}"), cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            setLock.Release();
        }

        if (result.Found)
        {
            await LogAsync(setId, actorId, jobId, via, "imported",
                Invariant($"{result.Artist} - {result.Title} ({result.Difficulties} difficulties)"), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await LogAsync(setId, actorId, jobId, via, "not_found", Invariant($"Set {setId} isn't on osu!"), cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private async Task<BeatmapSetImportResult> ImportCoreAsync(int setId, CancellationToken cancellationToken)
    {
        var set = await _beatmaps.FetchSetAsync(setId, cancellationToken).ConfigureAwait(false);
        if (set.Count == 0)
            return new BeatmapSetImportResult(setId, string.Empty, string.Empty, 0);

        foreach (var beatmap in set)
            await _repository.UpsertAsync(beatmap, cancellationToken).ConfigureAwait(false);

        // The maps are stored, which is what lazer's lookup waits for: the website's description, nominators
        // and listing follow in the background (they took up to half a minute, and lazer gives up after ten seconds).
        if (_interop is { IsConfigured: true })
            _ = Task.Run(() => FinishOnWebsiteAsync(setId), CancellationToken.None);

        return new BeatmapSetImportResult(setId, set[0].Artist, set[0].Title, set.Count);
    }

    /// <summary>A stored set's description and nominators (osu! API v2) on the website, and the listing re-indexed.</summary>
    private async Task FinishOnWebsiteAsync(int setId)
    {
        if (_interop is not { } interop)
            return;

        // Description and nominators, before indexing so the listing has them too.
        try
        {
            if (_apiV2 is { IsConfigured: true } && await _apiV2.GetBeatmapsetExtrasAsync(setId).ConfigureAwait(false) is { } extras)
                await interop.SyncBeatmapsetExtrasAsync(setId, extras).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Imported beatmap set {SetId} but sending its description/nominators to osu-web failed.", setId);
        }

        try
        {
            await interop.IndexBeatmapsetAsync(setId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The maps are stored either way; only the listing lags until the next re-index.
            _logger.LogWarning(ex, "Imported beatmap set {SetId} but osu-web search indexing failed.", setId);
        }
    }

    private async Task LogAsync(int setId, int actorId, string? jobId, string? via, string result, string summary, CancellationToken cancellationToken)
    {
        try
        {
            await _log.AddAsync(new StaffLogEntry
            {
                CreatedAt = _time.GetUtcNow().UtcDateTime,
                ActorId = actorId,
                Action = LogAction,
                TargetId = setId,
                Summary = summary.Length > 512 ? summary[..512] : summary,
                Result = result,
                JobId = jobId,
                Via = via,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The import itself happened; a missing log row mustn't turn it into an error.
            _logger.LogWarning(ex, "Couldn't write the staff log for beatmap set {SetId}.", setId);
        }
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
