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
/// stats and cover) to the website, and the set is re-indexed so the beatmap listing shows it.
/// </summary>
public sealed class BeatmapSetImporter
{
    private readonly BeatmapService _beatmaps;
    private readonly IBeatmapRepository _repository;
    private readonly OsuWebInterop? _interop;
    private readonly ILogger<BeatmapSetImporter> _logger;

    public BeatmapSetImporter(BeatmapService beatmaps, IBeatmapRepository repository, ILogger<BeatmapSetImporter> logger, OsuWebInterop? interop = null)
    {
        _beatmaps = beatmaps;
        _repository = repository;
        _logger = logger;
        _interop = interop;
    }

    /// <summary>Imports set <paramref name="setId"/>; a result with no difficulties when the API doesn't know it.</summary>
    public async Task<BeatmapSetImportResult> ImportAsync(int setId, CancellationToken cancellationToken = default)
    {
        var set = await _beatmaps.FetchSetAsync(setId, cancellationToken).ConfigureAwait(false);
        if (set.Count == 0)
            return new BeatmapSetImportResult(setId, string.Empty, string.Empty, 0);

        foreach (var beatmap in set)
            await _repository.UpsertAsync(beatmap, cancellationToken).ConfigureAwait(false);

        if (_interop is { IsConfigured: true })
        {
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
}
