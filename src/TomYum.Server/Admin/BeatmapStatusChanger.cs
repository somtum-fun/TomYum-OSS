using System.Globalization;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Server.Beatmaps;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Admin;

/// <summary>What a staff status change did.</summary>
/// <param name="SetId">The beatmap set.</param>
/// <param name="Status">The new status (API name, e.g. <c>ranked</c>).</param>
/// <param name="Changed">Difficulty names that changed.</param>
public sealed record BeatmapStatusChangeResult(int SetId, string Status, IReadOnlyList<string> Changed);

/// <summary>
/// Staff status changes (BN panel): sets a whole set or some of its difficulties to ranked, approved,
/// qualified, loved or pending on this server. The status is frozen in the database (osu! API
/// refreshes keep it), applied in-game at once (leaderboards/pp), mirrored to the website and logged.
/// </summary>
public sealed class BeatmapStatusChanger
{
    /// <summary>Staff log action for status changes.</summary>
    public const string LogAction = "beatmapset.status";

    /// <summary>The statuses staff can choose, by the name the panel sends.</summary>
    public static readonly IReadOnlyDictionary<string, RankedStatus> Statuses = new Dictionary<string, RankedStatus>(StringComparer.OrdinalIgnoreCase)
    {
        ["ranked"] = RankedStatus.Ranked,
        ["approved"] = RankedStatus.Approved,
        ["qualified"] = RankedStatus.Qualified,
        ["loved"] = RankedStatus.Loved,
        ["pending"] = RankedStatus.Pending,
    };

    private readonly BeatmapService _beatmaps;
    private readonly IBeatmapRepository _repository;
    private readonly IStaffLogRepository _log;
    private readonly TimeProvider _time;
    private readonly ILogger<BeatmapStatusChanger> _logger;
    private readonly OsuWebInterop? _interop;

    public BeatmapStatusChanger(
        BeatmapService beatmaps,
        IBeatmapRepository repository,
        IStaffLogRepository log,
        TimeProvider time,
        ILogger<BeatmapStatusChanger> logger,
        OsuWebInterop? interop = null)
    {
        _beatmaps = beatmaps;
        _repository = repository;
        _log = log;
        _time = time;
        _logger = logger;
        _interop = interop;
    }

    /// <summary>
    /// Sets <paramref name="status"/> on the difficulties <paramref name="beatmapIds"/> of set
    /// <paramref name="setId"/> (all of them when null or empty). Null when the set isn't on the
    /// server or on osu!.
    /// </summary>
    public async Task<BeatmapStatusChangeResult?> ChangeAsync(int setId, RankedStatus status, IReadOnlyCollection<int>? beatmapIds, int actorId, CancellationToken cancellationToken = default)
    {
        var set = await LoadSetAsync(setId, cancellationToken).ConfigureAwait(false);
        if (set.Count == 0)
            return null;

        var targets = beatmapIds is { Count: > 0 } ? set.Where(b => beatmapIds.Contains(b.Id)).ToList() : set.ToList();
        var changed = new List<string>();
        foreach (var beatmap in targets)
        {
            if (await _repository.SetStatusAsync(beatmap.Id, status, cancellationToken).ConfigureAwait(false))
            {
                _beatmaps.SetStatusOverride(beatmap.Id, status);
                changed.Add(beatmap.Version);
            }
        }

        var name = Statuses.First(s => s.Value == status).Key;
        if (changed.Count > 0)
        {
            await ReindexAsync(setId, cancellationToken).ConfigureAwait(false);

            var which = changed.Count == set.Count ? "all difficulties" : string.Join(", ", changed.Select(v => $"[{v}]"));
            var summary = string.Create(CultureInfo.InvariantCulture, $"{set[0].Artist} - {set[0].Title}: {which} → {name}");
            await _log.AddAsync(new StaffLogEntry
            {
                CreatedAt = _time.GetUtcNow().UtcDateTime,
                ActorId = actorId,
                Action = LogAction,
                TargetId = setId,
                Summary = summary.Length > 512 ? summary[..512] : summary,
                Result = name,
            }, cancellationToken).ConfigureAwait(false);
        }

        return new BeatmapStatusChangeResult(setId, name, changed);
    }

    /// <summary>The set's stored difficulties; imports it from the osu! API first when the server doesn't have it.</summary>
    private async Task<IReadOnlyList<Beatmap>> LoadSetAsync(int setId, CancellationToken cancellationToken)
    {
        var stored = await _repository.GetBySetAsync(setId, cancellationToken).ConfigureAwait(false);
        if (stored.Count > 0)
            return stored;

        foreach (var beatmap in await _beatmaps.FetchSetAsync(setId, cancellationToken).ConfigureAwait(false))
            await _repository.UpsertAsync(beatmap, cancellationToken).ConfigureAwait(false);
        return await _repository.GetBySetAsync(setId, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReindexAsync(int setId, CancellationToken cancellationToken)
    {
        if (_interop is not { IsConfigured: true })
            return;

        try
        {
            await _interop.IndexBeatmapsetAsync(setId, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Changed beatmap set {SetId}'s status but osu-web search indexing failed.", setId);
        }
    }
}
