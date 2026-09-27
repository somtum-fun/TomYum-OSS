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
    /// part of bulk job <paramref name="jobId"/>); a result with no difficulties when the API doesn't know it.
    /// </summary>
    public async Task<BeatmapSetImportResult> ImportAsync(int setId, int actorId, string? jobId = null, CancellationToken cancellationToken = default)
    {
        BeatmapSetImportResult result;
        try
        {
            result = await ImportCoreAsync(setId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await LogAsync(setId, actorId, jobId, "failed", Invariant($"Set {setId}: {ex.Message}"), cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (result.Found)
        {
            await LogAsync(setId, actorId, jobId, "imported",
                Invariant($"{result.Artist} - {result.Title} ({result.Difficulties} difficulties)"), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await LogAsync(setId, actorId, jobId, "not_found", Invariant($"Set {setId} isn't on osu!"), cancellationToken).ConfigureAwait(false);
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

        if (_interop is { IsConfigured: true })
        {
            // Description and nominators (osu! API v2), before indexing so the listing has them too.
            if (_apiV2 is { IsConfigured: true } && await _apiV2.GetBeatmapsetExtrasAsync(setId, cancellationToken).ConfigureAwait(false) is { } extras)
            {
                try
                {
                    await _interop.SyncBeatmapsetExtrasAsync(setId, extras, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Imported beatmap set {SetId} but sending its description/nominators to osu-web failed.", setId);
                }
            }

            try
            {
                await _interop.IndexBeatmapsetAsync(setId, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // The maps are stored either way; only the listing lags until the next re-index.
                _logger.LogWarning(ex, "Imported beatmap set {SetId} but osu-web search indexing failed.", setId);
            }
        }

        return new BeatmapSetImportResult(setId, set[0].Artist, set[0].Title, set.Count);
    }

    private async Task LogAsync(int setId, int actorId, string? jobId, string result, string summary, CancellationToken cancellationToken)
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
