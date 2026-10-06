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
/// <param name="Scores">What recalculating the scores on the changed difficulties did.</param>
/// <param name="Cleared">What clearing the leaderboards of the difficulties that just got ranked did (null: none were).</param>
public sealed record BeatmapStatusChangeResult(int SetId, string Status, IReadOnlyList<string> Changed, ScoreRecalculationResult? Scores = null, LeaderboardClearResult? Cleared = null);

/// <summary>
/// Staff status changes (BN panel): sets a whole set or some of its difficulties to ranked, approved,
/// qualified, loved or pending on this server. The status is frozen in the database (osu! API
/// refreshes keep it), applied in-game at once (leaderboards/pp), mirrored to the website and logged.
/// A difficulty that becomes ranked or approved (from any other status) starts with an empty
/// leaderboard, as on the old server: its scores are deleted (<see cref="ClearScoresOnRankKey"/>).
/// </summary>
public sealed class BeatmapStatusChanger
{
    /// <summary>Staff log action for status changes.</summary>
    public const string LogAction = "beatmapset.status";

    /// <summary>
    /// Config (<c>Ranking:ClearScoresOnRank</c>, env <c>TOMYUM_RANKING__CLEARSCORESONRANK</c>): delete
    /// a difficulty's scores when it becomes ranked or approved. On unless set to false.
    /// </summary>
    public const string ClearScoresOnRankKey = "Ranking:ClearScoresOnRank";

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
    private readonly ScoreRecalculation? _recalculation;
    private readonly OsuWebInterop? _interop;
    private readonly bool _clearScoresOnRank;

    public BeatmapStatusChanger(
        BeatmapService beatmaps,
        IBeatmapRepository repository,
        IStaffLogRepository log,
        TimeProvider time,
        ILogger<BeatmapStatusChanger> logger,
        ScoreRecalculation? recalculation = null,
        OsuWebInterop? interop = null,
        IConfiguration? configuration = null)
    {
        _clearScoresOnRank = configuration?.GetValue<bool?>(ClearScoresOnRankKey) ?? true;
        _beatmaps = beatmaps;
        _repository = repository;
        _log = log;
        _time = time;
        _logger = logger;
        _recalculation = recalculation;
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

        var changed = new List<Beatmap>();
        var newlyRanked = new List<Beatmap>();
        foreach (var beatmap in Targets(set, beatmapIds))
        {
            // Already set by staff to this status: nothing to do (no log entry, recalculation or announcement).
            if (beatmap.StatusFrozen && beatmap.Status == status)
                continue;
            var before = _beatmaps.GetStatusOverride(beatmap.Id) ?? beatmap.Status;
            if (await _repository.SetStatusAsync(beatmap.Id, status, cancellationToken).ConfigureAwait(false))
            {
                _beatmaps.SetStatusOverride(beatmap.Id, status);
                beatmap.Status = status;
                changed.Add(beatmap);
                if (IsRanked(status) && !IsRanked(before))
                    newlyRanked.Add(beatmap);
            }
        }

        return await FinishAsync(set, changed, Statuses.First(s => s.Value == status).Key, actorId, newlyRanked, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives the difficulties <paramref name="beatmapIds"/> of set <paramref name="setId"/> (all of
    /// them when null or empty) the osu! API's status back, undoing staff changes. Null when the set
    /// isn't on the server; an empty change list when osu! doesn't know it (custom sets stay as they are).
    /// </summary>
    public async Task<BeatmapStatusChangeResult?> UnlockAsync(int setId, IReadOnlyCollection<int>? beatmapIds, int actorId, CancellationToken cancellationToken = default)
    {
        var set = await _repository.GetBySetAsync(setId, cancellationToken).ConfigureAwait(false);
        if (set.Count == 0)
            return null;

        var targets = Targets(set, beatmapIds).Where(b => b.StatusFrozen).ToList();
        if (targets.Count == 0)
            return new BeatmapStatusChangeResult(setId, UnlockName, []);

        // Overrides go first: the fetch would otherwise hand back the staff status.
        var overrides = targets.Select(b => (b.Id, Status: _beatmaps.GetStatusOverride(b.Id))).ToList();
        foreach (var beatmap in targets)
            _beatmaps.ClearStatusOverride(beatmap.Id);

        var fromApi = (await _beatmaps.FetchSetAsync(setId, cancellationToken).ConfigureAwait(false)).ToDictionary(b => b.Id);
        var changed = new List<Beatmap>();
        foreach (var beatmap in targets)
        {
            if (!fromApi.TryGetValue(beatmap.Id, out var api))
                continue;
            await _repository.UnfreezeStatusAsync(beatmap.Id, cancellationToken).ConfigureAwait(false);
            await _repository.UpsertAsync(api, cancellationToken).ConfigureAwait(false);
            changed.Add(api);
        }

        // Difficulties osu! no longer has keep their staff status.
        foreach (var (id, status) in overrides.Where(o => !fromApi.ContainsKey(o.Id) && o.Status is not null))
            _beatmaps.SetStatusOverride(id, status!.Value);

        return await FinishAsync(set, changed, UnlockName, actorId, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The name the panel sends (and the log shows) for giving difficulties bancho's status back.</summary>
    public const string UnlockName = "bancho";

    /// <summary>Ranked or approved: the statuses whose leaderboards start empty.</summary>
    private static bool IsRanked(RankedStatus status) => status is RankedStatus.Ranked or RankedStatus.Approved;

    private static List<Beatmap> Targets(IReadOnlyList<Beatmap> set, IReadOnlyCollection<int>? beatmapIds) =>
        beatmapIds is { Count: > 0 } ? set.Where(b => beatmapIds.Contains(b.Id)).ToList() : set.ToList();

    /// <summary>After a change: recalculate scores, re-index the set on the website and log it.</summary>
    private async Task<BeatmapStatusChangeResult> FinishAsync(IReadOnlyList<Beatmap> set, List<Beatmap> changed, string name, int actorId, List<Beatmap>? newlyRanked, CancellationToken cancellationToken)
    {
        var setId = set[0].SetId;
        if (changed.Count == 0)
            return new BeatmapStatusChangeResult(setId, name, []);

        // Newly ranked difficulties start with an empty leaderboard (the old server's rule). Not
        // cancelled with the request (osu-web gives up after 30 s): a clear stopped halfway would
        // leave the game and the website disagreeing, with the players' stats not redone.
        LeaderboardClearResult? cleared = null;
        if (_clearScoresOnRank && _recalculation is not null && newlyRanked is { Count: > 0 })
            cleared = await _recalculation.ClearLeaderboardsAsync(newlyRanked, CancellationToken.None).ConfigureAwait(false);

        ScoreRecalculationResult? scores = null;
        if (_recalculation is not null)
            scores = await _recalculation.RecalculateAsync(changed, cancellationToken).ConfigureAwait(false);
        await ReindexAsync(setId, cancellationToken).ConfigureAwait(false);

        var which = changed.Count == set.Count ? "all difficulties" : string.Join(", ", changed.Select(b => $"[{b.Version}]"));
        var target = name == UnlockName ? "bancho's status" : name;
        var summary = string.Create(CultureInfo.InvariantCulture, $"{set[0].Artist} - {set[0].Title}: {which} → {target}");
        if (scores is { Changed: > 0 })
            summary += string.Create(CultureInfo.InvariantCulture, $" ({scores.Changed} score(s) recalculated, {scores.Players} player(s))");
        if (cleared is { Scores: > 0 } or { WebsiteScores: > 0 })
            summary += string.Create(CultureInfo.InvariantCulture, $" (leaderboard cleared: {cleared.Scores} score(s), {cleared.WebsiteScores} on the website)");
        await _log.AddAsync(new StaffLogEntry
        {
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            ActorId = actorId,
            Action = LogAction,
            TargetId = setId,
            Summary = summary.Length > 512 ? summary[..512] : summary,
            Result = name,
        }, cancellationToken).ConfigureAwait(false);

        return new BeatmapStatusChangeResult(setId, name, changed.Select(b => b.Version).ToList(), scores, cleared);
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
